using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace BossMod;

// Allocation-free, in-process MCH value planner. The rotation remains responsible for legal candidates and burst safety.
public static class MchRealtimeValuePlanner
{
    private const int MinimumLevel = 76;
    private const int HorizonGCDs = 3;
    private const float MaxPriorityLoss = 10f;
    private const float MinimumValueGain = 5f;

    // cached config node: the planner runs every frame, so don't look it up each call
    private static CustomConfig? _config;

    private static readonly float[] ValueWeights =
    [
        1f,    // simulated potency
        2f,    // retained heat
        5f,    // retained battery
        0.35f, // Drill cooldown progress
        0.35f, // Air Anchor cooldown progress
        0.35f, // Chain Saw cooldown progress
        0.50f, // retained Excavator Ready
        0.10f, // retained combo value
        -6f,   // gauge overcap loss
        4f,    // existing queue-priority preference
    ];

    private struct SimulationState
    {
        public int Heat;
        public int Battery;
        public int GaugeOvercap;
        public uint ComboAction;
        public float DrillReadyIn;
        public float AirAnchorReadyIn;
        public float ChainSawReadyIn;
        public bool ExcavatorReady;
        public bool DrillUnlocked;
        public bool AirAnchorUnlocked;
        public bool ChainSawUnlocked;
        public bool ExcavatorUnlocked;
        public int ChainSawTargets;
        public int ExcavatorTargets;
        public float GCDLength;
    }

    public static ActionQueue.Entry Select(ActionQueue queue, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, float instantAnimLockDelay, bool allowDismount)
    {
        _config ??= Service.Config.Get<CustomConfig>();
        if (!_config.EnableLocalRotationAI)
            return baseline;
        if (player.Class != Class.MCH || player.Level < MinimumLevel || !player.InCombat || !baseline.Action || baseline.Action.Type != ActionType.Spell)
            return baseline;
        if (baseline.Manual || baseline.Force || baseline.Expire != float.MaxValue)
            return baseline;
        if (!SupportedAction(baseline.Action) || baseline.Target == null || baseline.Target.IsAlly || HasProtectedAction(queue) || HasProtectedState(player) || BurstAnchorSoon(ws, cooldowns))
            return baseline;

        var baselineDefinition = ActionDefinitions.Instance[baseline.Action];
        if (baselineDefinition == null || !baselineDefinition.IsGCD)
            return baseline;

        var initial = CaptureState(ws, player, cooldowns, hints, baseline.Target);
        var baselineStart = StartDelay(baseline, baselineDefinition, ws, cooldowns, animationLock);
        var baselineDuration = Duration(baselineDefinition, instantAnimLockDelay);
        Advance(ref initial, baselineStart);
        var baselineValue = Evaluate(baseline, baseline, initial, player.Level);
        var best = baseline;
        var bestValue = baselineValue;
        var eligibleActions = 1;

        foreach (var candidate in queue.Entries)
        {
            if (candidate.Action == baseline.Action || !Eligible(candidate, baseline, baselineDefinition, baselineStart, baselineDuration, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount, queue))
                continue;

            ++eligibleActions;
            var value = Evaluate(candidate, baseline, initial, player.Level);
            if (value > bestValue + MinimumValueGain)
            {
                best = candidate;
                bestValue = value;
            }
        }

        return eligibleActions >= 2 ? best : baseline;
    }

    private static bool Eligible(ActionQueue.Entry candidate, ActionQueue.Entry baseline, ActionDefinition baselineDefinition, float baselineStart, float baselineDuration, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, float instantAnimLockDelay, bool allowDismount, ActionQueue queue)
    {
        if (!SupportedAction(candidate.Action) || candidate.Target != baseline.Target || candidate.Manual || candidate.Force || candidate.Expire != float.MaxValue)
            return false;
        if (candidate.Priority < baseline.Priority - MaxPriorityLoss || candidate.Priority > baseline.Priority)
            return false;

        var definition = ActionDefinitions.Instance[candidate.Action];
        if (definition == null || !definition.IsGCD || definition.IsGCD != baselineDefinition.IsGCD || !definition.IsUnlocked(ws, player) || candidate.CastTime > hints.MaxCastTime)
            return false;
        if (StartDelay(candidate, definition, ws, cooldowns, animationLock) > baselineStart + 0.01f || Duration(definition, instantAnimLockDelay) > baselineDuration + 0.01f)
            return false;
        return queue.CanExecuteEx(candidate, definition, ws, player, hints, allowDismount);
    }

