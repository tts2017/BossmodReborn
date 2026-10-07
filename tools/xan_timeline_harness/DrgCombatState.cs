using System;
using System.Runtime.CompilerServices;
using BossMod;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using AID = BossMod.DRG.AID;
using SID = BossMod.DRG.SID;

namespace XanTimelineHarness;

// Deterministic action-result simulation for Dragoon (7.5), in the style of GnbCombatState. Decisions come from production
// xan DRG.cs; this only models what the client does with them: the two five-step combos and the AoE combo, Draconian Fire,
// Firstminds' Focus and the Life of the Dragon timer on the gauge, the self buffs and "ready" statuses DRG.cs reads back, and the
// Chaotic Spring dot on the target. Every press is scored at the press with the buffs up then (Power Surge 10%, Lance Charge 10%,
// Life of the Dragon 15%, expected crit with Battle Litany and Life Surge), no damage rolls, so two runs are byte-identical.
internal sealed class DrgCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float> onExecuted)
{
    // party damage that Battle Litany's 10% critical rate lifts (the same 1450 potency/s party the NIN and RPR runs assume)
    public const float PartyPotencyPerSecond = 1450f;

    public float PartyLitanyValue { get; private set; }
    public int LanceChargeGCDs { get; private set; }
    public int LifeOfTheDragonGCDs { get; private set; }
    public int LifeSurgesLost { get; private set; }
    public int LifeSurgesWeak { get; private set; }
    public int ProcsLost { get; private set; }
    public int FocusOvercap { get; private set; }
    public int NoPowerSurgeGCDs { get; private set; }
    public int DotGapFrames { get; private set; }
    public int Geirskoguls { get; private set; }
    public int Stardivers { get; private set; }
    public System.Collections.Generic.List<string> Events { get; } = [];

    private int _focus;
    private DateTime _lotdUntil;
    private DateTime _bloodUntil;
    private bool _hadPowerSurge;
    private bool _hadDot;
    private uint _sequence;

    private const float ComboDuration = 30f;
    private const float PowerSurgeDuration = 30f;
    private const float DotDuration = 24f;
    private const float LanceChargeDuration = 20f;
    private const float BattleLitanyDuration = 20f;
    private const float LifeSurgeDuration = 5f;
    private const float DraconianFireDuration = 30f;
    private const float DiveReadyDuration = 15f;
    private const float NastrondReadyDuration = 20f;
    private const float LifeOfTheDragonDuration = 20f;
    private const float StarcrossReadyDuration = 20f;
    private const float DragonsFlightDuration = 30f;
    private const float EnhancedTalonDuration = 15f;
    private const float TrueNorthDuration = 10f;
    private const float MedicatedMultiplier = 1.06f;
    private const uint MedicatedStatus = 49;

