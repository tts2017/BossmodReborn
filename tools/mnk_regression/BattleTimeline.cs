namespace MnkRegression;

public sealed class BattleTimeline
{
    private readonly BattleScenario _scenario;
    private readonly HashSet<BattleEvent> _appliedSnapshots = [];

    public BattleTimeline(BattleScenario scenario)
    {
        _scenario = scenario;
    }

    public void Apply(BattleState state, double time)
    {
        state.Time = time;
        state.CombatTimer = time;
        state.InCombat = true;
        state.Targetable = true;
        state.HaveTarget = true;
        state.CanMelee = true;
        state.TargetCount = _scenario.InitialState.TargetCount;
        state.NumAOETargets = _scenario.InitialState.NumAOETargets;
        state.NumMeleeAOETargets = _scenario.InitialState.NumMeleeAOETargets;
        state.EncounterHint = _scenario.StrategyProfile.EncounterHint;
        state.LookAwayActive = false;
        state.ForbiddenZoneActive = false;
        state.SafeMeleeAvailable = true;
        state.ThunderclapSafe = true;
        state.PredictedDamage.Clear();
        state.EstimatedDowntimeStart = null;
        state.EstimatedPhaseEnd = null;
        state.EstimatedFightEnd = null;

        foreach (var ev in _scenario.Events)
        {
            if (ev.Kind == BattleEventKind.PredictedDamageEvent)
            {
                if (ev.Start >= time && ev.Start <= time + 10.0)
                    state.PredictedDamage.Add(new(Math.Round(ev.Start, 3), ev.DamageType, ev.SelfTargeted));
                continue;
            }

            if (ev.Kind is BattleEventKind.PhaseEndEstimateEvent or BattleEventKind.FightEndEstimateEvent or BattleEventKind.DowntimeEstimateEvent)
            {
                if (ev.Start >= time)
                {
                    if (ev.Kind == BattleEventKind.PhaseEndEstimateEvent)
                        state.EstimatedPhaseEnd = ev.Start;
                    else if (ev.Kind == BattleEventKind.FightEndEstimateEvent)
                        state.EstimatedFightEnd = ev.Start;
                    else
                        state.EstimatedDowntimeStart = ev.Start;
                }
                continue;
            }

            if (ev.Kind is BattleEventKind.GaugeSnapshotEvent or BattleEventKind.CooldownSnapshotEvent)
            {
                if (time + _scenario.TickInterval / 2 >= ev.Start && _appliedSnapshots.Add(ev))
                    ApplySnapshot(state, ev);
                continue;
            }

            if (!ev.ActiveAt(time))
                continue;

            switch (ev.Kind)
            {
                case BattleEventKind.TargetableWindow:
                    state.Targetable = ev.Targetable ?? state.Targetable;
                    state.HaveTarget = ev.HaveTarget ?? state.HaveTarget;
                    if (!state.Targetable || !state.HaveTarget)
                        state.CanMelee = false;
                    break;
                case BattleEventKind.MeleeWindow:
                    state.CanMelee = ev.CanMelee ?? state.CanMelee;
                    break;
                case BattleEventKind.TargetCountWindow:
                    state.TargetCount = ev.TargetCount ?? state.TargetCount;
                    state.NumAOETargets = ev.NumAOETargets ?? state.NumAOETargets;
                    state.NumMeleeAOETargets = ev.NumMeleeAOETargets ?? state.NumMeleeAOETargets;
                    break;
                case BattleEventKind.EncounterHintWindow:
                    state.EncounterHint = ev.EncounterHint ?? state.EncounterHint;
                    break;
                case BattleEventKind.LookAwayWindow:
                    state.LookAwayActive = ev.Active ?? true;
                    break;
                case BattleEventKind.ForbiddenZoneWindow:
                    state.ForbiddenZoneActive = ev.Active ?? true;
                    state.SafeMeleeAvailable = false;
                    break;
                case BattleEventKind.ThunderclapSafetyWindow:
                    state.ThunderclapSafe = ev.ThunderclapSafe ?? state.ThunderclapSafe;
                    break;
            }
        }
    }

