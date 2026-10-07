namespace BossMod.Autorotation;

// A GCD, or a chain of GCDs, worth pressing before a long target loss. StepPotency holds the potency gained in each consecutive GCD slot
// the candidate occupies (one entry for a single action); a chain cut short by the loss still counts the steps that fit. ReadyIn is when
// the first step can be pressed; RecoveredIn is when the charge spent now is back (ChargeCapIn + Cooldown); ExtraTime is time the candidate
// needs inside its slot before the hit lands (NIN mudras).
public readonly record struct WindDownCandidate(ActionID Action, float[] StepPotency, float ReadyIn, float RecoveredIn, float ExtraTime = 0f);

public readonly record struct WindDownSlot(int Candidate, int Steps); // Candidate -1 = the normal rotation keeps the slot

// Plans the GCD slots left before a long target loss (spec rule 2b): the set of candidates that maximises potency over the normal filler,
// candidates whose recast is not back by the return (+1 GCD) excluded, laid out lowest potency first so the strongest lands in the last slot.
public static class WindDown
{
    public const int MaxSlots = 8;
    public const int MaxCandidates = 10;
    public const float LastSlotMargin = 0.5f;

    // cheap per-frame gate: a known long loss close enough for its slots to matter, so rotations only build candidates when this holds
    public static bool Active(in MechanicForecast m, float gcdLength)
        => m.Enabled && m.ReturnKnown && !m.DowntimeNow && gcdLength > 0f && m.TargetLossIn <= (MaxSlots + 1) * gcdLength;

    public static int SlotsBeforeLoss(in MechanicForecast m, float gcdRemaining, float gcdLength, float lockTime)
    {
        if (!m.Enabled || !m.ReturnKnown || m.DowntimeNow || gcdLength <= 0f)
            return 0;
        var slots = 0;
        while (slots < MaxSlots && gcdRemaining + slots * gcdLength + lockTime <= m.TargetLossIn - LastSlotMargin)
            ++slots;
        return slots;
    }

    public static int SelectGcd(ReadOnlySpan<WindDownCandidate> candidates, in MechanicForecast m, float gcdRemaining, float gcdLength, float lockTime, float fillerPotency)
    {
        Span<WindDownSlot> plan = stackalloc WindDownSlot[MaxSlots];
        return PlanInto(plan, candidates, m, gcdRemaining, gcdLength, lockTime, fillerPotency) > 0 ? plan[0].Candidate : -1;
    }

    public static WindDownSlot[] Plan(ReadOnlySpan<WindDownCandidate> candidates, in MechanicForecast m, float gcdRemaining, float gcdLength, float lockTime, float fillerPotency)
    {
        Span<WindDownSlot> plan = stackalloc WindDownSlot[MaxSlots];
        return plan[..PlanInto(plan, candidates, m, gcdRemaining, gcdLength, lockTime, fillerPotency)].ToArray();
    }

    // The DP tables, kept per thread: the rotations plan every frame while a loss is near, and the tables grow as (slots + 1) << candidates.
    [ThreadStatic] private static float[]? _bestScratch;
    [ThreadStatic] private static (int Candidate, int Steps)[]? _choiceScratch;

    private static bool Fits(in WindDownCandidate cand, int k, float gcdRemaining, float gcdLength, float lockTime, float loss, float ret)
    {
        var t = gcdRemaining + k * gcdLength;
        return cand.ReadyIn <= t + 0.05f
            && t + cand.RecoveredIn <= ret + gcdLength
            && t + cand.ExtraTime + lockTime <= loss - LastSlotMargin;
    }

    private static float Gain(in WindDownCandidate cand, int steps, float fillerPotency)
    {
        var sum = 0f;
        for (var i = 0; i < steps; ++i)
            sum += cand.StepPotency[i] - fillerPotency;
        return sum;
    }

