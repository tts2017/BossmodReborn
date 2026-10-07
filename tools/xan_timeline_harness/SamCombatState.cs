using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BossMod;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using AID = BossMod.SAM.AID;
using SID = BossMod.SAM.SID;

namespace XanTimelineHarness;

// Deterministic action-result simulation for Samurai (7.5), in the style of GnbCombatState and DrgCombatState. Decisions come from
// production xan SAM.cs; this only models what the client does with them: the combos and Meikyo Shisui, Sen / Kenki / Meditation on
// the gauge, Fugetsu and Fuka (whose haste shortens the 1.8s Iaijutsu and Ogi Namikiri casts and the GCD), the Tsubame-gaeshi,
// Tendo, Ogi Namikiri and Zanshin states, and the Higanbana dot (ticked every 3s with the buffs snapshotted at the cast). Presses are
// scored when they land (a cast at its end) with Fugetsu and an expected crit (the guaranteed crits of Setsugekka and Namikiri at the
// critical multiplier), no damage rolls, so two runs are byte-identical.
internal sealed class SamCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float> onExecuted)
{
    public float DotPotency { get; private set; }
    public int KenkiOvercap { get; private set; }
    public int MeditationOvercap { get; private set; }
    public int SenOvercap { get; private set; }
    public int ProcsLost { get; private set; }
    public int NoFugetsuGCDs { get; private set; }
    public int DotGapFrames { get; private set; }
    public int Iaijutsu { get; private set; }
    public int Tsubame { get; private set; }
    public int Namikiri { get; private set; }
    public int InterruptedCasts { get; private set; }
    public List<string> Events { get; } = [];
    public DateTime BaseTime { get; init; }

    private int _kenki;
    private int _meditation;
    private SenFlags _sen;
    private bool _namikiriReady;
    private bool _hadFugetsu;
    private bool _hadDot;
    private uint _sequence;
    private ActionQueue.Entry? _casting;
    private DateTime _castFinish;
    private DateTime _nextTick;
    private DateTime _nextMeditateTick;
    private readonly Dictionary<ulong, float> _dotTick = [];

    private const float ComboDuration = 30f;
    private const float BuffDuration = 40f;
    private const float MeikyoDuration = 20f;
    private const float TendoDuration = 30f;
    private const float KaeshiDuration = 30f;
    private const float ReadyDuration = 30f;
    private const float EnhancedEnpiDuration = 15f;
    private const float MeditateDuration = 15f;
    private const float TrueNorthDuration = 10f;
    private const float SlidecastWindow = 0.5f;
    private const float MedicatedMultiplier = 1.06f;
    private const uint MedicatedStatus = 49;

    public int Kenki => _kenki;
    public int Meditation => _meditation;
    public SenFlags Sen => _sen;
    public bool NamikiriReady => _namikiriReady;
    public bool EnforceRangeAndMovement { get; set; }
    public bool Moving { get; set; }

