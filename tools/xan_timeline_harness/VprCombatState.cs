using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BossMod;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using AID = BossMod.VPR.AID;
using SID = BossMod.VPR.SID;

namespace XanTimelineHarness;

// Deterministic action-result simulation for Viper (7.5), in the style of SamCombatState. Decisions come from production xan VPR.cs;
// this only models what the client does with them: the dual-wield and AoE combos with their Honed / venom chains, Hunter's Instinct and
// Swiftscaled (whose 15% haste shortens every weaponskill recast, including the 3.0s coils and the 2.0s Generations), the Vicewinder /
// Vicepit twinblade combos, Rattling Coils, the Serpent Offerings gauge, Reawaken with its Anguine Tribute, Generations, Legacies and
// Ouroboros, and the Serpent's Tail / Twinfang / Twinblood follow-ups (each lost when the next weaponskill replaces it). Presses are
// scored when they land with Hunter's Instinct, no damage rolls, so two runs are byte-identical.
internal sealed class VprCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float> onExecuted)
{
    public int OfferingOvercap { get; private set; }
    public int CoilOvercap { get; private set; }
    public int ProcsLost { get; private set; }
    public int FollowUpsLost { get; private set; }
    public int NoInstinctGCDs { get; private set; }
    public int Reawakens { get; private set; }
    public int Generations { get; private set; }
    public int UncoiledFuries { get; private set; }
    public int Coils { get; private set; }
    public int PositionalsMissed { get; private set; }
    public List<string> Events { get; } = [];
    public DateTime BaseTime { get; init; }

    private int _offering;
    private int _coil;
    private int _anguine;
    private DreadCombo _dread;
    private SerpentCombo _serpent;
    private bool _twinfangReady;
    private bool _twinbloodReady;
    // the GCD that opened the current twin window (Hunter's / Swiftskin's Coil or Den, Uncoiled Fury): decides which venom a twin grants
    private AID _twinSource;
    private AID _lastWeaponskill;
    private bool _hadInstinct;
    private uint _sequence;

    private const float ComboDuration = 30f;
    private const float BuffDuration = 40f;
    private const float HonedDuration = 60f;
    private const float VenomDuration = 60f;
    private const float TwinVenomDuration = 30f;
    private const float PoisedDuration = 60f;
    private const float ReadyDuration = 30f;
    private const float ReawakenedDuration = 30f;
    private const float TrueNorthDuration = 10f;
    private const float InstinctMultiplier = 1.10f;
    private const float MedicatedMultiplier = 1.06f;
    private const uint MedicatedStatus = 49;

    // SerpentCombo past FourthLegacy: the twin windows the gauge reports (xan VPR.cs reads 7 / 8 / 9 as coil / den / Uncoiled Fury)
    private const SerpentCombo TwinsAfterCoil = (SerpentCombo)7;
    private const SerpentCombo TwinsAfterDen = (SerpentCombo)8;
    private const SerpentCombo TwinsAfterFury = (SerpentCombo)9;
    private const SerpentCombo NoSerpentCombo = 0;

    public int Offering => _offering;
    public int Coil => _coil;
    public int Anguine => _anguine;
    public DreadCombo Dread => _dread;
    public bool EnforceRangeAndMovement { get; set; }
    public bool Moving { get; set; }
    // VPR_POSITIONALS=geometry scores a positional bonus only when True North is up or the player really stands on that side (the harness
    // keeps the player still, at the target's flank): the worst case for True North, where its value and cost are both visible
    public static readonly bool GeometricPositionals = Environment.GetEnvironmentVariable("VPR_POSITIONALS") == "geometry";

    private int CoilMax => player.Level >= 88 ? 3 : player.Level >= 82 ? 2 : 0;
    private int AnguineMax => player.Level >= 96 ? 5 : 4;

