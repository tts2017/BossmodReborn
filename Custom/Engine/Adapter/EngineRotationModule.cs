using BossMod.Autorotation.Engine;

namespace BossMod.Autorotation;

// Base class connecting a RotationEngine to BMR. A concrete job module supplies:
//  - its JobDefinition and weights (usually static, shared by all instances),
//  - ReadJobState: game state -> EngineState (gauges, statuses, job cooldowns; GCD / animation lock / combo / targets are filled here),
//  - optionally ActionFor / TargetFor when the definition's ActionId or the default targeting is not enough.
// No module is registered from this file: the class is abstract.
public abstract class EngineRotationModule(RotationModuleManager manager, Actor player, RotationEngine engine) : RotationModule(manager, player)
{
    protected readonly RotationEngine Engine = engine;
    protected JobDefinition Job => Engine.Job;
    public EngineDecision LastDecision { get; private set; }
    // diagnostics (harnesses): called for every decision that ran a search
    public static Action<string>? DebugTrace;

    // raid-buff window multiplier used when only the party's buff timings are known
    protected virtual float RaidBuffMultiplier => 1.05f;
    protected virtual float RaidBuffInterval => 120;
    protected virtual float RaidBuffDuration => 20;
    protected virtual float RaidBuffFirst => 7.8f;
    protected virtual float TransientLossThreshold => 8.5f; // target losses shorter than this are ignored (RPR / BLM practice)
    // harnesses without party buffs: no assumed 2-minute raid-buff cycle when the party reports none
    public static bool AssumeRaidBuffCycle = true;
    // how long a missing / untargetable target is assumed to stay away when nothing forecasts its return
    protected virtual float UnknownDowntime => 2.5f;

    protected abstract void ReadJobState(ref EngineState s, Actor? primaryTarget);

    protected virtual ActionID ActionFor(SkillDef skill) => new(ActionType.Spell, skill.ActionId);
    protected virtual Actor? TargetFor(SkillDef skill, Actor? primaryTarget) => skill.RequiresTarget ? primaryTarget : Player;
    protected virtual byte CountTargets(Actor? primaryTarget) => 1;

    // priority targets within `radius` of the player measured hitbox to hitbox (the Akechi modules' convention)
    protected byte CountTargetsByHitbox(float radius)
    {
        var count = 0;
        foreach (var target in Hints.PriorityTargetsSpan)
            if (target.Actor is { } actor && !actor.IsDeadOrDestroyed && Player.DistanceToHitbox(actor) <= radius)
                ++count;
        return (byte)Math.Clamp(count, 1, 255);
    }

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        var s = EngineState.Create(Job);
        s.GcdReadyAt = GCD;
        s.AnimLockAt = 0; // the queue only runs us when an action could be requested; animation lock is handled by ActionManagerEx
        s.Targets = CountTargets(primaryTarget);
        ReadJobState(ref s, primaryTarget);
        ApplyCastInProgress(ref s);

        var tl = BuildTimeline(isMoving, primaryTarget);
        if (_epoch == default)
            _epoch = World.CurrentTime;
        var now = (float)(World.CurrentTime - _epoch).TotalSeconds; // small numbers: float keeps millisecond precision
        // opener (the pull, or the return after a downtime): every cooldown is up at once and a full search is several times its usual size;
        // from the first GCD after an idle stretch it gets a larger total budget for a few seconds (the usual slice per frame, the rest
        // just before the answer is acted on)
        if (GCD > 0)
        {
            if (now - _lastGcdRunning > IdleSeconds)
                _openerStart = now;
            _lastGcdRunning = now;
        }
        Engine.BudgetScale = now - _openerStart < OpenerSeconds || now - _lastGcdRunning > IdleSeconds ? OpenerBudgetScale : InBurst(s, tl) ? BurstBudgetScale : 1;
        var d = Engine.Decide(s, tl, now);
        // an unfinished search while something could be pressed (the queue would act on its answer this frame): it uses the rest of its
        // budget now. Holding the ability for later frames instead cost more than the occasional longer frame
        if (d.Partial && CanPressNow())
            d = Engine.FinishPending(now);
        LastDecision = d;
        if (DebugTrace != null && !d.Reused)
            DebugTrace(FormattableString.Invariant($"[engine {Job.Name}] t={now:f2} gcd={GCD:f2} skill={(d.Skill >= 0 ? Job.Skills[d.Skill].Name : "wait")} nextGcd={(d.NextGcd >= 0 ? Job.Skills[d.NextGcd].Name : "wait")} at={d.ExecuteAt:f2} depth={d.Depth} nodes={d.Nodes} hyst={d.Hysteresis} partial={d.Partial} combo={(s.ComboSkill != EngineLimits.NoCombo ? Job.Skills[s.ComboSkill].Name : "-")}/{World.Client.ComboState.Action}:{World.Client.ComboState.Remaining:f1} targets={s.Targets} legalGcds={string.Join(",", LegalGcds(s, tl))}"));