    // --irregular: a lockout status lands mid-cast (iaijutsu); the cast is interrupted and the recast refunded, as with movement
    public void InterruptCast()
    {
        if (_casting == null)
            return;
        _casting = null;
        ++InterruptedCasts;
        world.Execute(new ActorState.OpCastInfo(player.InstanceID, null));
        world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, 0))]));
    }

    public void Advance()
    {
        if (_nextTick == default)
            _nextTick = BaseTime.AddSeconds(SamPotency.DotTick);

        foreach (var actor in world.Actors)
            for (var i = 0; i < actor.Statuses.Length; ++i)
                if (actor.Statuses[i].ID != 0 && actor.Statuses[i].ExpireAt <= world.CurrentTime)
                {
                    var expired = (SID)actor.Statuses[i].ID;
                    var stacks = actor.Statuses[i].Extra;
                    world.Execute(new ActorState.OpStatus(actor.InstanceID, i, default));
                    if (actor != player)
                        continue;
                    if (expired is SID.KaeshiGoken or SID.KaeshiSetsugekka or SID.TendoKaeshiGoken or SID.TendoKaeshiSetsugekka or SID.OgiNamikiriReady
                        or SID.ZanshinReady or SID.Tendo || expired == SID.MeikyoShisui && stacks > 0)
                    {
                        ++ProcsLost;
                        Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{expired}"));
                    }
                }

        while (world.CurrentTime >= _nextTick)
        {
            foreach (var (targetID, tick) in _dotTick)
                if (world.Actors.Find(targetID) is { IsDead: false, IsTargetable: true } target && target.FindStatus((uint)SID.Higanbana, player.InstanceID) != null)
                    DotPotency += tick;
            _nextTick = _nextTick.AddSeconds(SamPotency.DotTick);
        }

        if (Left(SID.Meditate) > 0 && world.CurrentTime >= _nextMeditateTick)
        {
            _nextMeditateTick = _nextMeditateTick.AddSeconds(3);
            if (player.InCombat)
            {
                GainKenki(10, AID.Meditate);
                if (player.Level >= 80)
                    GainMeditation();
            }
        }

        if (_casting is { } cast)
        {
            if (cast.Target != null && cast.Target != player && (cast.Target.IsDead || !cast.Target.IsTargetable))
            {
                _casting = null;
                world.Execute(new ActorState.OpCastInfo(player.InstanceID, null));
            }
            else if (Moving && world.CurrentTime < _castFinish.AddSeconds(-SlidecastWindow))
            {
                _casting = null;
                ++InterruptedCasts;
                world.Execute(new ActorState.OpCastInfo(player.InstanceID, null));
                world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, 0))]));
            }
            else if (world.CurrentTime >= _castFinish)
            {
                _casting = null;
                world.Execute(new ActorState.OpCastInfo(player.InstanceID, null));
                Complete(cast, ActionDefinitions.Instance[cast.Action]!, casted: true);
            }
        }

        var enemyUp = false;
        var dotUp = false;
        foreach (var actor in world.Actors)
            if (actor.Type == ActorType.Enemy && !actor.IsDead && actor.IsTargetable)
            {
                enemyUp = true;
                dotUp |= actor.FindStatus((uint)SID.Higanbana, player.InstanceID) != null;
            }
        if (enemyUp && _hadDot && !dotUp)
            ++DotGapFrames;

        SyncHaste();
        PublishGauge();
    }

    public void ExecuteBestAction(AIHints hints)
    {
        if (_casting != null || player.IsDead)
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
        if (Irregular.Refuses("sam", entry, definition, world, player)) // --irregular lockout statuses, refused after the pick like the client
            return;
        if (ClientReject.Enabled && !CanExecute(entry))
        {
            ClientReject.Record("sam", entry.Action, world.CurrentTime, player);
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
            world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, GCDLength()))]));
        StartCooldown(definition);

        var castTime = CastTime(definition);
        if (castTime > 0)
        {
            _casting = entry;
            _castFinish = world.CurrentTime.AddSeconds(castTime);
            world.Execute(new ActorState.OpCastInfo(player.InstanceID, new()
            {
                Action = entry.Action,
                TargetID = entry.Target?.InstanceID ?? player.InstanceID,
                Location = entry.Target?.PosRot.XYZ() ?? entry.TargetPos,
                Rotation = player.Rotation,
                TotalTime = castTime
            }));
        }
        else
        {
            Complete(entry, definition, casted: false);
        }
    }

    // oracle-search: emulator state shown next to each decision of the deviation log
    private string DescribeForSearch() => FormattableString.Invariant($"kenki={_kenki} meditation={_meditation}");

    // burst study (BurstControl.cs): the state shown next to a cycle and the emulator's own readiness rules for a starter
    public string BurstState() => DescribeForSearch();
    public bool BurstFeasible(ActionQueue.Entry entry) => CanExecute(entry);

    private bool CanExecute(ActionQueue.Entry entry, bool jobRules = true)
    {
        if (entry.Target != null && entry.Target != player && (entry.Target.IsDead || !entry.Target.IsTargetable))
            return false;
        var definition = ActionDefinitions.Instance[entry.Action];
        if (EnforceRangeAndMovement && definition != null)
        {
            if (entry.Target != null && entry.Target != player && definition.Range > 0 && player.DistanceToHitbox(entry.Target) > definition.Range)
                return false;
            if (Moving && CastTime(definition) > 0)
                return false;
        }
        if (!jobRules)
            return true;
        if (entry.Action.Type != ActionType.Spell)
            return true;

        var sen = SenCount();
        return (AID)entry.Action.ID switch
        {
            AID.Higanbana => sen == 1,
            AID.TenkaGoken => sen == 2 && Left(SID.Tendo) <= 0,
            AID.MidareSetsugekka => sen == 3 && Left(SID.Tendo) <= 0,
            AID.TendoGoken => sen == 2 && Left(SID.Tendo) > 0,
            AID.TendoSetsugekka => sen == 3 && Left(SID.Tendo) > 0,
            AID.KaeshiGoken => Left(SID.KaeshiGoken) > 0,
            AID.KaeshiSetsugekka => Left(SID.KaeshiSetsugekka) > 0,
            AID.TendoKaeshiGoken => Left(SID.TendoKaeshiGoken) > 0,
            AID.TendoKaeshiSetsugekka => Left(SID.TendoKaeshiSetsugekka) > 0,
            AID.OgiNamikiri => Left(SID.OgiNamikiriReady) > 0,
            AID.KaeshiNamikiri => _namikiriReady,
            AID.Zanshin => Left(SID.ZanshinReady) > 0 && _kenki >= 50,
            AID.HissatsuShinten or AID.HissatsuKyuten or AID.HissatsuSenei or AID.HissatsuGuren => _kenki >= 25,
            AID.HissatsuGyoten or AID.HissatsuYaten => _kenki >= 10,
            AID.Shoha => _meditation >= 3,
            AID.Hagakure => sen > 0,
            AID.Ikishoten => player.InCombat,
            _ => true
        };
    }

    private int SenCount() => ((_sen & SenFlags.Setsu) != 0 ? 1 : 0) + ((_sen & SenFlags.Getsu) != 0 ? 1 : 0) + ((_sen & SenFlags.Ka) != 0 ? 1 : 0);

    private float Haste => world.Client.PlayerStats.Haste;

    private float GCDLength() => ActionSpeed.GCDRounded(world.Client.PlayerStats.SkillSpeed, world.Client.PlayerStats.Haste, player.Level, 2500);

    private float CastTime(ActionDefinition definition)
    {
        if (definition.CastTime <= 0)
            return 0;
        var stats = world.Client.PlayerStats;
        return MathF.Floor(definition.CastTime * 1000 * ActionSpeed.SpeedStatToModifier(stats.SkillSpeed, player.Level) / 1000 * stats.Haste / 100) / 1000;
    }

    // Fuka is the SAM haste: 13% from 78 (10% before), applied to the weaponskill recast and cast times the client computes
    private void SyncHaste()
    {
        var haste = Left(SID.Fuka) > 0 ? 100 - (player.Level >= 78 ? 13 : 10) : 100;
        var stats = world.Client.PlayerStats;
        if (stats.Haste != haste)
            world.Execute(new ClientState.OpPlayerStatsChange(new(stats.SkillSpeed, stats.SpellSpeed, haste)));
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

    private void Complete(ActionQueue.Entry entry, ActionDefinition definition, bool casted)
    {
        var targetID = entry.Target?.InstanceID ?? player.InstanceID;
        var animationLock = (casted ? definition.CastAnimLock : definition.InstantAnimLock) + frameStep;
        world.Execute(new ClientState.OpAnimationLockChange(animationLock));
        var castEvent = new ActorCastEvent(entry.Action, targetID, animationLock, 1, entry.Target?.PosRot.XYZ() ?? entry.TargetPos, ++_sequence, _sequence, player.Rotation);
        world.Execute(new ActorState.OpCastEvent(player.InstanceID, castEvent));

        var potency = 0f;
        if (entry.Action.Type == ActionType.Spell)
        {
            var aid = (AID)entry.Action.ID;
            if (aid != AID.Meditate && Left(SID.Meditate) > 0)
                Remove(SID.Meditate);
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

    private bool IsComboAction(AID action) => action is AID.Hakaze or AID.Gyofu or AID.Jinpu or AID.Shifu or AID.Gekko or AID.Kasha or AID.Yukikaze
        or AID.Fuga or AID.Fuko or AID.Mangetsu or AID.Oka;

    // the combo condition the press meets, Meikyo Shisui included
    private bool Combo(AID action)
    {
        var last = (AID)world.Client.ComboState.Action;
        if (Stacks(SID.MeikyoShisui) > 0 && IsComboAction(action))
            return true;
        return action switch
        {
            AID.Jinpu or AID.Shifu or AID.Yukikaze => last is AID.Hakaze or AID.Gyofu,
            AID.Gekko => last == AID.Jinpu,
            AID.Kasha => last == AID.Shifu,
            AID.Mangetsu or AID.Oka => last is AID.Fuga or AID.Fuko,
            _ => false
        };
    }

    private static bool IsWeaponskill(AID action) => action is AID.Hakaze or AID.Gyofu or AID.Jinpu or AID.Shifu or AID.Gekko or AID.Kasha or AID.Yukikaze
        or AID.Enpi or AID.Fuga or AID.Fuko or AID.Mangetsu or AID.Oka or AID.Higanbana or AID.TenkaGoken or AID.MidareSetsugekka or AID.TendoGoken
        or AID.TendoSetsugekka or AID.KaeshiGoken or AID.KaeshiSetsugekka or AID.TendoKaeshiGoken or AID.TendoKaeshiSetsugekka or AID.OgiNamikiri or AID.KaeshiNamikiri;

    private float BuffMultiplier()
    {
        var m = 1f;
        if (Left(SID.Fugetsu) > 0)
            m *= player.Level >= 78 ? 1.13f : 1.10f;
        if (player.FindStatus(MedicatedStatus) != null)
            m *= MedicatedMultiplier;
        return m;
    }

    private float Score(AID action, Actor? target, ActionDefinition definition)
    {
        var level = player.Level;
        var combo = Combo(action);
        var basePotency = action == AID.Enpi && Left(SID.EnhancedEnpi) > 0 ? SamPotency.EnhancedEnpi(level) : SamPotency.Of(action, level, combo);
        if (basePotency <= 0)
            return 0;
        if (IsWeaponskill(action) && target is { Type: ActorType.Enemy } || SamPotency.ShapeOf(action) is not SamPotency.Shape.Single && IsWeaponskill(action))
        {
            if (_hadFugetsu && Left(SID.Fugetsu) <= 0)
            {
                ++NoFugetsuGCDs;
                Events.Add(FormattableString.Invariant($"no_fugetsu,{Now():f2},{action}"));
            }
        }

        var buffs = BuffMultiplier();
        var crit = SamPotency.GuaranteedCrit(action) ? DrgPotency.GuaranteedCrit(0) : 1;
        var total = Hit(action, target, basePotency) * buffs * crit;

        if (action == AID.Higanbana && target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true })
        {
            _dotTick[target.InstanceID] = SamPotency.HiganbanaTick(level) * buffs;
            _hadDot = true;
        }
        return total;
    }

    private float Hit(AID action, Actor? target, float potency)
    {
        var shape = SamPotency.ShapeOf(action);
        if (shape == SamPotency.Shape.Single)
            return target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true } ? potency : 0;

        var falloff = SamPotency.Falloff(action);
        var total = 0f;
        var hits = 0;
        var aim = target is { Type: ActorType.Enemy } ? player.DirectionTo(target) : player.Rotation.ToDirection();
        if (target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true } && shape is SamPotency.Shape.Cone8 or SamPotency.Shape.Line10)
        {
            total += potency;
            ++hits;
        }
        foreach (var enemy in world.Actors)
        {
            if (enemy.Type != ActorType.Enemy || enemy.IsDead || !enemy.IsTargetable || hits > 0 && enemy == target)
                continue;
            var hit = shape switch
            {
                SamPotency.Shape.Cone8 => AIHints.TargetInAOECone(enemy, player.Position, 8, aim, 60.Degrees()),
                SamPotency.Shape.Line10 => AIHints.TargetInAOERect(enemy, player.Position, aim, 10, 2),
                SamPotency.Shape.Self5 => AIHints.TargetInAOECircle(enemy, player.Position, 5),
                _ => AIHints.TargetInAOECircle(enemy, player.Position, 8)
            };
            if (!hit)
                continue;
            total += potency * (hits == 0 ? 1 : falloff);
            ++hits;
        }
        return total;
    }

    private void ApplySpell(AID action, Actor? target, ActionDefinition definition)
    {
        var level = player.Level;
        var combo = Combo(action);
        var meikyo = Stacks(SID.MeikyoShisui);
        if (meikyo > 0 && IsComboAction(action))
        {
            if (meikyo > 1)
                Set(SID.MeikyoShisui, Left(SID.MeikyoShisui), meikyo - 1);
            else
                Remove(SID.MeikyoShisui);
        }
        var kenki62 = level >= 62;

        switch (action)
        {
            case AID.Hakaze:
            case AID.Gyofu:
                if (kenki62)
                    GainKenki(5, action);
                SetCombo(action);
                break;
            case AID.Jinpu:
            case AID.Shifu:
                if (combo)
                {
                    Set(action == AID.Jinpu ? SID.Fugetsu : SID.Fuka, BuffDuration);
                    if (action == AID.Jinpu)
                        _hadFugetsu = true;
                    if (kenki62)
                        GainKenki(5, action);
                }
                SetCombo(combo && meikyo == 0 ? action : AID.None);
                break;
            case AID.Gekko:
            case AID.Kasha:
            case AID.Yukikaze:
                if (combo)
                {
                    GainKenki(action == AID.Yukikaze ? (kenki62 ? 15 : level >= 52 ? 10 : 0) : kenki62 ? 10 : level >= 52 ? 5 : 0, action);
                    GainSen(action == AID.Gekko ? SenFlags.Getsu : action == AID.Kasha ? SenFlags.Ka : SenFlags.Setsu);
                    if (meikyo > 0 && action == AID.Gekko)
                    {
                        Set(SID.Fugetsu, BuffDuration);
                        _hadFugetsu = true;
                    }
                    else if (meikyo > 0 && action == AID.Kasha)
                        Set(SID.Fuka, BuffDuration);
                }
                SetCombo(AID.None);
                break;
            case AID.Fuga:
            case AID.Fuko:
                GainKenki(action == AID.Fuko ? 10 : kenki62 ? 5 : 0, action);
                SetCombo(action);
                break;
            case AID.Mangetsu:
            case AID.Oka:
                if (combo)
                {
                    Set(action == AID.Mangetsu ? SID.Fugetsu : SID.Fuka, BuffDuration);
                    if (action == AID.Mangetsu)
                        _hadFugetsu = true;
                    GainKenki(kenki62 ? 10 : level >= 52 ? 5 : 0, action);
                    GainSen(action == AID.Mangetsu ? SenFlags.Getsu : SenFlags.Ka);
                }
                SetCombo(AID.None);
                break;
            case AID.Enpi:
                // Enpi leaves a running combo alone, like the other ranged weaponskills (Piercing Talon 13, Harpe 47 and Lightning Shot 206 uses
                // in real replays never cleared it). No replay of a SAM's own Enpi was available to check this one directly.
                Remove(SID.EnhancedEnpi);
                GainKenki(kenki62 ? 10 : level >= 52 ? 5 : 0, action);
                break;

            case AID.Higanbana:
                ++Iaijutsu;
                _sen = SenFlags.None;
                if (target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true })
                    SetOn(target, SID.Higanbana, SamPotency.HiganbanaDuration);
                if (level >= 80)
                    GainMeditation();
                break;
            case AID.TenkaGoken:
            case AID.MidareSetsugekka:
            case AID.TendoGoken:
            case AID.TendoSetsugekka:
                ++Iaijutsu;
                _sen = SenFlags.None;
                if (action is AID.TendoGoken or AID.TendoSetsugekka)
                    Remove(SID.Tendo);
                if (level >= 74)
                    Set(action switch
                    {
                        AID.TenkaGoken => SID.KaeshiGoken,
                        AID.MidareSetsugekka => SID.KaeshiSetsugekka,
                        AID.TendoGoken => SID.TendoKaeshiGoken,
                        _ => SID.TendoKaeshiSetsugekka
                    }, KaeshiDuration);
                if (level >= 80)
                    GainMeditation();
                break;
            case AID.KaeshiGoken:
            case AID.KaeshiSetsugekka:
            case AID.TendoKaeshiGoken:
            case AID.TendoKaeshiSetsugekka:
                ++Tsubame;
                Remove(action switch
                {
                    AID.KaeshiGoken => SID.KaeshiGoken,
                    AID.KaeshiSetsugekka => SID.KaeshiSetsugekka,
                    AID.TendoKaeshiGoken => SID.TendoKaeshiGoken,
                    _ => SID.TendoKaeshiSetsugekka
                });
                break;
            case AID.OgiNamikiri:
                ++Namikiri;
                Remove(SID.OgiNamikiriReady);
                _namikiriReady = true;
                GainMeditation();
                break;
            case AID.KaeshiNamikiri:
                _namikiriReady = false;
                break;

            case AID.MeikyoShisui:
                Set(SID.MeikyoShisui, MeikyoDuration, 3);
                if (level >= 100)
                    Set(SID.Tendo, TendoDuration);
                break;
            case AID.Ikishoten:
                GainKenki(50, action);
                if (level >= 90)
                    Set(SID.OgiNamikiriReady, ReadyDuration);
                if (level >= 96)
                    Set(SID.ZanshinReady, ReadyDuration);
                break;
            case AID.Zanshin:
                SpendKenki(50);
                Remove(SID.ZanshinReady);
                break;
            case AID.HissatsuShinten:
            case AID.HissatsuKyuten:
            case AID.HissatsuSenei:
            case AID.HissatsuGuren:
                SpendKenki(25);
                break;
            case AID.HissatsuGyoten:
                SpendKenki(10);
                break;
            case AID.HissatsuYaten:
                SpendKenki(10);
                Set(SID.EnhancedEnpi, EnhancedEnpiDuration);
                break;
            case AID.Shoha:
                _meditation = 0;
                break;
            case AID.Hagakure:
                GainKenki(10 * SenCount(), action);
                _sen = SenFlags.None;
                break;
            case AID.Meditate:
                Set(SID.Meditate, MeditateDuration);
                _nextMeditateTick = world.CurrentTime.AddSeconds(3);
                break;
            case AID.TrueNorth:
                Set(SID.TrueNorth, TrueNorthDuration);
                break;
        }
    }

    private void GainKenki(int amount, AID source)
    {
        if (amount <= 0)
            return;
        var wasted = Math.Max(0, _kenki + amount - 100);
        if (wasted > 0)
        {
            KenkiOvercap += wasted;
            Events.Add(FormattableString.Invariant($"kenki_overcap,{Now():f2},{source},{wasted}"));
        }
        _kenki = Math.Min(100, _kenki + amount);
    }

    private void SpendKenki(int amount) => _kenki = Math.Max(0, _kenki - amount);

    private void GainSen(SenFlags sen)
    {
        if ((_sen & sen) != 0)
        {
            ++SenOvercap;
            Events.Add(FormattableString.Invariant($"sen_overcap,{Now():f2},{sen}"));
        }
        _sen |= sen;
    }

    private void GainMeditation()
    {
        if (_meditation >= 3)
        {
            ++MeditationOvercap;
            Events.Add(FormattableString.Invariant($"meditation_overcap,{Now():f2}"));
        }
        _meditation = Math.Min(3, _meditation + 1);
    }

    private float Now() => (float)(world.CurrentTime - BaseTime).TotalSeconds;
    private float Left(SID status) => MathF.Max(0, (float)((player.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);
    private int Stacks(SID status) => player.FindStatus((uint)status, player.InstanceID) is ActorStatus s && s.ExpireAt > world.CurrentTime ? s.Extra & 0xFF : 0;

    private void SetCombo(AID action) => world.Execute(new ClientState.OpComboChange(action == AID.None ? default : new((uint)action, ComboDuration)));

    private void Set(SID status, float duration, int stacks = 0) => SetOn(player, status, duration, stacks);

    private void SetOn(Actor actor, SID status, float duration, int stacks = 0)
    {
        var slot = Array.FindIndex(actor.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot < 0)
            slot = Array.FindIndex(actor.Statuses, s => s.ID == 0);
        if (slot < 0)
            throw new InvalidOperationException("SAM simulation ran out of status slots.");
        world.Execute(new ActorState.OpStatus(actor.InstanceID, slot, new((uint)status, (ushort)stacks, duration == float.MaxValue ? DateTime.MaxValue : world.CurrentTime.AddSeconds(duration), player.InstanceID)));
    }

    private void Remove(SID status)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot >= 0)
            world.Execute(new ActorState.OpStatus(player.InstanceID, slot, default));
    }

    // SamuraiGauge lives at struct offset 0x08 onwards (ClientState.GetGauge copies GaugePayload.Low there)
    private unsafe void PublishGauge()
    {
        SamuraiGauge gauge = default;
        gauge.Kenki = (byte)_kenki;
        gauge.MeditationStacks = (byte)_meditation;
        gauge.SenFlags = _sen;
        gauge.Kaeshi = _namikiriReady ? KaeshiAction.Namikiri : default;
        var raw = (ulong*)Unsafe.AsPointer(ref gauge);
        world.Client.GaugePayload = new(sizeof(SamuraiGauge) > 8 ? raw[1] : 0, sizeof(SamuraiGauge) > 16 ? raw[2] : 0);
    }
}