    public int Focus => _focus;
    public float LifeOfTheDragonLeft => Seconds(_lotdUntil);
    public bool EnforceRangeAndMovement { get; set; }
    public bool Moving { get; set; }
    public int InterruptedCasts => 0;

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
                    if (expired is SID.DiveReady or SID.NastrondReady or SID.StarcrossReady or SID.DragonsFlight or SID.DraconianFire)
                    {
                        ++ProcsLost;
                        Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{expired}"));
                    }
                    else if (expired == SID.LifeSurge)
                    {
                        ++LifeSurgesLost;
                        Events.Add(FormattableString.Invariant($"ls_lost,{Now():f2}"));
                    }
                }

        // Starcross Ready ends with Life of the Dragon
        if (Seconds(_lotdUntil) <= 0 && Left(SID.StarcrossReady) > 0)
        {
            Remove(SID.StarcrossReady);
            ++ProcsLost;
            Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},StarcrossReadyWithLotD"));
        }

        var enemyUp = false;
        var dotUp = false;
        foreach (var actor in world.Actors)
            if (actor.Type == ActorType.Enemy && !actor.IsDead && actor.IsTargetable)
            {
                enemyUp = true;
                dotUp |= actor.FindStatus((uint)SID.ChaoticSpring, player.InstanceID) != null || actor.FindStatus((uint)SID.ChaosThrust, player.InstanceID) != null;
            }
        if (enemyUp && Left(SID.BattleLitany) > 0)
            PartyLitanyValue += PartyPotencyPerSecond * frameStep * (DrgPotency.NormalHit(0.10f) - 1);
        if (enemyUp && _hadDot && !dotUp)
            ++DotGapFrames;

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
        if (Irregular.Refuses("drg", entry, definition, world, player)) // --irregular lockout statuses, refused after the pick like the client
            return;
        if (ClientReject.Enabled && !CanExecute(entry))
        {
            ClientReject.Record("drg", entry.Action, world.CurrentTime, player);
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
    private string DescribeForSearch() => FormattableString.Invariant($"focus={_focus}");

    // burst study (BurstControl.cs): the state shown next to a cycle and the emulator's own readiness rules for a starter
    public string BurstState() => DescribeForSearch();
    public bool BurstFeasible(ActionQueue.Entry entry) => CanExecute(entry);

    private bool CanExecute(ActionQueue.Entry entry, bool jobRules = true)
    {
        if (entry.Target != null && entry.Target != player && (entry.Target.IsDead || !entry.Target.IsTargetable))
            return false;
        if (EnforceRangeAndMovement && entry.Target != null && entry.Target != player)
        {
            var def = ActionDefinitions.Instance[entry.Action];
            if (def != null && def.Range > 0 && player.DistanceToHitbox(entry.Target) > def.Range)
                return false;
        }
        if (!jobRules)
            return true;
        if (entry.Action.Type != ActionType.Spell)
            return true;

        var combo = (AID)world.Client.ComboState.Action;
        return (AID)entry.Action.ID switch
        {
            AID.Drakesbane => combo is AID.WheelingThrust or AID.FangAndClaw,
            AID.RaidenThrust or AID.DraconianFury => Left(SID.DraconianFire) > 0,
            AID.MirageDive => Left(SID.DiveReady) > 0,
            AID.Nastrond => Left(SID.NastrondReady) > 0,
            AID.Starcross => Left(SID.StarcrossReady) > 0,
            AID.RiseOfTheDragon => Left(SID.DragonsFlight) > 0,
            AID.Stardiver => Seconds(_lotdUntil) > 0,
            AID.WyrmwindThrust => _focus >= 2,
            _ => true
        };
    }

    private float GCDLength(ActionDefinition definition)
    {
        var stats = world.Client.PlayerStats;
        return ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, player.Level, 2500);
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
            // the client replaces Jump with High Jump from 74 (Jump Mastery); DRG.cs pushes Jump and relies on that
            if (aid == AID.Jump && player.Level >= 74)
                aid = AID.HighJump;
            potency = Score(aid, entry.Target, definition);
            ApplySpell(aid, entry.Target, definition);
        }
        else if (entry.Action.Type == ActionType.Item)
        {
            var quantity = world.Client.GetInventoryItemQuantity(entry.Action.ID);
            world.Execute(new ClientState.OpInventoryChange(entry.Action.ID, quantity > 0 ? quantity - 1 : 0));
            if (entry.Action == ActionDefinitions.IDPotionStr)
            {
                var item = Service.LuminaRow<Lumina.Excel.Sheets.Item>(entry.Action.ID % 500000)!.Value;
                var data = item.ItemAction.Value.DataHQ;
                Set((SID)data[0], data[2], data[1] + 10000);
            }
        }
        PublishGauge();
        onExecuted(entry.Action, targetID, definition.IsGCD, potency);
    }

    private static bool IsComboFrom(AID action, AID combo) => action switch
    {
        AID.VorpalThrust or AID.LanceBarrage or AID.Disembowel or AID.SpiralBlow => combo is AID.TrueThrust or AID.RaidenThrust,
        AID.FullThrust or AID.HeavensThrust => combo is AID.VorpalThrust or AID.LanceBarrage,
        AID.ChaosThrust or AID.ChaoticSpring => combo is AID.Disembowel or AID.SpiralBlow,
        AID.FangAndClaw => combo is AID.FullThrust or AID.HeavensThrust,
        AID.WheelingThrust => combo is AID.ChaosThrust or AID.ChaoticSpring,
        AID.Drakesbane => combo is AID.WheelingThrust or AID.FangAndClaw,
        AID.SonicThrust => combo is AID.DoomSpike or AID.DraconianFury,
        AID.CoerthanTorment => combo == AID.SonicThrust,
        _ => false
    };

    private static bool IsWeaponskill(AID action) => action is AID.TrueThrust or AID.RaidenThrust or AID.VorpalThrust or AID.LanceBarrage or AID.FullThrust
        or AID.HeavensThrust or AID.Disembowel or AID.SpiralBlow or AID.ChaosThrust or AID.ChaoticSpring or AID.FangAndClaw or AID.WheelingThrust
        or AID.Drakesbane or AID.PiercingTalon or AID.DoomSpike or AID.SonicThrust or AID.CoerthanTorment or AID.DraconianFury;

    // scored before ApplySpell, so the combo state and the buffs are the ones the press sees
    private float Score(AID action, Actor? target, ActionDefinition definition)
    {
        var combo = IsComboFrom(action, (AID)world.Client.ComboState.Action);
        var level = player.Level;
        var basePotency = action == AID.PiercingTalon && Left(SID.EnhancedPiercingTalon) > 0 ? DrgPotency.EnhancedTalon(level) : DrgPotency.Of(action, level, combo);
        if (basePotency <= 0 && action is not (AID.ChaosThrust or AID.ChaoticSpring))
            return 0;

        var weaponskill = IsWeaponskill(action);
        var buffs = BuffMultiplier();
        var critBuff = Left(SID.BattleLitany) > 0 ? 0.10f : 0;
        var lifeSurged = weaponskill && Left(SID.LifeSurge) > 0;
        var crit = lifeSurged ? DrgPotency.GuaranteedCrit(critBuff) : DrgPotency.NormalHit(critBuff);

        if (weaponskill && target is { Type: ActorType.Enemy })
        {
            if (Left(SID.LanceCharge) > 0)
                ++LanceChargeGCDs;
            if (Seconds(_lotdUntil) > 0)
                ++LifeOfTheDragonGCDs;
            if (_hadPowerSurge && Left(SID.PowerSurge) <= 0)
            {
                ++NoPowerSurgeGCDs;
                Events.Add(FormattableString.Invariant($"no_surge,{Now():f2},{action}"));
            }
            if (lifeSurged && DrgPotency.ShapeOf(action) == DrgPotency.Shape.Single && basePotency < 380)
            {
                ++LifeSurgesWeak;
                Events.Add(FormattableString.Invariant($"life_surge_weak,{Now():f2},{action}"));
            }
        }

        var total = Hit(action, target, basePotency) * buffs * crit;

        // the dot snapshots the buffs at the press and only the time it newly covers counts (a refresh clips the rest)
        if (combo && action is AID.ChaosThrust or AID.ChaoticSpring && target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true })
        {
            var old = MathF.Max(Left(target, SID.ChaoticSpring), Left(target, SID.ChaosThrust));
            var covered = DotDuration - MathF.Min(old, DotDuration);
            total += covered / DrgPotency.DotTick * DrgPotency.DotTickPotency(action) * buffs * DrgPotency.NormalHit(critBuff);
        }
        return total;
    }

    private float BuffMultiplier()
    {
        var m = 1f;
        if (Left(SID.PowerSurge) > 0)
            m *= 1.10f;
        if (Left(SID.LanceCharge) > 0)
            m *= 1.10f;
        if (Seconds(_lotdUntil) > 0)
            m *= 1.15f;
        else if (Seconds(_bloodUntil) > 0)
            m *= 1.10f;
        if (player.FindStatus(MedicatedStatus) != null)
            m *= MedicatedMultiplier;
        return m;
    }

    private float Hit(AID action, Actor? target, float potency)
    {
        if (target is not { Type: ActorType.Enemy, IsDead: false, IsTargetable: true })
            return 0;
        var shape = DrgPotency.ShapeOf(action);
        if (shape == DrgPotency.Shape.Single)
            return potency;
        var falloff = DrgPotency.Falloff(action);
        var total = potency;
        foreach (var enemy in world.Actors)
        {
            if (enemy == target || enemy.Type != ActorType.Enemy || enemy.IsDead || !enemy.IsTargetable)
                continue;
            var hit = shape switch
            {
                DrgPotency.Shape.Line10 => AIHints.TargetInAOERect(enemy, player.Position, player.DirectionTo(target), 10, 2),
                DrgPotency.Shape.Line15 => AIHints.TargetInAOERect(enemy, player.Position, player.DirectionTo(target), 15, 2),
                _ => AIHints.TargetInAOECircle(enemy, target.Position, 5)
            };
            if (hit)
                total += potency * falloff;
        }
        return total;
    }

    private void ApplySpell(AID action, Actor? target, ActionDefinition definition)
    {
        var combo = IsComboFrom(action, (AID)world.Client.ComboState.Action);
        var level = player.Level;
        var weaponskill = IsWeaponskill(action);
        if (weaponskill && Left(SID.LifeSurge) > 0)
            Remove(SID.LifeSurge);

        switch (action)
        {
            case AID.TrueThrust:
                SetCombo(action);
                break;
            case AID.RaidenThrust:
                Remove(SID.DraconianFire);
                GainFocus(level);
                SetCombo(action);
                break;
            case AID.VorpalThrust:
            case AID.LanceBarrage:
            case AID.FullThrust:
            case AID.HeavensThrust:
            case AID.FangAndClaw:
            case AID.WheelingThrust:
                SetCombo(combo ? action : AID.None);
                break;
            case AID.Disembowel:
            case AID.SpiralBlow:
                if (combo)
                    GainPowerSurge();
                SetCombo(combo ? action : AID.None);
                break;
            case AID.ChaosThrust:
            case AID.ChaoticSpring:
                if (combo && target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true })
                {
                    SetOn(target, action == AID.ChaoticSpring ? SID.ChaoticSpring : SID.ChaosThrust, DotDuration);
                    _hadDot = true;
                }
                SetCombo(combo ? action : AID.None);
                break;
            case AID.Drakesbane:
                if (level >= 76)
                    Set(SID.DraconianFire, DraconianFireDuration);
                SetCombo(AID.None);
                break;
            case AID.DoomSpike:
                SetCombo(action);
                break;
            case AID.DraconianFury:
                Remove(SID.DraconianFire);
                GainFocus(level);
                SetCombo(action);
                break;
            case AID.SonicThrust:
                if (combo)
                    GainPowerSurge();
                SetCombo(combo ? action : AID.None);
                break;
            case AID.CoerthanTorment:
                if (combo && level >= 82)
                    Set(SID.DraconianFire, DraconianFireDuration);
                SetCombo(AID.None);
                break;
            case AID.PiercingTalon:
                // Piercing Talon leaves a running combo alone: in a real replay (SanDoria the Second Walk, DRG 100) the combo state
                // was never cleared by its 13 uses in the middle of a combo (it used to be reset here, which made every disengage
                // restart the combo with True Thrust)
                Remove(SID.EnhancedPiercingTalon);
                break;

            case AID.LifeSurge:
                Set(SID.LifeSurge, LifeSurgeDuration);
                break;
            case AID.LanceCharge:
                Set(SID.LanceCharge, LanceChargeDuration);
                break;
            case AID.BattleLitany:
                Set(SID.BattleLitany, BattleLitanyDuration);
                break;
            case AID.Jump:
            case AID.HighJump:
                if (level >= 68)
                    Set(SID.DiveReady, DiveReadyDuration);
                break;
            case AID.MirageDive:
                Remove(SID.DiveReady);
                break;
            case AID.Geirskogul:
                ++Geirskoguls;
                if (level >= 70)
                {
                    _lotdUntil = world.CurrentTime.AddSeconds(LifeOfTheDragonDuration);
                    Set(SID.NastrondReady, NastrondReadyDuration);
                }
                else
                    _bloodUntil = world.CurrentTime.AddSeconds(LifeOfTheDragonDuration);
                break;
            case AID.Nastrond:
                Remove(SID.NastrondReady);
                break;
            case AID.Stardiver:
                ++Stardivers;
                if (level >= 100)
                    Set(SID.StarcrossReady, MathF.Min(StarcrossReadyDuration, Seconds(_lotdUntil)));
                break;
            case AID.Starcross:
                Remove(SID.StarcrossReady);
                break;
            case AID.DragonfireDive:
                if (level >= 92)
                    Set(SID.DragonsFlight, DragonsFlightDuration);
                break;
            case AID.RiseOfTheDragon:
                Remove(SID.DragonsFlight);
                break;
            case AID.WyrmwindThrust:
                _focus = Math.Max(0, _focus - 2);
                break;
            case AID.ElusiveJump:
                Set(SID.EnhancedPiercingTalon, EnhancedTalonDuration);
                break;
            case AID.TrueNorth:
                Set(SID.TrueNorth, TrueNorthDuration);
                break;
        }
    }

    private void GainPowerSurge()
    {
        Set(SID.PowerSurge, PowerSurgeDuration);
        _hadPowerSurge = true;
    }

    private void GainFocus(int level)
    {
        if (level < 90)
            return;
        if (_focus >= 2)
        {
            ++FocusOvercap;
            Events.Add(FormattableString.Invariant($"focus_overcap,{Now():f2}"));
        }
        _focus = Math.Min(2, _focus + 1);
    }

    private float Now() => (float)(world.CurrentTime - BaseTime).TotalSeconds;
    public DateTime BaseTime { get; init; }

    private float Seconds(DateTime until) => MathF.Max(0, (float)(until - world.CurrentTime).TotalSeconds);
    private float Left(SID status) => Left(player, status);
    private float Left(Actor actor, SID status) => MathF.Max(0, (float)((actor.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);

    private void SetCombo(AID action) => world.Execute(new ClientState.OpComboChange(action == AID.None ? default : new((uint)action, ComboDuration)));

    private void Set(SID status, float duration, int stacks = 0) => SetOn(player, status, duration, stacks);

    private void SetOn(Actor actor, SID status, float duration, int stacks = 0)
    {
        var slot = Array.FindIndex(actor.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot < 0)
            slot = Array.FindIndex(actor.Statuses, s => s.ID == 0);
        if (slot < 0)
            throw new InvalidOperationException("DRG simulation ran out of status slots.");
        world.Execute(new ActorState.OpStatus(actor.InstanceID, slot, new((uint)status, (ushort)stacks, duration == float.MaxValue ? DateTime.MaxValue : world.CurrentTime.AddSeconds(duration), player.InstanceID)));
    }

    private void Remove(SID status)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot >= 0)
            world.Execute(new ActorState.OpStatus(player.InstanceID, slot, default));
    }

    // DragoonGauge lives at struct offset 0x08 onwards (ClientState.GetGauge copies GaugePayload.Low there)
    private unsafe void PublishGauge()
    {
        DragoonGauge gauge = default;
        gauge.FirstmindsFocusCount = (byte)_focus;
        gauge.LotdTimer = (short)Math.Clamp((int)(Seconds(_lotdUntil) * 1000), 0, short.MaxValue);
        var raw = (ulong*)Unsafe.AsPointer(ref gauge);
        world.Client.GaugePayload = new(sizeof(DragoonGauge) > 8 ? raw[1] : 0, sizeof(DragoonGauge) > 16 ? raw[2] : 0);
    }
}

