namespace BossMod.Autorotation;

// APIs the old fork added to upstream classes (PlanExecution, RaidCooldowns, RotationModule, RotationModuleManager, ActionManagerEx)
// for its rotation modules, provided here without editing upstream. Private upstream state is read through UnsafeAccessor:
// if upstream renames one of those members the build still succeeds but the call throws at runtime, so re-check after syncing.

// Scores candidates for Basexan.FindBetterTargetByScorer (formerly RotationModule). Accept filters every candidate except the initial target.
public interface ITargetScorer<P>
{
    P Score(Actor target);
    bool Accept(AIHints.Enemy enemy);
}

// formerly PlanExecution.TargetableWindow
public readonly record struct TargetableWindow(float StartIn, float EndIn, bool Targetable);

// formerly RaidCooldowns.DamageCooldownSnapshot / DamageBuffWindowSnapshot
public readonly record struct DamageCooldownSnapshot(ActionID Action, float AvailableIn);
public readonly record struct DamageBuffWindowSnapshot(ActionID Action, float StartsIn, float Duration, float Weight);

public static class CustomRotationApi
{
    // --- private upstream state ---
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "States")]
    private static extern ref Dictionary<uint, PlanExecution.StateData> PlanStates(PlanExecution plan);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Pull")]
    private static extern ref PlanExecution.StateData PlanPull(PlanExecution plan);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ForcedTargets")]
    private static extern ref List<PlanExecution.EntryData> PlanForcedTargets(PlanExecution plan);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_damageCooldowns")]
    private static extern ref List<(int Slot, ActionID Action, DateTime AvailableAt)> RaidDamageCooldowns(RaidCooldowns cds);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_ws")]
    private static extern ref WorldState RaidWorld(RaidCooldowns cds);

    private sealed class LastRequest
    {
        public (DateTime Time, ClientActionRequest Data) Value;
        public EventSubscription? Subscription;
    }
    private static readonly ConditionalWeakTable<RotationModuleManager, LastRequest> _lastRequests = new();
    private static readonly ConditionalWeakTable<PlanExecution, List<PlanExecution.StateData>> _statesByEnterTime = new();

    extension(PlanExecution plan)
    {
        public bool CurrentStateOverdue()
        {
            var s = plan.FindCurrentStateData();
            return !ReferenceEquals(s, PlanPull(plan)) && plan.Module.StateMachine.TimeSinceTransition > s.Duration + PlanExecution.OverdueGraceSeconds;
        }

        // Targetability windows on the concrete planner timeline, relative to the current virtual time.
        public List<TargetableWindow> EstimateTargetableWindows(float horizon)
        {
            List<TargetableWindow> res = [];
            plan.EstimateTargetableWindows(horizon, res);
            return res;
        }

        // Same windows written into the caller's list (cleared first), for rotations that sample them every frame.
        public void EstimateTargetableWindows(float horizon, List<TargetableWindow> res)
        {
            res.Clear();
            if (horizon <= 0)
                return;

            var current = plan.FindCurrentStateData();
            var now = plan.GetVirtualTime(current);
            var end = now + horizon;
            var states = _statesByEnterTime.GetValue(plan, static p => [.. PlanStates(p).Values.OrderBy(s => s.EnterTime)]);
            List<PlanExecution.EntryData> scratch = [];
            foreach (var s in states)
            {
                if (!IntersectBranchRange(current.BranchID, current.NumBranches, s.BranchID, s.NumBranches))
                    continue;
                var startAbs = Math.Max(now, s.EnterTime);
                var endAbs = Math.Min(end, s.EnterTime + s.Duration);
                if (endAbs <= startAbs)
                    continue;
                AddTargetableStateWindows(plan, res, scratch, now, startAbs, endAbs, s);
            }
        }
    }

    extension(RaidCooldowns cds)
    {
        public DamageCooldownSnapshot[] DamageCooldowns()
        {
            List<DamageCooldownSnapshot> result = [];
            cds.DamageCooldowns(result);
            return [.. result];
        }

        // Same snapshot written into the caller's list (cleared first), for rotations that read it every frame.
        public void DamageCooldowns(List<DamageCooldownSnapshot> result)
        {
            result.Clear();
            var now = RaidWorld(cds).CurrentTime;
            foreach (var cd in RaidDamageCooldowns(cds))
                result.Add(new(cd.Action, Math.Max(0, (float)(cd.AvailableAt - now).TotalSeconds)));
        }

        public DamageBuffWindowSnapshot[] DamageBuffWindows(Actor player, Actor? target)
        {
            var now = RaidWorld(cds).CurrentTime;
            var windows = new List<DamageBuffWindowSnapshot>();
            foreach (var status in player.Statuses)
                if (DamageBuffAction(status.ID) is var action && action)
                    AddDamageBuffWindow(windows, action, 0, (float)(status.ExpireAt - now).TotalSeconds);
            if (target is { } t)
                foreach (var status in t.Statuses)
                    if (DamageDebuffAction(status.ID) is var action && action)
                        AddDamageBuffWindow(windows, action, 0, (float)(status.ExpireAt - now).TotalSeconds);
            foreach (var cooldown in RaidDamageCooldowns(cds))
                AddDamageBuffWindow(windows, cooldown.Action, (float)(cooldown.AvailableAt - now).TotalSeconds, cooldown.Action.ID == (uint)AST.AID.Divination ? 15 : 20);
            return [.. windows];
        }
    }

    extension(RotationModuleManager manager)
    {
        // the last action request the client sent (formerly a field updated in RotationModuleManager.OnActionRequested)
        public (DateTime Time, ClientActionRequest Data) LastActionRequest
        {
            get
            {
                var data = _lastRequests.GetValue(manager, static _ => new());
                if (data.Subscription == null)
                {
                    var ws = manager.WorldState;
                    data.Subscription = ws.Client.ActionRequested.Subscribe(op => data.Value = (ws.CurrentTime, op.Request));
                }
                return data.Value;
            }
        }
    }

    extension(ActionManagerEx)
    {
        // null outside the game (replay analysis, offline harness); rotation modules that need live client queries must cope with that
        public static ActionManagerEx? Instance => CustomPlugin.Instance?.ActionManager;
    }

    private static bool IntersectBranchRange(int branchID1, int numBranches1, int branchID2, int numBranches2)
        => branchID1 < branchID2 + numBranches2 && branchID2 < branchID1 + numBranches1;

    private static void AddTargetableWindow(List<TargetableWindow> res, float startIn, float endIn, bool targetable)
    {
        startIn = Math.Max(0, startIn);
        if (endIn <= startIn)
            return;
        if (res.Count > 0 && Math.Abs(res[^1].EndIn - startIn) <= 0.01f && res[^1].Targetable == targetable)
        {
            res[^1] = res[^1] with { EndIn = Math.Max(res[^1].EndIn, endIn) };
            return;
        }
        res.Add(new(startIn, endIn, targetable));
    }

    private static void AddTargetableStateWindows(PlanExecution plan, List<TargetableWindow> res, List<PlanExecution.EntryData> scratch, float now, float startAbs, float endAbs, PlanExecution.StateData s)
    {
        if (!s.Downtime.Active)
        {
            AddTargetableWindow(res, startAbs - now, endAbs - now, targetable: true);
            return;
        }

        var cursor = startAbs;
        scratch.Clear();
        foreach (var e in PlanForcedTargets(plan))
            if (e.Value is StrategyValueTrack track && IsHostileTarget(track.Target) && e.IntersectBranchRange(s.BranchID, s.NumBranches) && e.WindowEnd > startAbs && e.WindowStart < endAbs)
                scratch.Add(e);
        scratch.Sort(static (a, b) => a.WindowStart.CompareTo(b.WindowStart));
        foreach (var target in scratch)
        {
            var targetStart = Math.Max(startAbs, target.WindowStart);
            var targetEnd = Math.Min(endAbs, target.WindowEnd);
            if (targetEnd <= targetStart)
                continue;
            AddTargetableWindow(res, cursor - now, targetStart - now, targetable: false);
            AddTargetableWindow(res, targetStart - now, targetEnd - now, targetable: true);
            cursor = Math.Max(cursor, targetEnd);
        }
        AddTargetableWindow(res, cursor - now, endAbs - now, targetable: false);
    }

    private static bool IsHostileTarget(StrategyTarget target)
        => target is StrategyTarget.Automatic or StrategyTarget.EnemyWithHighestPriority or StrategyTarget.EnemyByOID;

    private static void AddDamageBuffWindow(List<DamageBuffWindowSnapshot> windows, ActionID action, float startsIn, float duration)
    {
        if (duration <= 0)
            return;
        windows.Add(new(action, Math.Max(0, startsIn), duration, DamageBuffWeight(action.ID)));
    }

    private static ActionID DamageBuffAction(uint statusID) => statusID switch
    {
        (uint)AST.SID.Divination => ActionID.MakeSpell(AST.AID.Divination),
        (uint)DRG.SID.BattleLitany => ActionID.MakeSpell(DRG.AID.BattleLitany),
        (uint)RPR.SID.ArcaneCircle => ActionID.MakeSpell(RPR.AID.ArcaneCircle),
        (uint)MNK.SID.Brotherhood => ActionID.MakeSpell(MNK.AID.Brotherhood),
        (uint)BRD.SID.BattleVoice => ActionID.MakeSpell(BRD.AID.BattleVoice),
        (uint)DNC.SID.TechnicalFinish => ActionID.MakeSpell(DNC.AID.QuadrupleTechnicalFinish),
        (uint)SMN.SID.SearingLight => ActionID.MakeSpell(SMN.AID.SearingLight),
        (uint)RDM.SID.Embolden => ActionID.MakeSpell(RDM.AID.Embolden),
        (uint)PCT.SID.StarryMuse => ActionID.MakeSpell(PCT.AID.StarryMuse),
        _ => default
    };

    private static ActionID DamageDebuffAction(uint statusID) => statusID switch
    {
        (uint)SCH.SID.ChainStratagem => ActionID.MakeSpell(SCH.AID.ChainStratagem),
        (uint)NIN.SID.Dokumori or (uint)NIN.SID.VulnerabilityUp => ActionID.MakeSpell(NIN.AID.Dokumori),
        _ => default
    };

    private static float DamageBuffWeight(uint actionID) => actionID switch
    {
        (uint)SCH.AID.ChainStratagem or (uint)NIN.AID.Dokumori or (uint)NIN.AID.Mug => 1.1f,
        _ => 1
    };
}