// Value of the samurai resources still held when a scenario ends: each is what it adds over the filler GCD or nothing it displaces.
internal static class SamTerminalValue
{
    private const float Filler = 300;

    public static float Estimate(WorldState world, Actor player, SamCombatState combat)
    {
        float Left(SID sid) => player.FindStatus((uint)sid, player.InstanceID) is ActorStatus s ? MathF.Max(0, (float)(s.ExpireAt - world.CurrentTime).TotalSeconds) : 0;
        var level = player.Level;
        float P(AID aid) => SamPotency.Of(aid, level, true);
        var sen = ((combat.Sen & SenFlags.Setsu) != 0 ? 1 : 0) + ((combat.Sen & SenFlags.Getsu) != 0 ? 1 : 0) + ((combat.Sen & SenFlags.Ka) != 0 ? 1 : 0);
        var value = combat.Kenki * P(AID.HissatsuShinten) / 25 + combat.Meditation * P(AID.Shoha) / 3 + sen * (P(AID.MidareSetsugekka) - Filler) / 3;
        if (Left(SID.KaeshiSetsugekka) > 0)
            value += P(AID.KaeshiSetsugekka) - Filler;
        if (Left(SID.KaeshiGoken) > 0)
            value += P(AID.KaeshiGoken) - Filler;
        if (Left(SID.TendoKaeshiSetsugekka) > 0)
            value += P(AID.TendoKaeshiSetsugekka) - Filler;
        if (Left(SID.TendoKaeshiGoken) > 0)
            value += P(AID.TendoKaeshiGoken) - Filler;
        if (Left(SID.OgiNamikiriReady) > 0)
            value += 2 * P(AID.OgiNamikiri) - 2 * Filler;
        else if (combat.NamikiriReady)
            value += P(AID.KaeshiNamikiri) - Filler;
        if (Left(SID.ZanshinReady) > 0)
            value += P(AID.Zanshin);
        if (Left(SID.Tendo) > 0)
            value += 2 * (P(AID.TendoSetsugekka) - P(AID.MidareSetsugekka));
        return value;
    }
}