    public void Advance()
    {
        foreach (var actor in world.Actors)
            for (var i = 0; i < actor.Statuses.Length; ++i)
                if (actor.Statuses[i].ID != 0 && actor.Statuses[i].ExpireAt <= world.CurrentTime)
                {
                    var expired = (SID)actor.Statuses[i].ID;
                    world.Execute(new ActorState.OpStatus(actor.InstanceID, i, default));
                    if (actor != player)
                        continue;
                    if (expired == SID.Reawakened && _anguine > 0)
                    {
                        _anguine = 0;
                        ++ProcsLost;
                        Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{expired}"));
                    }
                    else if (expired is SID.ReawakenReady or SID.HuntersVenom or SID.SwiftskinsVenom or SID.FellhuntersVenom or SID.FellskinsVenom
                        or SID.PoisedForTwinfang or SID.PoisedForTwinblood)
                    {
                        ++ProcsLost;
                        Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{expired}"));
                    }
                }

        SyncHaste();
        PublishGauge();
    }

    public void ExecuteBestAction(AIHints hints)
    {
        if (player.IsDead)
            return;

        if (ClientReject.Enabled)
            hints.ActionsToExecute.Entries.RemoveAll(entry => !CanExecute(entry, jobRules: false));
        else
            hints.ActionsToExecute.Entries.RemoveAll(entry => !CanExecute(entry));
        SearchControl.FilterSuppressed(hints.ActionsToExecute.Entries, world); // oracle-search: actions held by the search stay out of the queue
        var entry = hints.ActionsToExecute.FindBest(world, player, world.Client.Cooldowns, world.Client.AnimationLock, hints, frameStep, true);
        var definition = ActionDefinitions.Instance[entry.Action];
        if (entry.Action.ID == 0 || definition == null
            || MathF.Max(entry.Delay, MathF.Max(world.Client.AnimationLock, definition.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions))) > 0.001f)
            return;
        if (Irregular.Refuses("vpr", entry, definition, world, player)) // --irregular lockout statuses, refused after the pick like the client
            return;
        if (ClientReject.Enabled && !CanExecute(entry))
        {
            ClientReject.Record("vpr", entry.Action, world.CurrentTime, player);
            return;
        }
        if (SearchControl.Active) // oracle-search: the search may swap the module pick for an alternative or hold it back
        {
            if (SearchControl.Decide(hints.ActionsToExecute, entry, world, player, hints, frameStep, e => CanExecute(e), DescribeForSearch) is not { } decided)
                return;
            entry = decided;
            definition = ActionDefinitions.Instance[entry.Action]!;
        }

