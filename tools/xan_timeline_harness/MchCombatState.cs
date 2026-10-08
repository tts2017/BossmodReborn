using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BossMod;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using AID = BossMod.MCH.AID;
using SID = BossMod.MCH.SID;

namespace XanTimelineHarness;

// Deterministic action-result simulation for Machinist (7.5), in the style of SamCombatState / VprCombatState. Decisions come from
// production xan MCH.cs; this only models what the client does with them: the Heat / Battery gauges, the combo, Hypercharge with its
// five Overheated stacks (Heat Blast / Blazing Shot / Auto Crossbow on a 1.5s recast, +20 potency on single-target weaponskills, each
// Heat Blast / Blazing Shot taking 15s off both Gauss Round / Double Check and Ricochet / Checkmate), Reassemble (a guaranteed critical
// direct hit), Wildfire (240 per weaponskill landed on its target, up to six, dealt when it ends or on Detonator), Barrel Stabilizer's
// Hypercharged / Full Metal Machinist, Chain Saw's Excavator Ready, the Bioblaster dot and the Automaton Queen / Rook Autoturret,
// whose attacks land on a fixed schedule after the summon and scale with the Battery it consumed. No damage rolls: a guaranteed
// critical direct hit is scored at its expectation over a normal hit, so two runs are byte-identical.
internal sealed class MchCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float> onExecuted)
{
    public float DotPotency { get; private set; }
    public int HeatOvercap { get; private set; }
    public int BatteryOvercap { get; private set; }
    public int ProcsLost { get; private set; }
    public int Hypercharges { get; private set; }
    public int OverheatedShots { get; private set; }
    public int OverheatedStacksLost { get; private set; }
    public int WildfireHits { get; private set; }
    public int Wildfires { get; private set; }
    public int Queens { get; private set; }
    public int QueenBattery { get; private set; }
    public int PetHitsLost { get; private set; }
    public int ReassembledTools { get; private set; }
    public int ReassembleWasted { get; private set; }
    public List<string> Events { get; } = [];
    public DateTime BaseTime { get; init; }

    private int _heat;
    private int _battery;
    private int _overheatStacks;
    private DateTime _overheatUntil;
    private DateTime _summonUntil;
    private int _summonBattery;
    private ulong _petTargetID;
    private ulong _lastHostileTargetID;
    private readonly List<(DateTime At, uint Attack)> _petAttacks = [];
    private ulong _wildfireTargetID;
    private int _wildfireStacks;
    private bool _flamethrowerActive;
    private DateTime _nextFlamethrowerTick;
    private uint _sequence;
    private DateTime _nextTick;
    private readonly Dictionary<ulong, float> _dotTick = [];

    private const float ComboDuration = 30f;
    private const float ReassembleDuration = 5f;
    private const float HyperchargedDuration = 30f;
    private const float ReadyDuration = 30f;
    private const float OverheatDuration = 10f;
    // pet attack times after the summon / Overdrive press, measured on 283 natural Automaton Queen summons (level 90+) and the Rook
    // Autoturret summons of other machinists in BossMod replays: the tooltip's 12s / 9s count from the pet's arrival, not the press
    private const float RollerDashAt = 5.6f;
    private const float FirstArmPunchAt = 8.7f;
    private const float ArmPunchInterval = 1.6f;
    private const int ArmPunches = 3;
    private const float PileBunkerAt = 13.4f;
    private const float CrownedColliderAt = 15.5f;
    private const float FirstVolleyFireAt = 2.7f;
    private const int VolleyFires = 5;
    private const float RookOverloadAt = 9.75f;
    private const float OverdrivePileBunkerAt = 0.5f;
    private const float OverdriveCrownedColliderAt = 2.5f;
    private const float CooldownCut = 15f;
    private const float MedicatedMultiplier = 1.06f;
    private const uint MedicatedStatus = 49;
    // expected crit 25% at x1.6 and direct hit 30% at x1.25, against which a guaranteed critical direct hit is scored
    private const float CritRate = 0.25f;
    private const float CritMultiplier = 1.6f;
    private const float DirectRate = 0.3f;
    private const float DirectMultiplier = 1.25f;
    public static readonly float CritDirectMultiplier = CritMultiplier / (1 + CritRate * (CritMultiplier - 1)) * DirectMultiplier / (1 + DirectRate * (DirectMultiplier - 1));

    public int Heat => _heat;
    public int Battery => _battery;

