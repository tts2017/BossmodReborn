using System;
using System.Runtime.CompilerServices;

namespace BossMod.Autorotation.Engine;

public static class EngineLimits
{
    public const int MaxGauges = 8;
    public const int MaxStatuses = 16;
    public const int MaxCooldowns = 16;
    public const int MaxSkills = 64;
    public const int MaxWindows = 8;
    public const byte NoCombo = 0xFF;
}

[InlineArray(EngineLimits.MaxGauges)]
public struct GaugeArray { private short _e; }

[InlineArray(EngineLimits.MaxStatuses)]
public struct StatusTimeArray { private float _e; }

[InlineArray(EngineLimits.MaxStatuses)]
public struct StatusStackArray { private byte _e; }

[InlineArray(EngineLimits.MaxCooldowns)]
public struct CooldownTimeArray { private float _e; }

[InlineArray(EngineLimits.MaxCooldowns)]
public struct ChargeArray { private byte _e; }

// Complete, fixed-size simulation state. Copied by value during search; never allocates.
// Times: Time, GcdReadyAt and AnimLockAt are absolute seconds since the decision started (decision start = 0);
// status / cooldown / combo timers are remaining seconds at Time.
public struct EngineState
{
    public float Time;
    public float GcdReadyAt;
    public float AnimLockAt;
    public byte ComboSkill;
    public float ComboLeft;
    public byte Targets;
    public GaugeArray Gauges;
    public StatusTimeArray StatusLeft;
    public StatusStackArray StatusStacks;
    public CooldownTimeArray CdReadyIn; // time until the next charge comes back (0 when at max charges)
    public ChargeArray Charges;

    public static EngineState Create(JobDefinition job)
    {
        var s = new EngineState { ComboSkill = EngineLimits.NoCombo, Targets = 1 };
        for (var i = 0; i < job.Cooldowns.Length; ++i)
            s.Charges[i] = (byte)job.Cooldowns[i].MaxCharges;
        return s;
    }

    public readonly bool HasStatus(int index) => StatusLeft[index] > 0;

    // FNV-1a over the decision-relevant fields, times quantized to 0.05 s (relative to Time where it matters).
    public readonly ulong Hash(JobDefinition job)
    {
        var h = 14695981039346656037UL;
        h = Mix(h, Q(Time));
        h = Mix(h, Q(MathF.Max(0, GcdReadyAt - Time)));
        h = Mix(h, Q(MathF.Max(0, AnimLockAt - Time)));
        h = Mix(h, ComboSkill);
        h = Mix(h, ComboSkill == EngineLimits.NoCombo ? 0 : Q(ComboLeft));
        h = Mix(h, Targets);
        for (var i = 0; i < job.Gauges.Length; ++i)
            h = Mix(h, (uint)(ushort)Gauges[i]);
        for (var i = 0; i < job.Statuses.Length; ++i)
        {
            h = Mix(h, Q(StatusLeft[i]));
            h = Mix(h, StatusStacks[i]);
        }
        for (var i = 0; i < job.Cooldowns.Length; ++i)
        {
            h = Mix(h, Q(CdReadyIn[i]));
            h = Mix(h, Charges[i]);
        }
        return h;
    }

    private static uint Q(float seconds) => (uint)(int)MathF.Round(seconds * 20);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong h, uint v)
    {
        h ^= v;
        return h * 1099511628211UL;
    }
}

// External information about the fight, relative to the decision start. Fixed size.
public struct EngineTimeline
{
    [InlineArray(EngineLimits.MaxWindows)]
    public struct WindowArray { private Window _e; }

    public struct Window
    {
        public float Start;
        public float End;
        public float Multiplier; // only for buff windows
    }

    public float FightEndIn; // float.PositiveInfinity if unknown
    public WindowArray Downtime;
    public int NumDowntime;
    public WindowArray Buffs;
    public int NumBuffs;
    public WindowArray NoCast;
    public int NumNoCast;
    public int Version; // bump when the window set changes; triggers an upper-tier recompute

    public static EngineTimeline Open() => new() { FightEndIn = float.PositiveInfinity };

    public void AddDowntime(float start, float end) { if (NumDowntime < EngineLimits.MaxWindows) Downtime[NumDowntime++] = new() { Start = start, End = end }; }
    public void AddBuff(float start, float end, float multiplier) { if (NumBuffs < EngineLimits.MaxWindows) Buffs[NumBuffs++] = new() { Start = start, End = end, Multiplier = multiplier }; }
    public void AddNoCast(float start, float end) { if (NumNoCast < EngineLimits.MaxWindows) NoCast[NumNoCast++] = new() { Start = start, End = end }; }

    public readonly bool InDowntime(float t)
    {
        for (var i = 0; i < NumDowntime; ++i)
            if (t >= Downtime[i].Start && t < Downtime[i].End)
                return true;
        return false;
    }

    public readonly bool OverlapsDowntime(float start, float end)
    {
        for (var i = 0; i < NumDowntime; ++i)
            if (start < Downtime[i].End && end > Downtime[i].Start)
                return true;
        return false;
    }

    public readonly bool OverlapsNoCast(float start, float end)
    {
        for (var i = 0; i < NumNoCast; ++i)
            if (start < NoCast[i].End && end > NoCast[i].Start)
                return true;
        return false;
    }

    // end of the downtime containing t (t itself if not in downtime)
    public readonly float DowntimeEnd(float t)
    {
        for (var i = 0; i < NumDowntime; ++i)
            if (t >= Downtime[i].Start && t < Downtime[i].End)
                return Downtime[i].End;
        return t;
    }

    public readonly float BuffMultiplier(float t)
    {
        var m = 1f;
        for (var i = 0; i < NumBuffs; ++i)
            if (t >= Buffs[i].Start && t < Buffs[i].End)
                m *= Buffs[i].Multiplier;
        return m;
    }

    public readonly float MaxBuffMultiplier()
    {
        var m = 1f;
        for (var i = 0; i < NumBuffs; ++i)
            m *= MathF.Max(1, Buffs[i].Multiplier);
        return m;
    }

    // average raid multiplier over [start, end)
    public readonly float AverageBuffMultiplier(float start, float end)
    {
        if (end <= start)
            return BuffMultiplier(start);
        var extra = 0f;
        for (var i = 0; i < NumBuffs; ++i)
        {
            var overlap = MathF.Min(end, Buffs[i].End) - MathF.Max(start, Buffs[i].Start);
            if (overlap > 0)
                extra += overlap * (Buffs[i].Multiplier - 1);
        }
        return 1 + extra / (end - start);
    }
}