        if (definition.IsGCD)
            world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, GCDLength(definition)))]));
        StartCooldown(definition);
        Complete(entry, definition);
    }

    // oracle-search: emulator state shown next to each decision of the deviation log
    private string DescribeForSearch() => FormattableString.Invariant($"offering={_offering} coil={_coil} anguine={_anguine}");

    // burst study (BurstControl.cs): the state shown next to a cycle and the emulator's own readiness rules for a starter
    public string BurstState() => DescribeForSearch();
    public bool BurstFeasible(ActionQueue.Entry entry) => CanExecute(entry);

    private bool CanExecute(ActionQueue.Entry entry, bool jobRules = true)
    {
        if (entry.Target != null && entry.Target != player && (entry.Target.IsDead || !entry.Target.IsTargetable))
            return false;
        var definition = ActionDefinitions.Instance[entry.Action];
        if (EnforceRangeAndMovement && definition != null && entry.Target != null && entry.Target != player && definition.Range > 0
            && player.DistanceToHitbox(entry.Target) > definition.Range)
            return false;
        if (!jobRules)
            return true;
        if (entry.Action.Type != ActionType.Spell)
            return true;

        var last = (AID)world.Client.ComboState.Action;
        var reawakened = Left(SID.Reawakened) > 0;
        var step = AnguineMax - _anguine;
        return (AID)entry.Action.ID switch
        {
            AID.SteelFangs or AID.ReavingFangs or AID.SteelMaw or AID.ReavingMaw => !reawakened,
            AID.HuntersSting or AID.SwiftskinsSting => !reawakened && last is AID.SteelFangs or AID.ReavingFangs,
            AID.FlankstingStrike or AID.FlanksbaneFang => !reawakened && last == AID.HuntersSting,
            AID.HindstingStrike or AID.HindsbaneFang => !reawakened && last == AID.SwiftskinsSting,
            AID.HuntersBite or AID.SwiftskinsBite => !reawakened && last is AID.SteelMaw or AID.ReavingMaw,
            AID.JaggedMaw or AID.BloodiedMaw => !reawakened && last is AID.HuntersBite or AID.SwiftskinsBite,
            AID.DeathRattle => _serpent == SerpentCombo.DeathRattle,
            AID.LastLash => _serpent == SerpentCombo.LastLash,
            AID.FirstLegacy => _serpent == SerpentCombo.FirstLegacy,
            AID.SecondLegacy => _serpent == SerpentCombo.SecondLegacy,
            AID.ThirdLegacy => _serpent == SerpentCombo.ThirdLegacy,
            AID.FourthLegacy => _serpent == SerpentCombo.FourthLegacy,
            AID.HuntersCoil => !reawakened && _dread is DreadCombo.Dreadwinder or DreadCombo.SwiftskinsCoil,
            AID.SwiftskinsCoil => !reawakened && _dread is DreadCombo.Dreadwinder or DreadCombo.HuntersCoil,
            AID.HuntersDen => !reawakened && _dread is DreadCombo.PitOfDread or DreadCombo.SwiftskinsDen,
            AID.SwiftskinsDen => !reawakened && _dread is DreadCombo.PitOfDread or DreadCombo.HuntersDen,
            AID.Vicewinder or AID.Vicepit => !reawakened,
            AID.TwinfangBite => _serpent == TwinsAfterCoil && _twinfangReady,
            AID.TwinbloodBite => _serpent == TwinsAfterCoil && _twinbloodReady,
            AID.TwinfangThresh => _serpent == TwinsAfterDen && _twinfangReady,
            AID.TwinbloodThresh => _serpent == TwinsAfterDen && _twinbloodReady,
            AID.UncoiledTwinfang => _serpent == TwinsAfterFury && _twinfangReady,
            AID.UncoiledTwinblood => _serpent == TwinsAfterFury && _twinbloodReady,
            AID.UncoiledFury => _coil > 0,
            AID.Reawaken => !reawakened && (Left(SID.ReawakenReady) > 0 || _offering >= 50),
            AID.FirstGeneration => reawakened && _anguine > 0 && step == 0,
            AID.SecondGeneration => reawakened && _anguine > 0 && step == 1,
            AID.ThirdGeneration => reawakened && _anguine > 0 && step == 2,
            AID.FourthGeneration => reawakened && _anguine > 0 && step == 3,
            AID.Ouroboros => reawakened && _anguine > 0 && step == 4,
            AID.SerpentsIre => player.InCombat,
            _ => true
        };
    }

    // Swiftscaled is the viper haste: 15%, applied to the weaponskill recast the client computes
    private void SyncHaste()
    {
        var haste = Left(SID.Swiftscaled) > 0 ? 85 : 100;
        var stats = world.Client.PlayerStats;
        if (stats.Haste != haste)
            world.Execute(new ClientState.OpPlayerStatsChange(new(stats.SkillSpeed, stats.SpellSpeed, haste)));
    }

    // the recast the press starts on the GCD group: its own base recast when the GCD is its main group (3.0s coils, 3.5s Uncoiled Fury,
    // 2.2s Reawaken, 2.0s Generations, 3.0s Ouroboros), else 2.5s (Vicewinder / Vicepit keep their charges on their own group)
    private float GCDLength(ActionDefinition definition)
    {
        var baseRecast = definition.MainCooldownGroup == ActionDefinitions.GCDGroup ? definition.Cooldown : 2.5f;
        return ActionSpeed.GCDRounded(world.Client.PlayerStats.SkillSpeed, world.Client.PlayerStats.Haste, player.Level, (int)MathF.Round(baseRecast * 1000));
    }

    private void StartCooldown(ActionDefinition definition)
    {
        var group = definition.ActualMainCooldownGroup(world.Client.DutyActions);
        if (group < 0 || group == ActionDefinitions.GCDGroup || definition.Cooldown <= 0)
            return;
        var capCharges = Math.Max(1, definition.MaxChargesAtCap());
        var levelCharges = Math.Clamp(definition.MaxChargesAtLevel(player.Level), 1, capCharges);
        var current = world.Client.Cooldowns[group];
        var elapsed = current.Total > 0 ? MathF.Max(0, current.Elapsed - definition.Cooldown) : definition.Cooldown * (levelCharges - 1);
        world.Execute(new ClientState.OpCooldown(false, [(group, new(elapsed, definition.Cooldown * capCharges))]));
        if (definition.ExtraCooldownGroup >= 0 && definition.ExtraCooldownGroup != ActionDefinitions.GCDGroup)
            world.Execute(new ClientState.OpCooldown(false, [(definition.ExtraCooldownGroup, new(0, 1))]));
    }

    private void Complete(ActionQueue.Entry entry, ActionDefinition definition)
    {
        var targetID = entry.Target?.InstanceID ?? player.InstanceID;
        var animationLock = definition.InstantAnimLock + frameStep;
        world.Execute(new ClientState.OpAnimationLockChange(animationLock));
        var castEvent = new ActorCastEvent(entry.Action, targetID, animationLock, 1, entry.Target?.PosRot.XYZ() ?? entry.TargetPos, ++_sequence, _sequence, player.Rotation);
        world.Execute(new ActorState.OpCastEvent(player.InstanceID, castEvent));

        var potency = 0f;
        if (entry.Action.Type == ActionType.Spell)
        {
            var aid = (AID)entry.Action.ID;
            potency = Score(aid, entry.Target);
            ApplySpell(aid, entry.Target, definition);
        }
        else if (entry.Action.Type == ActionType.Item)
        {
            var quantity = world.Client.GetInventoryItemQuantity(entry.Action.ID);
            world.Execute(new ClientState.OpInventoryChange(entry.Action.ID, quantity > 0 ? quantity - 1 : 0));
            if (entry.Action == ActionDefinitions.IDPotionDex)
            {
                var item = Service.LuminaRow<Lumina.Excel.Sheets.Item>(entry.Action.ID % 500000)!.Value;
                var data = item.ItemAction.Value.DataHQ;
                Set((SID)data[0], data[2], data[1] + 10000);
            }
        }
        PublishGauge();
        onExecuted(entry.Action, targetID, definition.IsGCD, potency);
    }

    private static bool IsWeaponskill(AID action) => ActionDefinitions.Instance.Spell(action) is { IsGCD: true };

    private float Score(AID action, Actor? target)
    {
        var level = player.Level;
        var potency = VprPotency.Of(action, level);
        if (potency <= 0)
            return 0;
        switch (action)
        {
            case AID.SteelFangs when Left(SID.HonedSteel) > 0:
            case AID.ReavingFangs when Left(SID.HonedReavers) > 0:
                potency += VprPotency.HonedFangsBonus;
                break;
            case AID.SteelMaw when Left(SID.HonedSteel) > 0:
            case AID.ReavingMaw when Left(SID.HonedReavers) > 0:
                potency += VprPotency.HonedMawBonus;
                break;
            case AID.FlankstingStrike when Left(SID.FlankstungVenom) > 0:
            case AID.FlanksbaneFang when Left(SID.FlanksbaneVenom) > 0:
            case AID.HindstingStrike when Left(SID.HindstungVenom) > 0:
            case AID.HindsbaneFang when Left(SID.HindsbaneVenom) > 0:
                potency += VprPotency.VenomBonus;
                break;
            case AID.JaggedMaw when Left(SID.GrimhuntersVenom) > 0:
            case AID.BloodiedMaw when Left(SID.GrimskinsVenom) > 0:
                potency += VprPotency.GrimBonus;
                break;
            case AID.TwinfangBite when Left(SID.HuntersVenom) > 0:
            case AID.TwinbloodBite when Left(SID.SwiftskinsVenom) > 0:
            case AID.UncoiledTwinfang when Left(SID.PoisedForTwinfang) > 0:
            case AID.UncoiledTwinblood when Left(SID.PoisedForTwinblood) > 0:
                potency += VprPotency.TwinBonus;
                break;
            case AID.TwinfangThresh when Left(SID.FellhuntersVenom) > 0:
            case AID.TwinbloodThresh when Left(SID.FellskinsVenom) > 0:
                potency += VprPotency.ThreshBonus;
                break;
            case AID.FirstGeneration when _lastWeaponskill == AID.Reawaken:
            case AID.SecondGeneration when _lastWeaponskill == AID.FirstGeneration:
            case AID.ThirdGeneration when _lastWeaponskill == AID.SecondGeneration:
            case AID.FourthGeneration when _lastWeaponskill == AID.ThirdGeneration:
                potency = VprPotency.GenerationInSequence;
                break;
        }

        var positional = action switch
        {
            AID.FlankstingStrike or AID.FlanksbaneFang or AID.HuntersCoil => Positional.Flank,
            AID.HindstingStrike or AID.HindsbaneFang or AID.SwiftskinsCoil => Positional.Rear,
            _ => Positional.Any
        };
        if (positional != Positional.Any && GeometricPositionals && Left(SID.TrueNorth) <= 0 && target != null && !OnSide(target, positional))
        {
            potency -= action is AID.HuntersCoil or AID.SwiftskinsCoil ? VprPotency.CoilPositionalBonus : VprPotency.FinisherPositionalBonus;
            ++PositionalsMissed;
            Events.Add(FormattableString.Invariant($"positional_missed,{Now():f2},{action}"));
        }

        if (IsWeaponskill(action) && _hadInstinct && Left(SID.HuntersInstinct) <= 0)
        {
            ++NoInstinctGCDs;
            Events.Add(FormattableString.Invariant($"no_instinct,{Now():f2},{action}"));
        }

        var buffs = 1f;
        if (Left(SID.HuntersInstinct) > 0)
            buffs *= InstinctMultiplier;
        if (player.FindStatus(MedicatedStatus) != null)
            buffs *= MedicatedMultiplier;
        return Hit(action, target, potency) * buffs;
    }

    // same test as Basexan.UpdatePositionals
    private bool OnSide(Actor target, Positional positional)
    {
        var dot = target.Rotation.ToDirection().Dot((player.Position - target.Position).Normalized());
        return positional == Positional.Flank ? MathF.Abs(dot) < 0.7071067f : dot < -0.7071068f;
    }

    private float Hit(AID action, Actor? target, float potency)
    {
        var shape = VprPotency.ShapeOf(action);
        var targetAlive = target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true };
        if (shape == VprPotency.Shape.Single)
            return targetAlive ? potency : 0;

        var falloff = VprPotency.Falloff(action) ? VprPotency.FalloffMultiplier : 1;
        var center = shape == VprPotency.Shape.Target5 && target != null ? target : player;
        var total = 0f;
        var hits = 0;
        // the targeted enemy takes the full potency when it is inside the circle
        if (targetAlive && target != player && (shape == VprPotency.Shape.Target5 || AIHints.TargetInAOECircle(target!, player.Position, 5)))
        {
            total += potency;
            ++hits;
        }
        foreach (var enemy in world.Actors)
        {
            if (enemy.Type != ActorType.Enemy || enemy.IsDead || !enemy.IsTargetable || hits > 0 && enemy == target)
                continue;
            if (!AIHints.TargetInAOECircle(enemy, center.Position, 5))
                continue;
            total += potency * (hits == 0 ? 1 : falloff);
            ++hits;
        }
        return total;
    }

    private void ApplySpell(AID action, Actor? target, ActionDefinition definition)
    {
        var level = player.Level;
        var weaponskill = definition.IsGCD;
        if (weaponskill)
            ReplaceSerpentCombo(NoSerpentCombo);

        switch (action)
        {
            case AID.SteelFangs:
            case AID.SteelMaw:
                Remove(SID.HonedSteel);
                if (level >= 10)
                    Set(SID.HonedReavers, HonedDuration);
                SetCombo(action);
                break;
            case AID.ReavingFangs:
            case AID.ReavingMaw:
                Remove(SID.HonedReavers);
                Set(SID.HonedSteel, HonedDuration);
                SetCombo(action);
                break;
            case AID.HuntersSting:
            case AID.HuntersBite:
                SetInstinct();
                SetCombo(action);
                break;
            case AID.SwiftskinsSting:
            case AID.SwiftskinsBite:
                Set(SID.Swiftscaled, BuffDuration);
                SetCombo(action);
                break;
            case AID.FlankstingStrike:
            case AID.FlanksbaneFang:
            case AID.HindstingStrike:
            case AID.HindsbaneFang:
                SetVenom(action switch
                {
                    AID.FlankstingStrike => SID.HindstungVenom,
                    AID.FlanksbaneFang => SID.HindsbaneVenom,
                    AID.HindstingStrike => SID.FlanksbaneVenom,
                    _ => SID.FlankstungVenom
                });
                if (level >= 90)
                    GainOffering(10, action);
                if (level >= 55)
                    ReplaceSerpentCombo(SerpentCombo.DeathRattle);
                SetCombo(AID.None);
                break;
            case AID.JaggedMaw:
            case AID.BloodiedMaw:
                SetVenom(action == AID.JaggedMaw ? SID.GrimskinsVenom : SID.GrimhuntersVenom);
                if (level >= 90)
                    GainOffering(10, action);
                if (level >= 60)
                    ReplaceSerpentCombo(SerpentCombo.LastLash);
                SetCombo(AID.None);
                break;

            case AID.Vicewinder:
            case AID.Vicepit:
                _dread = action == AID.Vicewinder ? DreadCombo.Dreadwinder : DreadCombo.PitOfDread;
                if (level >= 82)
                    GainCoil(action);
                break;
            case AID.HuntersCoil:
            case AID.SwiftskinsCoil:
            case AID.HuntersDen:
            case AID.SwiftskinsDen:
            {
                ++Coils;
                var hunter = action is AID.HuntersCoil or AID.HuntersDen;
                var den = action is AID.HuntersDen or AID.SwiftskinsDen;
                _dread = _dread is DreadCombo.Dreadwinder or DreadCombo.PitOfDread
                    ? action switch
                    {
                        AID.HuntersCoil => DreadCombo.HuntersCoil,
                        AID.SwiftskinsCoil => DreadCombo.SwiftskinsCoil,
                        AID.HuntersDen => DreadCombo.HuntersDen,
                        _ => DreadCombo.SwiftskinsDen
                    }
                    : 0;
                if (hunter)
                    SetInstinct();
                else
                    Set(SID.Swiftscaled, BuffDuration);
                if (level >= (den ? 80 : 75))
                {
                    Set(den ? (hunter ? SID.FellhuntersVenom : SID.FellskinsVenom) : hunter ? SID.HuntersVenom : SID.SwiftskinsVenom, TwinVenomDuration);
                    OpenTwins(den ? TwinsAfterDen : TwinsAfterCoil, action);
                }
                if (level >= 90)
                    GainOffering(5, action);
                break;
            }
            case AID.TwinfangBite:
            case AID.TwinbloodBite:
            case AID.TwinfangThresh:
            case AID.TwinbloodThresh:
            case AID.UncoiledTwinfang:
            case AID.UncoiledTwinblood:
            {
                var fang = action is AID.TwinfangBite or AID.TwinfangThresh or AID.UncoiledTwinfang;
                Remove(action switch
                {
                    AID.TwinfangBite => SID.HuntersVenom,
                    AID.TwinbloodBite => SID.SwiftskinsVenom,
                    AID.TwinfangThresh => SID.FellhuntersVenom,
                    AID.TwinbloodThresh => SID.FellskinsVenom,
                    AID.UncoiledTwinfang => SID.PoisedForTwinfang,
                    _ => SID.PoisedForTwinblood
                });
                // the second twin is buffed by the first when the window opened on the matching side
                var grant = action switch
                {
                    AID.TwinfangBite when _twinSource == AID.HuntersCoil => SID.SwiftskinsVenom,
                    AID.TwinbloodBite when _twinSource == AID.SwiftskinsCoil => SID.HuntersVenom,
                    AID.TwinfangThresh when _twinSource == AID.HuntersDen => SID.FellskinsVenom,
                    AID.TwinbloodThresh when _twinSource == AID.SwiftskinsDen => SID.FellhuntersVenom,
                    AID.UncoiledTwinfang => SID.PoisedForTwinblood,
                    _ => SID.None
                };
                if (grant != SID.None)
                    Set(grant, grant == SID.PoisedForTwinblood ? PoisedDuration : TwinVenomDuration);
                if (fang)
                    _twinfangReady = false;
                else
                    _twinbloodReady = false;
                if (!_twinfangReady && !_twinbloodReady)
                    _serpent = NoSerpentCombo;
                break;
            }
            case AID.UncoiledFury:
                ++UncoiledFuries;
                _coil = Math.Max(0, _coil - 1);
                if (level >= 92)
                {
                    Set(SID.PoisedForTwinfang, PoisedDuration);
                    OpenTwins(TwinsAfterFury, action);
                }
                break;

            case AID.SerpentsIre:
                GainCoil(action);
                if (level >= 90)
                    Set(SID.ReawakenReady, ReadyDuration);
                break;
            case AID.Reawaken:
                ++Reawakens;
                if (Left(SID.ReawakenReady) > 0)
                    Remove(SID.ReawakenReady);
                else
                    _offering = Math.Max(0, _offering - 50);
                _anguine = AnguineMax;
                Set(SID.Reawakened, ReawakenedDuration);
                break;
            case AID.FirstGeneration:
            case AID.SecondGeneration:
            case AID.ThirdGeneration:
            case AID.FourthGeneration:
                ++Generations;
                _anguine = Math.Max(0, _anguine - 1);
                if (level >= 100)
                    ReplaceSerpentCombo(action switch
                    {
                        AID.FirstGeneration => SerpentCombo.FirstLegacy,
                        AID.SecondGeneration => SerpentCombo.SecondLegacy,
                        AID.ThirdGeneration => SerpentCombo.ThirdLegacy,
                        _ => SerpentCombo.FourthLegacy
                    });
                if (_anguine == 0)
                    Remove(SID.Reawakened);
                break;
            case AID.Ouroboros:
                _anguine = 0;
                Remove(SID.Reawakened);
                break;

            case AID.DeathRattle:
            case AID.LastLash:
            case AID.FirstLegacy:
            case AID.SecondLegacy:
            case AID.ThirdLegacy:
            case AID.FourthLegacy:
                _serpent = NoSerpentCombo;
                break;
            case AID.TrueNorth:
                Set(SID.TrueNorth, TrueNorthDuration);
                break;
        }

        // Uncoiled Fury and Writhing Snap leave combos alone (in replays Stings and finishers follow both); for the Generation chain the
        // replays show it for Uncoiled Fury: the Generation after it still lands with its combo potency
        if (weaponskill && action is not (AID.UncoiledFury or AID.WrithingSnap))
            _lastWeaponskill = action;
    }

    // a weaponskill replaces whatever Serpent's Tail / twin follow-up was still pending
    private void ReplaceSerpentCombo(SerpentCombo next)
    {
        if (_serpent != NoSerpentCombo)
        {
            ++FollowUpsLost;
            Events.Add(FormattableString.Invariant($"follow_up_lost,{Now():f2},{(int)_serpent},{(_twinfangReady ? 1 : 0) + (_twinbloodReady ? 1 : 0)}"));
        }
        _serpent = next;
        _twinfangReady = _twinbloodReady = false;
    }

    private void OpenTwins(SerpentCombo window, AID source)
    {
        _serpent = window;
        _twinfangReady = _twinbloodReady = true;
        _twinSource = source;
    }

    private void SetInstinct()
    {
        Set(SID.HuntersInstinct, BuffDuration);
        _hadInstinct = true;
    }

    // the combo venoms cannot stack with each other
    private void SetVenom(SID venom)
    {
        foreach (var other in (ReadOnlySpan<SID>)[SID.FlankstungVenom, SID.FlanksbaneVenom, SID.HindstungVenom, SID.HindsbaneVenom, SID.GrimhuntersVenom, SID.GrimskinsVenom])
            Remove(other);
        Set(venom, VenomDuration);
    }

    private void GainOffering(int amount, AID source)
    {
        var wasted = Math.Max(0, _offering + amount - 100);
        if (wasted > 0)
        {
            OfferingOvercap += wasted;
            Events.Add(FormattableString.Invariant($"offering_overcap,{Now():f2},{source},{wasted}"));
        }
        _offering = Math.Min(100, _offering + amount);
    }

    private void GainCoil(AID source)
    {
        if (CoilMax == 0)
            return;
        if (_coil >= CoilMax)
        {
            ++CoilOvercap;
            Events.Add(FormattableString.Invariant($"coil_overcap,{Now():f2},{source}"));
        }
        _coil = Math.Min(CoilMax, _coil + 1);
    }

    private float Now() => (float)(world.CurrentTime - BaseTime).TotalSeconds;
    private float Left(SID status) => MathF.Max(0, (float)((player.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);

    private void SetCombo(AID action) => world.Execute(new ClientState.OpComboChange(action == AID.None ? default : new((uint)action, ComboDuration)));

    private void Set(SID status, float duration, int stacks = 0)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot < 0)
            slot = Array.FindIndex(player.Statuses, s => s.ID == 0);
        if (slot < 0)
            throw new InvalidOperationException("VPR simulation ran out of status slots.");
        world.Execute(new ActorState.OpStatus(player.InstanceID, slot, new((uint)status, (ushort)stacks, world.CurrentTime.AddSeconds(duration), player.InstanceID)));
    }

    private void Remove(SID status)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot >= 0)
            world.Execute(new ActorState.OpStatus(player.InstanceID, slot, default));
    }

    // ViperGauge lives at struct offset 0x08 onwards (ClientState.GetGauge copies GaugePayload.Low there); SerpentComboState keeps the
    // combo in its upper bits and the twin uses left in the lower two
    private unsafe void PublishGauge()
    {
        ViperGauge gauge = default;
        gauge.RattlingCoilStacks = (byte)_coil;
        gauge.AnguineTribute = (byte)_anguine;
        gauge.SerpentOffering = (byte)_offering;
        gauge.DreadCombo = _dread;
        gauge.SerpentComboState = (byte)(((int)_serpent << 2) | ((_twinfangReady ? 1 : 0) + (_twinbloodReady ? 1 : 0)));
        var raw = (ulong*)Unsafe.AsPointer(ref gauge);
        world.Client.GaugePayload = new(sizeof(ViperGauge) > 8 ? raw[1] : 0, sizeof(ViperGauge) > 16 ? raw[2] : 0);
    }
}