    private static SimulationState CaptureState(WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, AIHints hints, Actor target)
    {
        var gauge = ws.Client.GetGauge<MachinistGauge>();
        var direction = player.DirectionTo(target);
        // Targeted AoEs always hit their main target, even at the edge of the rectangle.
        var chainSawTargets = 1;
        var excavatorTargets = 1;
        foreach (var enemy in hints.PriorityTargetsSpan)
        {
            if (enemy.Actor == target)
                continue;
            if (AIHints.TargetInAOERect(enemy.Actor, player.Position, direction, 25, 2))
                ++chainSawTargets;
            if (AIHints.TargetInAOECircle(enemy.Actor, target.Position, 5))
                ++excavatorTargets;
        }
        foreach (var enemy in hints.ForbiddenTargetsSpan)
        {
            if (enemy.Actor == target || AIHints.TargetInAOERect(enemy.Actor, player.Position, direction, 25, 2))
                chainSawTargets = 0;
            if (enemy.Actor == target || AIHints.TargetInAOECircle(enemy.Actor, target.Position, 5))
                excavatorTargets = 0;
        }
        return new()
        {
            Heat = gauge.Heat,
            Battery = gauge.Battery,
            ComboAction = ws.Client.ComboState.Action,
            DrillReadyIn = ReadyIn(MCH.AID.Drill, ws, cooldowns),
            AirAnchorReadyIn = ReadyIn(MCH.AID.AirAnchor, ws, cooldowns),
            ChainSawReadyIn = ReadyIn(MCH.AID.ChainSaw, ws, cooldowns),
            DrillUnlocked = Unlocked(MCH.AID.Drill, ws, player),
            AirAnchorUnlocked = Unlocked(MCH.AID.AirAnchor, ws, player),
            ChainSawUnlocked = Unlocked(MCH.AID.ChainSaw, ws, player),
            ExcavatorUnlocked = Unlocked(MCH.AID.Excavator, ws, player),
            ChainSawTargets = chainSawTargets,
            ExcavatorTargets = excavatorTargets,
            GCDLength = ActionSpeed.GCDRounded(ws.Client.PlayerStats.SkillSpeed, ws.Client.PlayerStats.Haste, player.Level),
        };
    }

    private static float Evaluate(ActionQueue.Entry candidate, ActionQueue.Entry baseline, SimulationState initial, int level)
    {
        var state = initial;
        var toolPotency = level >= 94 ? 660f : 620f;
        var potency = ApplyAction(ref state, (MCH.AID)candidate.Action.ID, level);
        Advance(ref state, state.GCDLength);

        for (var step = 1; step < HorizonGCDs; ++step)
        {
            var next = SelectFutureAction(state, level);
            potency += ApplyAction(ref state, next, level);
            Advance(ref state, state.GCDLength);
        }

        Span<float> features = stackalloc float[ValueWeights.Length];
        features[0] = potency;
        features[1] = state.Heat;
        features[2] = state.Battery;
        features[3] = CooldownProgressValue(state.DrillReadyIn, 20f, toolPotency);
        features[4] = CooldownProgressValue(state.AirAnchorReadyIn, 40f, toolPotency);
        features[5] = CooldownProgressValue(state.ChainSawReadyIn, 60f, AOEValue(toolPotency, 0.75f, state.ChainSawTargets));
        features[6] = state.ExcavatorReady ? AOEValue(toolPotency, 0.75f, state.ExcavatorTargets) : 0;
        features[7] = Potency(NextComboAction(state.ComboAction), state.ComboAction, level, state.ChainSawTargets, state.ExcavatorTargets);
        features[8] = state.GaugeOvercap;
        features[9] = candidate.Priority - baseline.Priority;

        var value = 0f;
        for (var index = 0; index < features.Length; ++index)
            value += features[index] * ValueWeights[index];
        return value;
    }