    private static void ApplySnapshot(BattleState state, BattleEvent ev)
    {
        if (ev.Gauge is { } gauge)
        {
            if (gauge.Chakra is { } chakra)
                state.PlayerGauge.Chakra = chakra;
            if (gauge.ChakraProgress is { } chakraProgress)
                state.PlayerGauge.ChakraProgress = chakraProgress;
            if (gauge.BeastChakra != null)
            {
                state.PlayerGauge.BeastChakra.Clear();
                state.PlayerGauge.BeastChakra.AddRange(gauge.BeastChakra);
            }
            if (gauge.LunarNadi is { } lunar)
                state.PlayerGauge.LunarNadi = lunar;
            if (gauge.SolarNadi is { } solar)
                state.PlayerGauge.SolarNadi = solar;
            if (gauge.CurrentForm is { } form)
                state.PlayerGauge.CurrentForm = form;
            if (gauge.OpoFury is { } opoFury)
                state.PlayerGauge.OpoFury = opoFury;
            if (gauge.RaptorFury is { } raptorFury)
                state.PlayerGauge.RaptorFury = raptorFury;
            if (gauge.CoeurlFury is { } coeurlFury)
                state.PlayerGauge.CoeurlFury = coeurlFury;
            if (gauge.PerfectBalancePlan is { } pbPlan)
                state.PlayerGauge.PerfectBalancePlan = pbPlan;
            if (gauge.FormlessFistLeft is { } formless)
                state.PlayerGauge.FormlessFistLeft = formless;
            if (gauge.PerfectBalanceLeft is { } pbLeft)
                state.PlayerGauge.PerfectBalanceLeft = pbLeft;
            if (gauge.PerfectBalanceStacks is { } pbStacks)
                state.PlayerGauge.PerfectBalanceStacks = pbStacks;
            if (gauge.RiddleOfFireLeft is { } rof)
                state.PlayerGauge.RiddleOfFireLeft = rof;
            if (gauge.BrotherhoodLeft is { } bh)
                state.PlayerGauge.BrotherhoodLeft = bh;
            if (gauge.RiddleOfWindLeft is { } row)
                state.PlayerGauge.RiddleOfWindLeft = row;
            if (gauge.PotionLeft is { } potion)
                state.PlayerGauge.PotionLeft = potion;
            if (gauge.RiddleOfEarthLeft is { } roe)
                state.PlayerGauge.RiddleOfEarthLeft = roe;
            if (gauge.EarthsReplyLeft is { } earthsReply)
                state.PlayerGauge.EarthsReplyLeft = earthsReply;
            if (gauge.WindsReplyLeft is { } windsReply)
                state.PlayerGauge.WindsReplyLeft = windsReply;
            if (gauge.FiresReplyLeft is { } firesReply)
                state.PlayerGauge.FiresReplyLeft = firesReply;
            if (gauge.BlitzLeft is { } blitz)
                state.PlayerGauge.BlitzLeft = blitz;
        }

        if (ev.Cooldowns is { } cooldowns)
        {
            if (cooldowns.PerfectBalanceCharges is { } pbCharges)
                state.Cooldowns.PerfectBalance.Charges = pbCharges;
            if (cooldowns.PerfectBalanceReadyIn is { } pbReady)
                state.Cooldowns.PerfectBalance.ReadyIn = pbReady;
            if (cooldowns.RiddleOfFireReadyIn is { } rofReady)
                state.Cooldowns.RiddleOfFire.ReadyIn = rofReady;
            if (cooldowns.BrotherhoodReadyIn is { } bhReady)
                state.Cooldowns.Brotherhood.ReadyIn = bhReady;
            if (cooldowns.RiddleOfWindReadyIn is { } rowReady)
                state.Cooldowns.RiddleOfWind.ReadyIn = rowReady;
            if (cooldowns.RiddleOfEarthReadyIn is { } roeReady)
                state.Cooldowns.RiddleOfEarth.ReadyIn = roeReady;
            if (cooldowns.PotionReadyIn is { } potionReady)
                state.Cooldowns.Potion.ReadyIn = potionReady;
            if (cooldowns.ThunderclapCharges is { } thunderclapCharges)
                state.Cooldowns.Thunderclap.Charges = thunderclapCharges;
            if (cooldowns.ThunderclapReadyIn is { } thunderclapReady)
                state.Cooldowns.Thunderclap.ReadyIn = thunderclapReady;
            if (cooldowns.TrueNorthCharges is { } trueNorthCharges)
                state.Cooldowns.TrueNorth.Charges = trueNorthCharges;
            if (cooldowns.TrueNorthReadyIn is { } trueNorthReady)
                state.Cooldowns.TrueNorth.ReadyIn = trueNorthReady;
        }
    }
}