    // --start-gauge: Heat at a fraction of 100
    public void SetGaugeFraction(float fraction)
    {
        _heat = (int)MathF.Round(Math.Clamp(fraction, 0, 1) * 100);
        PublishGauge();
    }
    public int WildfireStacks => _wildfireStacks;
    public bool SummonActive => world.CurrentTime < _summonUntil;
    public float SummonFactor => _summonBattery / 50f;
    public IReadOnlyList<(DateTime At, uint Attack)> PendingPetAttacks => _petAttacks;
    public bool EnforceRangeAndMovement { get; set; }
    public bool Moving { get; set; }

    public void Advance()
    {
        if (_nextTick == default)
            _nextTick = BaseTime.AddSeconds(MchPotency.DotTick);

        foreach (var actor in world.Actors)
            for (var i = 0; i < actor.Statuses.Length; ++i)
                if (actor.Statuses[i].ID != 0 && actor.Statuses[i].ExpireAt <= world.CurrentTime)
                {
                    var expired = (SID)actor.Statuses[i].ID;
                    world.Execute(new ActorState.OpStatus(actor.InstanceID, i, default));
                    if (actor != player && expired == SID.WildfireTarget && actor.InstanceID == _wildfireTargetID)
                        Detonate("expired");
                    if (actor != player)
                        continue;
                    if (expired is SID.Hypercharged or SID.FullMetalMachinist or SID.ExcavatorReady)
                    {
                        ++ProcsLost;
                        Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{expired}"));
                    }
                    else if (expired == SID.Reassembled)
                    {
                        ++ReassembleWasted;
                        Events.Add(FormattableString.Invariant($"reassemble_wasted,{Now():f2}"));
                    }
                    else if (expired == SID.Flamethrower)
                        _flamethrowerActive = false;
                }

        if (_overheatStacks > 0 && world.CurrentTime >= _overheatUntil)
        {
            OverheatedStacksLost += _overheatStacks;
            Events.Add(FormattableString.Invariant($"overheat_lost,{Now():f2},{_overheatStacks}"));
            _overheatStacks = 0;
        }

        while (world.CurrentTime >= _nextTick)
        {
            foreach (var (targetID, tick) in _dotTick)
                if (world.Actors.Find(targetID) is { IsDead: false, IsTargetable: true } target && target.FindStatus((uint)SID.Bioblaster, player.InstanceID) != null)
                    DotPotency += tick;
            _nextTick = _nextTick.AddSeconds(MchPotency.DotTick);
        }

        if (_flamethrowerActive && (Moving || Left(SID.Flamethrower) <= 0))
        {
            _flamethrowerActive = false;
            Remove(SID.Flamethrower);
        }
        while (_flamethrowerActive && world.CurrentTime >= _nextFlamethrowerTick)
        {
            DotPotency += Hit(AID.Flamethrower, null, MchPotency.FlamethrowerTick, out _) * Buffs();
            _nextFlamethrowerTick = _nextFlamethrowerTick.AddSeconds(1);
        }

        while (_petAttacks.Count > 0 && world.CurrentTime >= _petAttacks[0].At)
        {
            var attack = _petAttacks[0].Attack;
            _petAttacks.RemoveAt(0);
            PetAttack(attack);
        }

        PublishGauge();
    }