    private static MCH.AID SelectFutureAction(SimulationState state, int level)
    {
        var bestAction = NextComboAction(state.ComboAction);
        var bestValue = Potency(bestAction, state.ComboAction, level, state.ChainSawTargets, state.ExcavatorTargets);

        if (state.ExcavatorReady && state.ExcavatorUnlocked && state.ExcavatorTargets > 0)
            Consider(MCH.AID.Excavator, ref bestAction, ref bestValue, state, level);
        if (state.DrillUnlocked && state.DrillReadyIn <= 0)
            Consider(MCH.AID.Drill, ref bestAction, ref bestValue, state, level);
        if (state.AirAnchorUnlocked && state.AirAnchorReadyIn <= 0)
            Consider(MCH.AID.AirAnchor, ref bestAction, ref bestValue, state, level);
        if (state.ChainSawUnlocked && state.ChainSawReadyIn <= 0 && !state.ExcavatorReady && state.ChainSawTargets > 0)
            Consider(MCH.AID.ChainSaw, ref bestAction, ref bestValue, state, level);

        return bestAction;
    }

    private static void Consider(MCH.AID action, ref MCH.AID bestAction, ref float bestValue, SimulationState state, int level)
    {
        var value = Potency(action, state.ComboAction, level, state.ChainSawTargets, state.ExcavatorTargets) + CooldownOpportunity(action, state, level);
        if (value > bestValue)
        {
            bestAction = action;
            bestValue = value;
        }
    }

    private static float ApplyAction(ref SimulationState state, MCH.AID action, int level)
    {
        var potency = Potency(action, state.ComboAction, level, state.ChainSawTargets, state.ExcavatorTargets);
        switch (action)
        {
            case MCH.AID.Drill:
                state.DrillReadyIn = 20f;
                break;
            case MCH.AID.AirAnchor:
                state.AirAnchorReadyIn = 40f;
                AddBattery(ref state, 20);
                break;
            case MCH.AID.ChainSaw:
                state.ChainSawReadyIn = 60f;
                AddBattery(ref state, 20);
                state.ExcavatorReady = state.ExcavatorUnlocked;
                break;
            case MCH.AID.Excavator:
                AddBattery(ref state, 20);
                state.ExcavatorReady = false;
                break;
            case MCH.AID.HeatedSplitShot:
                AddHeat(ref state, 5);
                state.ComboAction = (uint)MCH.AID.HeatedSplitShot;
                break;
            case MCH.AID.HeatedSlugShot:
                if (state.ComboAction == (uint)MCH.AID.HeatedSplitShot)
                    AddHeat(ref state, 5);
                state.ComboAction = (uint)MCH.AID.HeatedSlugShot;
                break;
            case MCH.AID.HeatedCleanShot:
                if (state.ComboAction == (uint)MCH.AID.HeatedSlugShot)
                {
                    AddHeat(ref state, 5);
                    AddBattery(ref state, 10);
                }
                state.ComboAction = 0;
                break;
        }
        return potency;
    }

    private static void Advance(ref SimulationState state, float seconds)
    {
        state.DrillReadyIn = Math.Max(0, state.DrillReadyIn - seconds);
        state.AirAnchorReadyIn = Math.Max(0, state.AirAnchorReadyIn - seconds);
        state.ChainSawReadyIn = Math.Max(0, state.ChainSawReadyIn - seconds);
    }

    private static void AddHeat(ref SimulationState state, int amount)
    {
        var next = state.Heat + amount;
        state.GaugeOvercap += Math.Max(0, next - 100);
        state.Heat = Math.Min(100, next);
    }

    private static void AddBattery(ref SimulationState state, int amount)
    {
        var next = state.Battery + amount;
        state.GaugeOvercap += Math.Max(0, next - 100);
        state.Battery = Math.Min(100, next);
    }

    private static float Potency(MCH.AID action, uint comboAction, int level, int chainSawTargets, int excavatorTargets)
        => action switch
        {
            MCH.AID.Drill or MCH.AID.AirAnchor => level >= 94 ? 660f : 620f,
            MCH.AID.ChainSaw => AOEValue(level >= 94 ? 660f : 620f, 0.75f, chainSawTargets),
            MCH.AID.Excavator => AOEValue(level >= 94 ? 660f : 620f, 0.75f, excavatorTargets),
            MCH.AID.HeatedSplitShot => level >= 94 ? 220f : 200f,
            MCH.AID.HeatedSlugShot => comboAction == (uint)MCH.AID.HeatedSplitShot ? 320f : level >= 94 ? 140f : 120f,
            MCH.AID.HeatedCleanShot => comboAction == (uint)MCH.AID.HeatedSlugShot ? 420f : level >= 94 ? 160f : 120f,
            _ => 0f,
        };

