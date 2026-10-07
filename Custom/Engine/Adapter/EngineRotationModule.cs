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

    protected abstract void ReadJobState(ref EngineState s, Actor? primaryTarget);

    protected virtual ActionID ActionFor(SkillDef skill) => new(ActionType.Spell, skill.ActionId);
    protected virtual Actor? TargetFor(SkillDef skill, Actor? primaryTarget) => skill.RequiresTarget ? primaryTarget : Player;
    protected virtual byte CountTargets(Actor? primaryTarget) => 1;

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        var s = EngineState.Create(Job);
        s.GcdReadyAt = GCD;
        s.AnimLockAt = 0; // the queue only runs us when an action could be requested; animation lock is handled by ActionManagerEx
        s.Targets = CountTargets(primaryTarget);
        ReadJobState(ref s, primaryTarget);

        var tl = BuildTimeline(isMoving, primaryTarget);
        if (_epoch == default)
            _epoch = World.CurrentTime;
        var now = (float)(World.CurrentTime - _epoch).TotalSeconds; // small numbers: float keeps millisecond precision
        var d = Engine.Decide(s, tl, now);
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

    private IEnumerable<string> LegalGcds(EngineState s, EngineTimeline tl)
    {
        var at = s;
        foreach (var sk in Job.Skills)
            if (sk.IsGcd && Simulator.IsLegal(Job, at, tl, sk))
                yield return sk.Name;
    }

    private DateTime _epoch;
    private readonly List<TargetableWindow> _windowScratch = [];
    private readonly List<DamageBuffWindowSnapshot> _buffScratch = [];
    private int _timelineHash;
    private int _timelineVersion;

    protected EngineTimeline BuildTimeline(bool isMoving, Actor? primaryTarget = null)
    {
        var tl = EngineTimeline.Open();
        // no attackable target right now: treat the next seconds as downtime (nothing that needs a target, e.g. Soulsow instead)
        if (primaryTarget == null || !primaryTarget.IsTargetable)
            tl.AddDowntime(0, 2.5f);

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
        if (windows.Count == 0 && Manager.CombatStart != default)
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
            charges = readyIn > 0 ? 0 : 1;
            s.CdReadyIn[cdIndex] = readyIn;
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