    public void ExecuteBestAction(AIHints hints)
    {
        if (player.IsDead)
            return;

        var entries = hints.ActionsToExecute.Entries;
        for (var i = 0; i < entries.Count; ++i)
            if (Upgrade(entries[i].Action) is var upgraded && upgraded != entries[i].Action)
            {
                var e = entries[i];
                entries[i] = new(upgraded, e.Target, e.Priority, e.Expire, e.Delay, e.CastTime, e.TargetPos, e.FacingAngle, e.Manual, e.Force);
            }
        if (ClientReject.Enabled)
            entries.RemoveAll(entry => !CanExecute(entry, jobRules: false));
        else
            entries.RemoveAll(entry => !CanExecute(entry));
        SearchControl.FilterSuppressed(hints.ActionsToExecute.Entries, world); // oracle-search: actions held by the search stay out of the queue
        var entry = hints.ActionsToExecute.FindBest(world, player, world.Client.Cooldowns, world.Client.AnimationLock, hints, frameStep, true);
        var definition = ActionDefinitions.Instance[entry.Action];
        if (entry.Action.ID == 0 || definition == null
            || MathF.Max(entry.Delay, MathF.Max(world.Client.AnimationLock, definition.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions))) > 0.001f)
            return;
        if (Irregular.Refuses("mch", entry, definition, world, player)) // --irregular lockout statuses, refused after the pick like the client
            return;
        if (ClientReject.Enabled && !CanExecute(entry))
        {
            ClientReject.Record("mch", entry.Action, world.CurrentTime, player);
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
            world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, GCDLength(entry.Action)))]));
        StartCooldown(definition);
        Complete(entry, definition);
    }

    // what the hotbar slot executes at this level: xan MCH.cs presses Gauss Round, Ricochet and Rook Autoturret and lets the client
    // replace them with Double Check, Checkmate and Automaton Queen
    private ActionID Upgrade(ActionID action)
    {
        if (action.Type != ActionType.Spell)
            return action;
        var level = player.Level;
        var upgraded = (AID)action.ID switch
        {
            AID.SplitShot when level >= 54 => AID.HeatedSplitShot,
            AID.SlugShot when level >= 60 => AID.HeatedSlugShot,
            AID.CleanShot when level >= 64 => AID.HeatedCleanShot,
            AID.HotShot when level >= 76 => AID.AirAnchor,
            AID.HeatBlast when level >= 68 => AID.BlazingShot,
            AID.SpreadShot when level >= 82 => AID.Scattergun,
            AID.GaussRound when level >= 92 => AID.DoubleCheck,
            AID.Ricochet when level >= 92 => AID.Checkmate,
            AID.RookAutoturret when level >= 80 => AID.AutomatonQueen,
            AID.RookOverdrive when level >= 80 => AID.QueenOverdrive,
            AID.ChainSaw when Left(SID.ExcavatorReady) > 0 => AID.Excavator,
            var same => same
        };
        return upgraded == (AID)action.ID ? action : ActionID.MakeSpell(upgraded);
    }

    // oracle-search: emulator state shown next to each decision of the deviation log
    private string DescribeForSearch() => FormattableString.Invariant($"heat={_heat} battery={_battery} overheat={_overheatStacks} wildfire={_wildfireStacks}");

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

        return (AID)entry.Action.ID switch
        {
            AID.HeatBlast or AID.BlazingShot or AID.AutoCrossbow => _overheatStacks > 0,
            AID.Hypercharge => _overheatStacks == 0 && (Left(SID.Hypercharged) > 0 || _heat >= 50),
            AID.Detonator => _wildfireTargetID != 0,
            AID.Wildfire => _wildfireTargetID == 0,
            AID.AutomatonQueen or AID.RookAutoturret => !SummonActive && _battery >= 50,
            AID.QueenOverdrive or AID.RookOverdrive => SummonActive,
            AID.Excavator => Left(SID.ExcavatorReady) > 0,
            AID.FullMetalField => Left(SID.FullMetalMachinist) > 0,
            AID.BarrelStabilizer => player.InCombat,
            _ => true
        };
    }

    // Heat Blast / Blazing Shot / Auto Crossbow recast in 1.5s; every other weaponskill on the 2.5s GCD
    private float GCDLength(ActionID action)
    {
        var baseRecast = (AID)action.ID is AID.HeatBlast or AID.BlazingShot or AID.AutoCrossbow ? 1500 : 2500;
        return ActionSpeed.GCDRounded(world.Client.PlayerStats.SkillSpeed, world.Client.PlayerStats.Haste, player.Level, baseRecast);
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

    // Heat Blast / Blazing Shot advance the charge recast of Gauss Round / Double Check and Ricochet / Checkmate
    private void CutCooldown(AID action)
    {
        var definition = ActionDefinitions.Instance.Spell(action);
        if (definition == null)
            return;
        var group = definition.MainCooldownGroup;
        var current = world.Client.Cooldowns[group];
        if (current.Total <= 0)
            return;
        var elapsed = current.Elapsed + CooldownCut;
        world.Execute(new ClientState.OpCooldown(false, [(group, elapsed >= current.Total ? new(0, 0) : new(elapsed, current.Total))]));
    }

    private void Complete(ActionQueue.Entry entry, ActionDefinition definition)
    {
        var targetID = entry.Target?.InstanceID ?? player.InstanceID;
        var animationLock = definition.InstantAnimLock + frameStep;
        world.Execute(new ClientState.OpAnimationLockChange(animationLock));
        var castEvent = new ActorCastEvent(entry.Action, targetID, animationLock, 1, entry.Target?.PosRot.XYZ() ?? entry.TargetPos, ++_sequence, _sequence, player.Rotation);
        world.Execute(new ActorState.OpCastEvent(player.InstanceID, castEvent));

        if (_flamethrowerActive && (AID)entry.Action.ID != AID.Flamethrower)
        {
            _flamethrowerActive = false;
            Remove(SID.Flamethrower);
        }
        if (entry.Target is { Type: ActorType.Enemy } hostile)
            _lastHostileTargetID = hostile.InstanceID;

        var potency = 0f;
        if (entry.Action.Type == ActionType.Spell)
        {
            var aid = (AID)entry.Action.ID;
            potency = Score(aid, entry.Target, definition);
            ApplySpell(aid, entry.Target);
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

    private float Buffs() => player.FindStatus(MedicatedStatus) != null ? MedicatedMultiplier : 1;

    private float Score(AID action, Actor? target, ActionDefinition definition)
    {
        var level = player.Level;
        var last = (AID)world.Client.ComboState.Action;
        var combo = action switch
        {
            AID.SlugShot or AID.HeatedSlugShot => last is AID.SplitShot or AID.HeatedSplitShot,
            AID.CleanShot or AID.HeatedCleanShot => last is AID.SlugShot or AID.HeatedSlugShot,
            _ => false
        };
        var potency = MchPotency.Of(action, level, combo);
        if (potency <= 0)
            return 0;
        if (_overheatStacks > 0 && MchPotency.IsSingleTargetWeaponskill(action))
            potency += MchPotency.OverheatedBonus;

        var multiplier = Buffs();
        if (definition.IsGCD)
        {
            if (MchPotency.GuaranteedCritDirect(action))
                multiplier *= CritDirectMultiplier;
            else if (Left(SID.Reassembled) > 0)
            {
                multiplier *= CritDirectMultiplier;
                Remove(SID.Reassembled);
                if (action is AID.Drill or AID.AirAnchor or AID.ChainSaw or AID.Excavator or AID.HotShot)
                    ++ReassembledTools;
                else
                    Events.Add(FormattableString.Invariant($"reassemble_weak,{Now():f2},{action}"));
            }
        }

        var total = Hit(action, target, potency, out var hitWildfireTarget) * multiplier;
        if (definition.IsGCD && hitWildfireTarget && _wildfireTargetID != 0 && _wildfireStacks < MchPotency.WildfireMaxHits)
        {
            ++_wildfireStacks;
            ++WildfireHits;
        }
        if (action == AID.Bioblaster)
            foreach (var enemy in world.Actors)
                if (enemy.Type == ActorType.Enemy && !enemy.IsDead && enemy.IsTargetable && InShape(MchPotency.Shape.Cone12, enemy, target))
                {
                    SetOn(enemy, SID.Bioblaster, MchPotency.BioblasterDuration);
                    _dotTick[enemy.InstanceID] = MchPotency.BioblasterTick * Buffs();
                }
        return total;
    }

    private bool InShape(MchPotency.Shape shape, Actor enemy, Actor? target)
    {
        var aim = target is { Type: ActorType.Enemy } ? player.DirectionTo(target) : player.Rotation.ToDirection();
        return shape switch
        {
            MchPotency.Shape.Cone12 => AIHints.TargetInAOECone(enemy, player.Position, 12, aim, 45.Degrees()),
            MchPotency.Shape.Line25 => AIHints.TargetInAOERect(enemy, player.Position, aim, 25, 2),
            MchPotency.Shape.Target5 => target != null && AIHints.TargetInAOECircle(enemy, target.Position, 5),
            _ => enemy == target
        };
    }

    private float Hit(AID action, Actor? target, float potency, out bool hitWildfireTarget)
    {
        hitWildfireTarget = false;
        var shape = MchPotency.ShapeOf(action);
        var targetAlive = target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true };
        if (shape == MchPotency.Shape.Single)
        {
            hitWildfireTarget = targetAlive && target!.InstanceID == _wildfireTargetID;
            return targetAlive ? potency : 0;
        }

        var falloff = MchPotency.Falloff(action);
        var total = 0f;
        var hits = 0;
        if (targetAlive && shape is MchPotency.Shape.Target5 or MchPotency.Shape.Line25)
        {
            total += potency;
            ++hits;
            hitWildfireTarget |= target!.InstanceID == _wildfireTargetID;
        }
        foreach (var enemy in world.Actors)
        {
            if (enemy.Type != ActorType.Enemy || enemy.IsDead || !enemy.IsTargetable || hits > 0 && enemy == target || !InShape(shape, enemy, target))
                continue;
            total += potency * (hits == 0 ? 1 : falloff);
            ++hits;
            hitWildfireTarget |= enemy.InstanceID == _wildfireTargetID;
        }
        return total;
    }

    private void ApplySpell(AID action, Actor? target)
    {
        var level = player.Level;
        var last = (AID)world.Client.ComboState.Action;
        switch (action)
        {
            case AID.SplitShot:
            case AID.HeatedSplitShot:
                if (level >= 30)
                    GainHeat(5, action);
                SetCombo(action);
                break;
            case AID.SlugShot:
            case AID.HeatedSlugShot:
                if (last is AID.SplitShot or AID.HeatedSplitShot)
                {
                    if (level >= 30)
                        GainHeat(5, action);
                    SetCombo(action);
                }
                else
                    SetCombo(AID.None);
                break;
            case AID.CleanShot:
            case AID.HeatedCleanShot:
                if (last is AID.SlugShot or AID.HeatedSlugShot)
                {
                    if (level >= 30)
                        GainHeat(5, action);
                    if (level >= 40)
                        GainBattery(10, action);
                }
                SetCombo(AID.None);
                break;
            case AID.SpreadShot:
                if (level >= 30)
                    GainHeat(5, action);
                break;
            case AID.Scattergun:
                GainHeat(10, action);
                break;

            case AID.HotShot:
                if (level >= 40)
                    GainBattery(20, action);
                break;
            case AID.AirAnchor:
                GainBattery(20, action);
                break;
            case AID.ChainSaw:
                GainBattery(20, action);
                if (level >= 96)
                    Set(SID.ExcavatorReady, ReadyDuration);
                break;
            case AID.Excavator:
                GainBattery(20, action);
                Remove(SID.ExcavatorReady);
                break;
            case AID.FullMetalField:
                Remove(SID.FullMetalMachinist);
                break;

            case AID.Hypercharge:
                ++Hypercharges;
                if (Left(SID.Hypercharged) > 0)
                    Remove(SID.Hypercharged);
                else
                    _heat = Math.Max(0, _heat - 50);
                _overheatStacks = 5;
                _overheatUntil = world.CurrentTime.AddSeconds(OverheatDuration);
                Set(SID.Overheated, OverheatDuration, 5);
                break;
            case AID.HeatBlast:
            case AID.BlazingShot:
            case AID.AutoCrossbow:
                ++OverheatedShots;
                if (action != AID.AutoCrossbow)
                {
                    CutCooldown(level >= 92 ? AID.DoubleCheck : AID.GaussRound);
                    CutCooldown(level >= 92 ? AID.Checkmate : AID.Ricochet);
                }
                if (--_overheatStacks <= 0)
                {
                    _overheatStacks = 0;
                    Remove(SID.Overheated);
                }
                else
                    Set(SID.Overheated, (float)(_overheatUntil - world.CurrentTime).TotalSeconds, _overheatStacks);
                break;

            case AID.Reassemble:
                if (Left(SID.Reassembled) > 0)
                {
                    ++ReassembleWasted;
                    Events.Add(FormattableString.Invariant($"reassemble_wasted,{Now():f2}"));
                }
                Set(SID.Reassembled, ReassembleDuration);
                break;
            case AID.BarrelStabilizer:
                Set(SID.Hypercharged, HyperchargedDuration);
                if (level >= 100)
                    Set(SID.FullMetalMachinist, HyperchargedDuration);
                break;
            case AID.Wildfire:
                if (target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true })
                {
                    ++Wildfires;
                    _wildfireTargetID = target.InstanceID;
                    _wildfireStacks = 0;
                    SetOn(target, SID.WildfireTarget, MchPotency.WildfireDuration);
                    Set(SID.WildfirePlayer, MchPotency.WildfireDuration);
                }
                break;
            case AID.Detonator:
                Detonate("detonator");
                break;

            case AID.AutomatonQueen:
            case AID.RookAutoturret:
            {
                ++Queens;
                QueenBattery += _battery;
                _summonBattery = _battery;
                _battery = 0;
                _petTargetID = _lastHostileTargetID;
                var queen = action == AID.AutomatonQueen;
                _petAttacks.Clear();
                if (queen)
                {
                    Schedule(RollerDashAt, MchPotency.RollerDash);
                    for (var i = 0; i < ArmPunches; ++i)
                        Schedule(FirstArmPunchAt + ArmPunchInterval * i, MchPotency.ArmPunch);
                    Schedule(PileBunkerAt, MchPotency.PileBunker);
                    if (level >= 86)
                        Schedule(CrownedColliderAt, MchPotency.CrownedCollider);
                }
                else
                {
                    for (var i = 0; i < VolleyFires; ++i)
                        Schedule(FirstVolleyFireAt + ArmPunchInterval * i, MchPotency.VolleyFire);
                    Schedule(RookOverloadAt, MchPotency.RookOverload);
                }
                // the gauge reports the pet until its last attack
                _summonUntil = _petAttacks[^1].At;
                break;
            }
            case AID.QueenOverdrive:
            case AID.RookOverdrive:
                _petAttacks.Clear();
                if (action == AID.QueenOverdrive)
                {
                    Schedule(OverdrivePileBunkerAt, MchPotency.PileBunker);
                    if (level >= 86)
                        Schedule(OverdriveCrownedColliderAt, MchPotency.CrownedCollider);
                }
                else
                    Schedule(OverdrivePileBunkerAt, MchPotency.RookOverload);
                _summonUntil = _petAttacks[^1].At;
                break;
            case AID.Flamethrower:
                Set(SID.Flamethrower, MchPotency.FlamethrowerDuration);
                _flamethrowerActive = true;
                _nextFlamethrowerTick = world.CurrentTime.AddSeconds(1);
                break;
        }
    }

    private void Schedule(float delay, uint attack) => _petAttacks.Add((world.CurrentTime.AddSeconds(delay), attack));

    private void PetAttack(uint attack)
    {
        var target = world.Actors.Find(_petTargetID);
        if (target is not { IsDead: false, IsTargetable: true })
        {
            // the queen keeps swinging at whatever the player is fighting now
            target = world.Actors.Find(_lastHostileTargetID);
            _petTargetID = _lastHostileTargetID;
        }
        if (target is not { IsDead: false, IsTargetable: true })
        {
            ++PetHitsLost;
            Events.Add(FormattableString.Invariant($"pet_hit_lost,{Now():f2},{attack}"));
            return;
        }
        onExecuted(new ActionID(ActionType.Spell, attack), target.InstanceID, false, MchPotency.PetAttack(attack) * SummonFactor * Buffs());
    }

    private void Detonate(string reason)
    {
        var target = world.Actors.Find(_wildfireTargetID);
        var landed = target is { IsDead: false, IsTargetable: true };
        var potency = landed ? _wildfireStacks * MchPotency.WildfirePerHit(player.Level) * Buffs() : 0;
        Events.Add(FormattableString.Invariant($"wildfire,{Now():f2},{reason},{_wildfireStacks}{(landed ? "" : ",lost")}"));
        if (target != null)
        {
            var slot = Array.FindIndex(target.Statuses, s => s.ID == (uint)SID.WildfireTarget && s.SourceID == player.InstanceID);
            if (slot >= 0)
                world.Execute(new ActorState.OpStatus(target.InstanceID, slot, default));
        }
        Remove(SID.WildfirePlayer);
        _wildfireTargetID = 0;
        _wildfireStacks = 0;
        // the explosion is the wildfire action's own hit
        if (landed)
            onExecuted(ActionID.MakeSpell(AID.Wildfire), target!.InstanceID, false, potency);
    }

    private void GainHeat(int amount, AID source)
    {
        var wasted = Math.Max(0, _heat + amount - 100);
        if (wasted > 0)
        {
            HeatOvercap += wasted;
            Events.Add(FormattableString.Invariant($"heat_overcap,{Now():f2},{source},{wasted}"));
        }
        _heat = Math.Min(100, _heat + amount);
    }

    private void GainBattery(int amount, AID source)
    {
        var wasted = Math.Max(0, _battery + amount - 100);
        if (wasted > 0)
        {
            BatteryOvercap += wasted;
            Events.Add(FormattableString.Invariant($"battery_overcap,{Now():f2},{source},{wasted}"));
        }
        _battery = Math.Min(100, _battery + amount);
    }

    private float Now() => (float)(world.CurrentTime - BaseTime).TotalSeconds;
    private float Left(SID status) => MathF.Max(0, (float)((player.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);

    private void SetCombo(AID action) => world.Execute(new ClientState.OpComboChange(action == AID.None ? default : new((uint)action, ComboDuration)));

    private void Set(SID status, float duration, int stacks = 0) => SetOn(player, status, duration, stacks);

    private void SetOn(Actor actor, SID status, float duration, int stacks = 0)
    {
        var slot = Array.FindIndex(actor.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot < 0)
            slot = Array.FindIndex(actor.Statuses, s => s.ID == 0);
        if (slot < 0)
            throw new InvalidOperationException("MCH simulation ran out of status slots.");
        world.Execute(new ActorState.OpStatus(actor.InstanceID, slot, new((uint)status, (ushort)stacks, world.CurrentTime.AddSeconds(duration), player.InstanceID)));
    }

    private void Remove(SID status)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot >= 0)
            world.Execute(new ActorState.OpStatus(player.InstanceID, slot, default));
    }

    // MachinistGauge lives at struct offset 0x08 onwards (ClientState.GetGauge copies GaugePayload.Low there); TimerActive bit 0 is
    // Overheated, bit 1 an active Automaton Queen / Rook Autoturret
    private unsafe void PublishGauge()
    {
        MachinistGauge gauge = default;
        gauge.Heat = (byte)_heat;
        gauge.Battery = (byte)_battery;
        var overheat = _overheatStacks > 0 ? (float)(_overheatUntil - world.CurrentTime).TotalMilliseconds : 0;
        var summon = SummonActive ? (float)(_summonUntil - world.CurrentTime).TotalMilliseconds : 0;
        gauge.OverheatTimeRemaining = (short)Math.Max(0, overheat);
        gauge.SummonTimeRemaining = (short)Math.Max(0, summon);
        gauge.LastSummonBatteryPower = (byte)_summonBattery;
        gauge.TimerActive = (byte)((_overheatStacks > 0 ? 1 : 0) | (SummonActive ? 2 : 0));
        var raw = (ulong*)Unsafe.AsPointer(ref gauge);
        world.Client.GaugePayload = new(sizeof(MachinistGauge) > 8 ? raw[1] : 0, sizeof(MachinistGauge) > 16 ? raw[2] : 0);
    }
}