    private static MCH.AID NextComboAction(uint comboAction)
        => comboAction switch
        {
            (uint)MCH.AID.HeatedSplitShot => MCH.AID.HeatedSlugShot,
            (uint)MCH.AID.HeatedSlugShot => MCH.AID.HeatedCleanShot,
            _ => MCH.AID.HeatedSplitShot,
        };

    private static float CooldownOpportunity(MCH.AID action, SimulationState state, int level)
    {
        var toolPotency = level >= 94 ? 660f : 620f;
        return action switch
        {
            MCH.AID.Drill => toolPotency / 20f,
            MCH.AID.AirAnchor => toolPotency / 40f,
            MCH.AID.ChainSaw => AOEValue(toolPotency, 0.75f, state.ChainSawTargets) / 60f,
            _ => 0f,
        };
    }

    private static float CooldownProgressValue(float readyIn, float recast, float potency)
        => Math.Clamp((recast - readyIn) / recast, 0, 1) * potency;

    private static float AOEValue(float firstTargetPotency, float remainingTargetMultiplier, int enemyCount)
        => enemyCount > 0 ? firstTargetPotency * (1 + (enemyCount - 1) * remainingTargetMultiplier) : 0;

    private static bool SupportedAction(ActionID action)
        => action.Type == ActionType.Spell && (MCH.AID)action.ID is MCH.AID.Drill or MCH.AID.AirAnchor;

    private static bool HasProtectedAction(ActionQueue queue)
    {
        foreach (var entry in queue.Entries)
        {
            if (entry.Priority <= ActionQueue.Priority.Minimal || entry.Action.Type != ActionType.Spell)
                continue;
            if ((MCH.AID)entry.Action.ID is MCH.AID.Wildfire or MCH.AID.Detonator or MCH.AID.Hypercharge or MCH.AID.BarrelStabilizer or MCH.AID.RookAutoturret or MCH.AID.AutomatonQueen or MCH.AID.Excavator or MCH.AID.FullMetalField)
                return true;
        }
        return false;
    }

    private static bool HasProtectedState(Actor player)
        => player.FindStatus(MCH.SID.Reassembled) != null
            || player.FindStatus(MCH.SID.WildfirePlayer) != null
            || player.FindStatus(MCH.SID.Hypercharged) != null
            || player.FindStatus(MCH.SID.ExcavatorReady) != null
            || player.FindStatus(MCH.SID.FullMetalMachinist) != null;

    private static bool BurstAnchorSoon(WorldState ws, ReadOnlySpan<Cooldown> cooldowns)
        => ReadyIn(MCH.AID.Wildfire, ws, cooldowns) <= 10f || ReadyIn(MCH.AID.BarrelStabilizer, ws, cooldowns) <= 10f;

    private static float ReadyIn(MCH.AID action, WorldState ws, ReadOnlySpan<Cooldown> cooldowns)
        => ActionDefinitions.Instance[ActionID.MakeSpell(action)]?.ReadyIn(cooldowns, ws.Client.DutyActions) ?? float.MaxValue;

    private static bool Unlocked(MCH.AID action, WorldState ws, Actor player)
        => ActionDefinitions.Instance[ActionID.MakeSpell(action)]?.IsUnlocked(ws, player) == true;

    private static float StartDelay(ActionQueue.Entry entry, ActionDefinition definition, WorldState ws, ReadOnlySpan<Cooldown> cooldowns, float animationLock)
        => Math.Max(Math.Max(entry.Delay, animationLock), definition.ReadyIn(cooldowns, ws.Client.DutyActions));

    private static float Duration(ActionDefinition definition, float instantAnimLockDelay)
        => definition.CastTime > 0 ? definition.CastTime + definition.CastAnimLock : definition.InstantAnimLock + instantAnimLockDelay;
}