    // Writes the slot plan (at most MaxSlots entries, one per candidate or filler slot) into result and returns its length.
    private static int PlanInto(Span<WindDownSlot> result, ReadOnlySpan<WindDownCandidate> candidates, in MechanicForecast m, float gcdRemaining, float gcdLength, float lockTime, float fillerPotency)
    {
        var slots = SlotsBeforeLoss(m, gcdRemaining, gcdLength, lockTime);
        var n = Math.Min(candidates.Length, MaxCandidates);
        if (slots == 0 || n == 0)
            return 0;

        var cands = candidates[..n];
        var loss = m.TargetLossIn;
        var ret = m.TargetReturnIn;

        // DP over (slot, used candidates): best total gain from slot k onwards, stored at [k * masks + mask]
        var masks = 1 << n;
        var size = (slots + 1) * masks;
        if (_bestScratch == null || _choiceScratch == null || _bestScratch.Length < size)
        {
            _bestScratch = new float[size];
            _choiceScratch = new (int, int)[size];
        }
        var best = _bestScratch;
        var choice = _choiceScratch;
        Array.Clear(best, slots * masks, masks); // nothing is gained past the last slot; every earlier row is written before it is read
        for (var k = slots - 1; k >= 0; --k)
        {
            for (var mask = masks - 1; mask >= 0; --mask)
            {
                var bestGain = best[(k + 1) * masks + mask];
                (int Candidate, int Steps) bestChoice = (-1, 1);
                for (var c = 0; c < n; ++c)
                {
                    if ((mask & (1 << c)) != 0 || !Fits(cands[c], k, gcdRemaining, gcdLength, lockTime, loss, ret))
                        continue;
                    var steps = Math.Min(cands[c].StepPotency.Length, slots - k);
                    var gain = Gain(cands[c], steps, fillerPotency) + best[(k + steps) * masks + (mask | (1 << c))];
                    if (gain > bestGain + 0.001f)
                    {
                        bestGain = gain;
                        bestChoice = (c, steps);
                    }
                }
                best[k * masks + mask] = bestGain;
                choice[k * masks + mask] = bestChoice;
            }
        }

        // DP layout
        var dpCount = 0;
        Span<(int Candidate, int Steps)> picked = stackalloc (int, int)[MaxSlots];
        var pickedCount = 0;
        for (int k = 0, mask = 0; k < slots;)
        {
            var (c, steps) = choice[k * masks + mask];
            if (c >= 0)
            {
                result[dpCount++] = new(c, steps);
                picked[pickedCount++] = (c, steps);
                mask |= 1 << c;
                k += steps;
            }
            else
            {
                result[dpCount++] = new(-1, 1);
                ++k;
            }
        }

        // preferred layout: normal rotation first, picked candidates packed at the end, lowest potency per slot first (strongest last);
        // a chain cut short by the loss stays last whatever its per-slot value, since nothing can be pressed between its steps.
        // Each candidate is picked at most once, so the order is total (ties fall to the candidate index) and an insertion sort gives
        // the same result as any other sort.
        for (var i = 1; i < pickedCount; ++i)
        {
            var item = picked[i];
            var j = i - 1;
            for (; j >= 0 && ComparePicked(picked[j], item, cands, fillerPotency) > 0; --j)
                picked[j + 1] = picked[j];
            picked[j + 1] = item;
        }
        var used = 0;
        for (var i = 0; i < pickedCount; ++i)
            used += picked[i].Steps;
        Span<WindDownSlot> packed = stackalloc WindDownSlot[MaxSlots];
        var packedCount = 0;
        for (var k = 0; k < slots - used; ++k)
            packed[packedCount++] = new(-1, 1);
        var pos = slots - used;
        var valid = true;
        for (var i = 0; i < pickedCount; ++i)
        {
            var p = picked[i];
            valid &= Fits(cands[p.Candidate], pos, gcdRemaining, gcdLength, lockTime, loss, ret);
            packed[packedCount++] = new(p.Candidate, p.Steps);
            pos += p.Steps;
        }
        if (!valid)
            return dpCount;
        packed[..packedCount].CopyTo(result);
        return packedCount;
    }

    private static int ComparePicked((int Candidate, int Steps) a, (int Candidate, int Steps) b, ReadOnlySpan<WindDownCandidate> cands, float fillerPotency)
    {
        var cutA = a.Steps < cands[a.Candidate].StepPotency.Length;
        var cutB = b.Steps < cands[b.Candidate].StepPotency.Length;
        if (cutA != cutB)
            return cutA ? 1 : -1;
        var pa = Gain(cands[a.Candidate], a.Steps, fillerPotency) / a.Steps;
        var pb = Gain(cands[b.Candidate], b.Steps, fillerPotency) / b.Steps;
        return pa != pb ? pa.CompareTo(pb) : a.Candidate.CompareTo(b.Candidate);
    }

    public static int SelectOgcd(ReadOnlySpan<WindDownCandidate> candidates, in MechanicForecast m, float gcdLength)
    {
        if (!m.Enabled || !m.ReturnKnown || m.DowntimeNow || gcdLength <= 0f)
            return -1;
        var weaveSlots = 2 * (int)MathF.Floor(Math.Max(0f, m.TargetLossIn - LastSlotMargin) / gcdLength) + 1;
        Span<int> eligible = stackalloc int[candidates.Length];
        var count = 0;
        for (var i = 0; i < candidates.Length; ++i)
        {
            var c = candidates[i];
            if (c.ReadyIn <= m.TargetLossIn - LastSlotMargin && c.RecoveredIn <= m.TargetReturnIn + gcdLength)
                eligible[count++] = i;
        }
        if (count < weaveSlots)
            return -1; // enough weave slots left for everything: the normal rotation decides

        // the strongest candidates take the remaining slots; among them the weakest ready one goes first, so the strongest is last
        var kept = eligible[..count];
        for (var i = 1; i < kept.Length; ++i)
            for (var j = i; j > 0 && candidates[kept[j]].StepPotency[0] > candidates[kept[j - 1]].StepPotency[0]; --j)
                (kept[j], kept[j - 1]) = (kept[j - 1], kept[j]);
        var pick = -1;
        foreach (var i in kept[..weaveSlots])
            if (candidates[i].ReadyIn <= 0.05f && (pick < 0 || candidates[i].StepPotency[0] < candidates[pick].StepPotency[0]))
                pick = i;
        return pick;
    }
}