// Value of the machinist resources still held when a scenario ends: each is what it adds over the filler GCDs it displaces.
internal static class MchTerminalValue
{
    // a combo GCD
    private const float Filler = 320;

    public static float Estimate(WorldState world, Actor player, MchCombatState combat)
    {
        float Left(SID sid) => player.FindStatus((uint)sid, player.InstanceID) is ActorStatus s ? MathF.Max(0, (float)(s.ExpireAt - world.CurrentTime).TotalSeconds) : 0;
        var level = player.Level;
        // five overheated shots in three GCD slots, plus the 75s they take off the Double Check / Checkmate recasts
        var hypercharge = 5 * (MchPotency.Of(level >= 68 ? AID.BlazingShot : AID.HeatBlast, level, false) + MchPotency.OverheatedBonus) - 3 * Filler
            + 5 * 2 * 15f / 30 * MchPotency.Of(level >= 92 ? AID.DoubleCheck : AID.GaussRound, level, false);
        var queenAt50 = MchPotency.PetAttack(MchPotency.RollerDash) + 3 * MchPotency.PetAttack(MchPotency.ArmPunch) + MchPotency.PetAttack(MchPotency.PileBunker)
            + (level >= 86 ? MchPotency.PetAttack(MchPotency.CrownedCollider) : 0);
        var value = combat.Heat / 50f * hypercharge + combat.Battery / 50f * queenAt50;
        if (Left(SID.Hypercharged) > 0)
            value += hypercharge;
        if (Left(SID.ExcavatorReady) > 0)
            value += MchPotency.Of(AID.Excavator, level, false) - Filler;
        if (Left(SID.FullMetalMachinist) > 0)
            value += MchPotency.Of(AID.FullMetalField, level, false) * MchCombatState.CritDirectMultiplier - Filler;
        foreach (var (_, attack) in combat.PendingPetAttacks)
            value += MchPotency.PetAttack(attack) * combat.SummonFactor;
        value += combat.WildfireStacks * MchPotency.WildfirePerHit(level);
        return value;
    }
}