        if (d.NextGcd >= 0)
        {
            var gcd = Job.Skills[d.NextGcd];
            Hints.ActionsToExecute.Push(ActionFor(gcd), TargetFor(gcd, primaryTarget), ActionQueue.Priority.High + 2, castTime: gcd.CastTime);
        }
        if (d.Skill >= 0 && !Job.Skills[d.Skill].IsGcd)
        {
            var ogcd = Job.Skills[d.Skill];
            Hints.ActionsToExecute.Push(ActionFor(ogcd), TargetFor(ogcd, primaryTarget), ActionQueue.Priority.Low + 1, delay: d.ExecuteAt);
        }
    }

    // something can be pressed now: no animation lock or cast (the GCD when it is up, or an ability, late weaves included)
    private bool CanPressNow() => World.Client.AnimationLock <= Imminent && Player.CastInfo == null;
    private const float Imminent = 0.05f;

    // Mid-cast the gauges and statuses do not include the spell being cast yet: plan from the end of the cast with its effects
    // applied (the variant of that action legal in the current state), so the next GCD is not chosen from a stale state.
    private void ApplyCastInProgress(ref EngineState s)
    {
        if (Player.CastInfo is not { } cast || cast.RemainingTime <= 0 || cast.Action.Type != ActionType.Spell)
            return;
        var tl = EngineTimeline.Open();
        foreach (var sk in Job.Skills)
        {
            if (sk.ActionId != cast.Action.ID || !Simulator.IsLegalIgnoringCooldown(Job, s, tl, sk))
                continue;
            var gcd = s.GcdReadyAt;
            Simulator.Execute(Job, ref s, tl, sk, _castCtx);
            s.GcdReadyAt = gcd;
            s.AnimLockAt = cast.RemainingTime + 0.1f + Job.Latency;
            return;
        }
        s.AnimLockAt = cast.RemainingTime + 0.1f + Job.Latency;
    }
    private readonly EvalContext _castCtx = new();

    private IEnumerable<string> LegalGcds(EngineState s, EngineTimeline tl)
    {
        var at = s;
        foreach (var sk in Job.Skills)
            if (sk.IsGcd && Simulator.IsLegal(Job, at, tl, sk))
                yield return sk.Name;
    }

    private DateTime _epoch;
    private float _lastGcdRunning = float.NegativeInfinity, _openerStart = float.NegativeInfinity;
    private const float IdleSeconds = 5, OpenerSeconds = 5, OpenerBudgetScale = 8, BurstBudgetScale = 3;

    private int[]? _buffCooldowns;
    // cooldown groups of the skills that apply one of the job's damage buffs
    private int[] BuffCooldownGroups()
    {
        var groups = new List<int>();
        foreach (var sk in Job.Skills)
            if (sk.Cooldown >= 0 && !groups.Contains(sk.Cooldown))
                foreach (var e in sk.Effects)
                    if (e.Kind == EffectKind.StatusApply && Job.Statuses[e.Index].DamageMultiplier > 1)
                    {
                        groups.Add(sk.Cooldown);
                        break;
                    }
        return [.. groups];
    }

    // a damage buff of the job's own is up or comes off cooldown within a few seconds, or a raid buff is up or starts within a few seconds:
    // the decisions that matter most, with the most cooldowns to order (a larger total budget, spent like the opener's)
    private bool InBurst(in EngineState s, in EngineTimeline tl)
    {
        for (var i = 0; i < Job.Statuses.Length; ++i)
            if (Job.Statuses[i].DamageMultiplier > 1 && s.StatusLeft[i] > 0)
                return true;
        _buffCooldowns ??= BuffCooldownGroups();
        foreach (var c in _buffCooldowns)
            if (s.Charges[c] > 0 || s.CdReadyIn[c] <= 3)
                return true;
        return tl.BuffMultiplier(0) > 1 || tl.BuffMultiplier(3) > 1;
    }

    private readonly List<TargetableWindow> _windowScratch = [];
    private readonly List<DamageBuffWindowSnapshot> _buffScratch = [];
    private int _timelineHash;
    private int _timelineVersion;

    protected EngineTimeline BuildTimeline(bool isMoving, Actor? primaryTarget = null)
    {
        var tl = EngineTimeline.Open();
        // no attackable target right now: treat the next seconds as downtime (nothing that needs a target, e.g. Soulsow instead)
        if (primaryTarget == null || !primaryTarget.IsTargetable)
            tl.AddDowntime(0, UnknownDowntime);

        var fight = Hints.FightRemaining;
        if (fight.Known)
            tl.FightEndIn = fight.RemainingSeconds;

        // target loss: disengage forecast, then the planner's targetable windows
        var dis = Hints.Disengage;
        if (dis.TargetLossIn < float.MaxValue && dis.TargetReturnIn - dis.TargetLossIn >= TransientLossThreshold)
            tl.AddDowntime(dis.TargetLossIn, dis.TargetReturnIn);
        if (Manager.Planner is { } plan)
        {
            plan.EstimateTargetableWindows(60, _windowScratch);
            foreach (var w in _windowScratch)
                if (!w.Targetable && w.EndIn - w.StartIn >= TransientLossThreshold)
                    tl.AddDowntime(w.StartIn, w.EndIn);
        }
        if (dis.ForcedMoveIn < float.MaxValue)
            tl.AddNoCast(dis.ForcedMoveIn, dis.ForcedMoveIn + MathF.Max(0.5f, dis.ForcedMoveFor));
        if (isMoving)
            tl.AddNoCast(0, 0.5f);

        // raid buffs: active / upcoming party buffs, else the 2-minute cycle from combat start
        Bossmods.RaidCooldowns.DamageBuffWindows(Player, null, _buffScratch);
        var windows = _buffScratch;
        foreach (var w in windows)
            tl.AddBuff(w.StartsIn, w.StartsIn + w.Duration, 1 + (RaidBuffMultiplier - 1) * w.Weight);
        if (windows.Count == 0 && AssumeRaidBuffCycle && Manager.CombatStart != default)
        {
            var elapsed = (float)(World.CurrentTime - Manager.CombatStart).TotalSeconds;
            for (var t = RaidBuffFirst; t < elapsed + 360 && tl.NumBuffs < EngineLimits.MaxWindows; t += RaidBuffInterval)
                if (t + RaidBuffDuration > elapsed)
                    tl.AddBuff(t - elapsed, t + RaidBuffDuration - elapsed, RaidBuffMultiplier);
        }

        // bump the version only when the window set changes (windows sliding with time do not count)
        var h = tl.NumDowntime * 7 + tl.NumBuffs * 131 + tl.NumNoCast * 1031 + (float.IsInfinity(tl.FightEndIn) ? 1 : 0);
        for (var i = 0; i < tl.NumBuffs; ++i)
            h = h * 31 + (int)MathF.Round(tl.Buffs[i].Multiplier * 100);
        if (h != _timelineHash)
        {
            _timelineHash = h;
            ++_timelineVersion;
        }
        tl.Version = _timelineVersion;
        return tl;
    }

    // helpers for ReadJobState
    protected void ReadCooldown(ref EngineState s, int cdIndex, ActionID action)
    {
        var def = ActionDefinitions.Instance[action];
        if (def == null)
            return;
        var cd = Job.Cooldowns[cdIndex];
        var readyIn = def.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions);
        var full = def.MainCooldownGroup >= 0 ? World.Client.Cooldowns[def.MainCooldownGroup] : default;
        var charges = cd.MaxCharges;
        if (cd.MaxCharges > 1 && full.Total > 0)
        {
            var missing = (full.Total - full.Elapsed) / cd.Recast;
            charges = Math.Clamp(cd.MaxCharges - (int)MathF.Ceiling(missing - 1e-3f), 0, cd.MaxCharges);
            s.CdReadyIn[cdIndex] = charges < cd.MaxCharges ? (full.Total - full.Elapsed) - (cd.MaxCharges - charges - 1) * cd.Recast : 0;
        }
        else
        {
            charges = readyIn > Simulator.CdEpsilon ? 0 : 1;
            s.CdReadyIn[cdIndex] = charges > 0 ? 0 : readyIn;
        }
        s.Charges[cdIndex] = (byte)charges;
    }

    protected void ReadStatus(ref EngineState s, int statusIndex, Actor? actor, uint sid, bool fromPlayer = true)
    {
        if (actor == null)
            return;
        var st = fromPlayer ? StatusDetails(actor, sid, Player.InstanceID) : StatusDetails(actor, sid, actor.InstanceID);
        if (st.Left > 0)
        {
            s.StatusLeft[statusIndex] = st.Left;
            s.StatusStacks[statusIndex] = (byte)Math.Max(1, st.Stacks);
        }
    }

    protected void ReadCombo(ref EngineState s)
    {
        var combo = World.Client.ComboState;
        if (combo.Remaining <= 0)
            return;
        for (var i = 0; i < Job.Skills.Length; ++i)
        {
            if (Job.Skills[i].ActionId == combo.Action)
            {
                s.ComboSkill = (byte)i;
                s.ComboLeft = combo.Remaining;
                return;
            }
        }
    }
}