// Value of the viper resources still held when a scenario ends: each is what it adds over the filler GCDs it displaces.
internal static class VprTerminalValue
{
    // a dual-wield combo GCD with its share of Death Rattle
    private const float Filler = 450;

    public static float Estimate(WorldState world, Actor player, VprCombatState combat)
    {
        float Left(SID sid) => player.FindStatus((uint)sid, player.InstanceID) is ActorStatus s ? MathF.Max(0, (float)(s.ExpireAt - world.CurrentTime).TotalSeconds) : 0;
        var level = player.Level;
        var generation = VprPotency.GenerationInSequence + (level >= 100 ? VprPotency.Of(AID.FirstLegacy, level) : 0);
        // Reawaken, four Generations and (from 96) Ouroboros against the fillers that fit in the same time
        var sequence = VprPotency.Of(AID.Reawaken, level) + 4 * generation + (level >= 96 ? VprPotency.Of(AID.Ouroboros, level) : 0)
            - Filler * (2.2f + 4 * 2.0f + (level >= 96 ? 3.0f : 0)) / 2.5f;
        var fury = VprPotency.Of(AID.UncoiledFury, level) + (level >= 92 ? 2 * (VprPotency.Of(AID.UncoiledTwinfang, level) + VprPotency.TwinBonus) : 0) - Filler * 3.5f / 2.5f;
        var coilGCD = VprPotency.Of(AID.HuntersCoil, level) + (level >= 75 ? 2 * (VprPotency.Of(AID.TwinfangBite, level) + VprPotency.TwinBonus) : 0) - Filler * 3.0f / 2.5f;

        var value = combat.Coil * fury + MathF.Max(0, sequence) * (combat.Offering / 50f + (Left(SID.ReawakenReady) > 0 ? 1 : 0));
        value += combat.Dread switch
        {
            DreadCombo.Dreadwinder => 2 * coilGCD,
            DreadCombo.HuntersCoil or DreadCombo.SwiftskinsCoil => coilGCD,
            _ => 0
        };
        if (Left(SID.Reawakened) > 0)
            value += combat.Anguine * (generation - Filler * 2.0f / 2.5f);
        return value;
    }
}