// Value of the dragoon resources still held when a scenario ends: each is what it adds over the filler GCD or nothing it displaces.
internal static class DrgTerminalValue
{
    public static float Estimate(WorldState world, Actor player, DrgCombatState combat)
    {
        float Left(SID sid) => player.FindStatus((uint)sid, player.InstanceID) is ActorStatus s ? MathF.Max(0, (float)(s.ExpireAt - world.CurrentTime).TotalSeconds) : 0;
        var level = player.Level;
        var value = combat.Focus * DrgPotency.Of(AID.WyrmwindThrust, level, false) / 2;
        if (Left(SID.DiveReady) > 0)
            value += DrgPotency.Of(AID.MirageDive, level, false);
        if (Left(SID.NastrondReady) > 0)
            value += DrgPotency.Of(AID.Nastrond, level, false);
        if (Left(SID.StarcrossReady) > 0)
            value += DrgPotency.Of(AID.Starcross, level, false);
        if (Left(SID.DragonsFlight) > 0)
            value += DrgPotency.Of(AID.RiseOfTheDragon, level, false);
        if (Left(SID.DraconianFire) > 0)
            value += DrgPotency.Of(AID.RaidenThrust, level, false) - DrgPotency.Of(AID.TrueThrust, level, false);
        return value;
    }
}
