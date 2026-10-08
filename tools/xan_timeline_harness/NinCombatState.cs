using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BossMod;
using BossMod.Autorotation.xan;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using AID = BossMod.NIN.AID;
using SID = BossMod.NIN.SID;

namespace XanTimelineHarness;

// Deterministic action-result simulation for Ninja, mirroring MnkCombatState. Decisions come from production NIN.cs; this only
// models what the client does with them. Rules that the tooltips leave open were measured from replays (nin-replay-scan, 8 alliance
// raid replays, 171 Kunai's Bane): Kunai's Bane and Meisui consume Shadow Walker, Zesho Meppo consumes Higi, Bhavacakra and Zesho
// Meppo consume Meisui, melee weaponskills end Raiju Ready, Phantom Kamaitachi does not spend a Bunshin stack while Raiju does, Ten Chi
// Jin grants Tenri Jindo Ready on the press. Target debuffs show up on the press and are re-applied when the server confirms them
// (Kunai's Bane +1.29s, Dokumori +1.07s), so they last 16.29s / 21.07s from the press; damage checks them at the press - a Spinning
// Edge pressed 0.5s after Kunai's Bane is already +10%, a Raiton pressed 16.25s after it still is, one at 16.5s is not.
internal sealed class NinCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float> onExecuted)
{
    public const float KunaiConfirmDelay = 1.29f;
    public const float DokumoriConfirmDelay = 1.07f;
    // party potency the other seven members gain from our 5% Dokumori, same rDPS proxy as the RPR harness uses for Arcane Circle
    private const float PartyPotencyPerSecond = 1450f;
    private const float PartyBurstMultiplier = 1.5f;
    private const float PotionMultiplier = 1.08f;

    private const float MudraGCD = 0.5f;
    private const float NinjutsuGCD = 1.5f;
    private const float FastTenChiJinGCD = 1.0f;
    private const float SlowTenChiJinGCD = 1.5f;
    private const float MudraDuration = 6f;
    private const float TenChiJinDuration = 6f;
    private const float ShadowWalkerDuration = 20f;
    private const float KassatsuDuration = 15f;
    private const float RaijuDuration = 30f;
    private const float BunshinDuration = 30f;
    private const float PhantomDuration = 45f;
    private const float MeisuiDuration = 30f;
    private const float HigiDuration = 30f;
    private const float TenriDuration = 30f;
    private const float KunaiDuration = 15f;
    private const float DokumoriDuration = 20f;
    private const float ComboDuration = 30f;

    public int NinkiOvercap { get; private set; }
    public int RaijuLost { get; private set; }
    public int Rabbits { get; private set; }
    public int NinjutsuRejectedFrames { get; private set; }
    public int InvalidTenChiJinFrames { get; private set; }
    public int TenChiJinIncomplete { get; private set; }
    public int MudraTimeouts { get; private set; }
    public int MudraCapFrames { get; private set; }
    public float KunaiPotency { get; private set; }
    public int KunaiGCDs { get; private set; }
    public float PartyDokumoriValue { get; private set; }
    public int GCDsExecuted { get; private set; }
    public int OpenerDokumoriGCD { get; private set; } = -1;
    public int OpenerKunaiGCD { get; private set; } = -1;
    public int RaitonFirstBursts { get; private set; }
    public int KassatsuFirstBursts { get; private set; }
    public List<string> KunaiWindows { get; } = [];
    // time-stamped anomalies (mudra timeouts, rabbits, lost Raiju) for the trace
    public List<string> Events { get; } = [];

    public bool EnforceRangeAndMovement { get; set; }
    public bool Moving { get; set; }
    public int InterruptedCasts => 0; // ninja has no casts
    // scenario time zero (the pull), for the Kunai's Bane window traces
    public DateTime BaseTime { get; set; }

    private int _ninki;
    private int _kazematoi;
    private int _mudraParam;
    private int _mudraCount;
    private DateTime _mudraExpire;
    private int _tcjParam;
    private int _tcjSteps;
    private uint _sequence;
    private readonly List<(ulong Target, uint Sid, DateTime At, DateTime Expire)> _refreshes = [];
    private DateTime _lastDokumoriPress;
    private DateTime _lastRaitonPress;
    // a Kunai's Bane pressed with neither Kassatsu up nor a Raiton just before it; a Kassatsu pressed shortly after makes it Kassatsu first
    private DateTime _unclassifiedKunai;
    private const float KassatsuAfterKunaiLimit = 3.0f;
    // the open Kunai's Bane window being traced: its end and the GCDs pressed inside it
    private DateTime _traceEnd;
    private readonly List<string> _trace = [];

    public int Ninki => _ninki;
    public int Kazematoi => _kazematoi;

    // --start-gauge: Ninki at a fraction of 100
    public void SetGaugeFraction(float fraction) => Initialize((int)MathF.Round(Math.Clamp(fraction, 0, 1) * 100), _kazematoi);

    public void Initialize(int ninki = 0, int kazematoi = 0)
    {
        _ninki = ninki;
        _kazematoi = kazematoi;
        PublishGauge();
    }

    // Increase Attack Speed (Lv45) is reported by the client as 15% haste, which ActionSpeed.GCDRounded multiplies in.
    private void SyncHaste()
    {
        var stats = world.Client.PlayerStats;
        var haste = player.Level >= 45 ? 85 : 100;
        if (stats.Haste != haste)
            world.Execute(new ClientState.OpPlayerStatsChange(new(stats.SkillSpeed, stats.SpellSpeed, haste)));
    }

    public void Advance()
    {
        SyncHaste();
        var now = world.CurrentTime;

        // server confirmation of a target debuff restarts its timer
        for (var i = _refreshes.Count - 1; i >= 0; --i)
        {
            var r = _refreshes[i];
            if (r.At > now)
                continue;
            _refreshes.RemoveAt(i);
            var target = world.Actors.Find(r.Target);
            if (target != null && !target.IsDead)
                SetOn(target, r.Sid, r.Expire, 0);
        }

        foreach (var actor in world.Actors)
            for (var i = 0; i < actor.Statuses.Length; ++i)
                if (actor.Statuses[i].ID != 0 && actor.Statuses[i].ExpireAt <= now)
                {
                    var expired = actor.Statuses[i].ID;
                    var extra = actor.Statuses[i].Extra & 0xFF;
                    world.Execute(new ActorState.OpStatus(actor.InstanceID, i, default));
                    if (actor != player)
                        continue;
                    if (expired == (uint)SID.TenChiJin)
                    {
                        if (_tcjSteps < 3)
                            ++TenChiJinIncomplete;
                        _tcjParam = 0;
                        _tcjSteps = 0;
                    }
                    else if (expired == (uint)SID.RaijuReady)
                    {
                        // timed out: whatever stacks were left were never spent
                        RaijuLost += extra;
                        Events.Add(FormattableString.Invariant($"raiju_expired,{(now - BaseTime).TotalSeconds:f2},{extra}"));
                    }
                }

        if (_mudraCount > 0 && _mudraExpire <= now)
        {
            ++MudraTimeouts;
            Events.Add(FormattableString.Invariant($"mudra_timeout,{(now - BaseTime).TotalSeconds:f2},param={_mudraParam}"));
            ClearMudra();
        }

        if (player.InCombat && MudraChargesNow() >= MudraMaxCharges() && _mudraCount == 0 && AnyEnemyTargetable())
            ++MudraCapFrames;

        // rDPS proxy for the party side of Dokumori (5% on the target for seven other players)
        if (AnyEnemyWithOurStatus((uint)SID.Dokumori) || AnyEnemyWithOurStatus((uint)SID.VulnerabilityUp))
        {
            var partyBurst = player.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || player.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
            PartyDokumoriValue += PartyPotencyPerSecond * (NinPotency.DokumoriBonus - 1) * frameStep * (partyBurst ? PartyBurstMultiplier : 1f);
        }

        if (_traceEnd != default && now > _traceEnd)
            FlushTrace();

        PublishGauge();
    }

    public void ExecuteBestAction(AIHints hints)
    {
        if (player.IsDead)
            return;

        var rejectedNinjutsu = false;
        var invalidTcj = false;
        hints.ActionsToExecute.Entries.RemoveAll(entry =>
        {
            var ok = CanExecute(entry, out var ninjutsuRejected, out var tcjInvalid, jobRules: !ClientReject.Enabled);
            rejectedNinjutsu |= ninjutsuRejected;
            invalidTcj |= tcjInvalid;
            return !ok;
        });
        if (rejectedNinjutsu)
            ++NinjutsuRejectedFrames;
        if (invalidTcj)
            ++InvalidTenChiJinFrames;

        SearchControl.FilterSuppressed(hints.ActionsToExecute.Entries, world); // oracle-search: actions held by the search stay out of the queue
        var entry = hints.ActionsToExecute.FindBest(world, player, world.Client.Cooldowns, world.Client.AnimationLock, hints, frameStep, true);
        var definition = ActionDefinitions.Instance[entry.Action];
        if (entry.Action.ID == 0 || definition == null
            || MathF.Max(entry.Delay, MathF.Max(world.Client.AnimationLock, definition.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions))) > 0.001f)
            return;
        if (Irregular.Refuses("nin", entry, definition, world, player)) // --irregular lockout statuses, refused after the pick like the client
            return;
        if (ClientReject.Enabled && !CanExecute(entry, out _, out _))
        {
            ClientReject.Record("nin", entry.Action, world.CurrentTime, player);
            return;
        }
        if (SearchControl.Active) // oracle-search: the search may swap the module pick for an alternative or hold it back
        {
            if (SearchControl.Decide(hints.ActionsToExecute, entry, world, player, hints, frameStep, e => CanExecute(e, out _, out _), DescribeForSearch) is not { } decided)
                return;
            entry = decided;
            definition = ActionDefinitions.Instance[entry.Action]!;
        }

        var aid = entry.Action.Type == ActionType.Spell ? (AID)entry.Action.ID : AID.None;
        if (definition.IsGCD)
            world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, GCDLength(aid)))]));
        StartCooldown(aid, definition);
        Complete(entry, definition, aid);
    }

    public void Finish() => FlushTrace();

    // oracle-search: emulator state shown next to each decision of the deviation log
    private string DescribeForSearch() => FormattableString.Invariant($"ninki={_ninki} kazematoi={_kazematoi} mudra={_mudraCount} tcj={_tcjSteps}");

    // burst study (BurstControl.cs): the state shown next to a cycle and the emulator's own readiness rules for a starter
    public string BurstState() => DescribeForSearch();
    public bool BurstFeasible(ActionQueue.Entry entry) => CanExecute(entry, out _, out _);

    private bool CanExecute(ActionQueue.Entry entry, out bool ninjutsuRejected, out bool tcjInvalid, bool jobRules = true)
    {
        ninjutsuRejected = false;
        tcjInvalid = false;
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

        var aid = (AID)entry.Action.ID;
        var tcjActive = Has(SID.TenChiJin);
        if (IsTenChiJinNinjutsu(aid))
        {
            if (!tcjActive)
                return false;
            var valid = TenChiJinStepValid(aid);
            tcjInvalid = !valid;
            return valid;
        }
        // Ten Chi Jin turns the three mudra into its ninjutsu and locks every other weaponskill until it finishes
        if (tcjActive && ActionDefinitions.Instance[entry.Action]?.IsGCD == true)
            return false;

        switch (aid)
        {
            case AID.Ten1:
            case AID.Chi1:
            case AID.Jin1:
                return true; // charge readiness is the cooldown group's job
            case AID.Ten2:
            case AID.Chi2:
            case AID.Jin2:
                return _mudraCount > 0 || Has(SID.Kassatsu);
            case AID.FumaShuriken:
            case AID.Katon:
            case AID.Raiton:
            case AID.Hyoton:
            case AID.Huton:
            case AID.Doton:
            case AID.Suiton:
            case AID.HyoshoRanryu:
            case AID.GokaMekkyaku:
            case AID.Ninjutsu:
            case AID.RabbitMedium:
            {
                if (_mudraCount == 0)
                    return false;
                var resolved = ResolveNinjutsu();
                // an invalid mudra string turns any ninjutsu press into Rabbit Medium; a valid one only executes as itself
                if (resolved == AID.RabbitMedium || aid is AID.Ninjutsu or AID.RabbitMedium || aid == resolved)
                    return true;
                ninjutsuRejected = true;
                return false;
            }
            case AID.KunaisBane:
            case AID.TrickAttack:
                return Has(SID.ShadowWalker) || Has(SID.Hidden);
            case AID.TenChiJin:
                return !Has(SID.Kassatsu) && _mudraCount == 0;
            case AID.Meisui:
                return Has(SID.ShadowWalker) && player.InCombat;
            case AID.Bunshin:
            case AID.Bhavacakra:
            case AID.HellfrogMedium:
                return _ninki >= 50;
            case AID.ZeshoMeppo:
            case AID.DeathfrogMedium:
                return _ninki >= 50 && Has(SID.Higi);
            case AID.PhantomKamaitachi:
                return Has(SID.PhantomKamaitachiReady);
            case AID.FleetingRaiju:
            case AID.ForkedRaiju:
                return Stacks(SID.RaijuReady) > 0;
            case AID.TenriJindo:
                return Has(SID.TenriJindoReady);
            case AID.Hide:
                return !player.InCombat;
            default:
                return true;
        }
    }

    private float GCDLength(AID aid)
    {
        if (IsMudra(aid))
            return MudraGCD;
        if (IsNinjutsu(aid))
            return NinjutsuGCD;
        if (aid is AID.FumaTen or AID.FumaChi or AID.FumaJin or AID.TCJKaton or AID.TCJRaiton or AID.TCJHyoton)
            return FastTenChiJinGCD;
        if (aid is AID.TCJHuton or AID.TCJDoton or AID.TCJSuiton)
            return SlowTenChiJinGCD;
        var stats = world.Client.PlayerStats;
        return ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, player.Level);
    }

    private void StartCooldown(AID aid, ActionDefinition definition)
    {
        // a mudra only spends a charge when it opens a sequence without Kassatsu; the follow-up mudra use the GCD alone
        if (aid is AID.Ten1 or AID.Chi1 or AID.Jin1 && (_mudraCount > 0 || Has(SID.Kassatsu)))
            return;

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

    private void Complete(ActionQueue.Entry entry, ActionDefinition definition, AID pressed)
    {
        var target = entry.Target;
        var targetID = target?.InstanceID ?? player.InstanceID;
        var animationLock = definition.InstantAnimLock + frameStep;
        world.Execute(new ClientState.OpAnimationLockChange(animationLock));
        var castEvent = new ActorCastEvent(entry.Action, targetID, animationLock, 1, target?.PosRot.XYZ() ?? entry.TargetPos, ++_sequence, _sequence, player.Rotation);
        world.Execute(new ActorState.OpCastEvent(player.InstanceID, castEvent));

        var potency = 0f;
        if (entry.Action.Type == ActionType.Item)
        {
            var quantity = world.Client.GetInventoryItemQuantity(entry.Action.ID);
            world.Execute(new ClientState.OpInventoryChange(entry.Action.ID, quantity > 0 ? quantity - 1 : 0));
            var item = Service.LuminaRow<Lumina.Excel.Sheets.Item>(entry.Action.ID % 500000)!.Value;
            var data = item.ItemAction.Value.DataHQ;
            Set((SID)data[0], data[2], data[1] + 10000);
        }
        else if (entry.Action.Type == ActionType.Spell)
        {
            // a ninjutsu press executes whatever the mudra string forms
            var aid = IsNinjutsu(pressed) ? ResolveNinjutsu() : pressed;
            potency = Resolve(aid, target, definition.IsGCD);
        }

        PublishGauge();
        onExecuted(entry.Action, targetID, definition.IsGCD, potency);
    }

    // Applies one executed action and returns the potency it dealt (all targets, all multipliers).
    private float Resolve(AID aid, Actor? target, bool gcd)
    {
        _ninkiSource = aid;
        var now = world.CurrentTime;
        var level = player.Level;
        var comboLast = (AID)world.Client.ComboState.Action;
        var kassatsu = Has(SID.Kassatsu);
        var kazematoi = _kazematoi > 0;
        var meisui = Has(SID.Meisui);
        var bunshin = Stacks(SID.Bunshin);
        var ninjutsu = IsNinjutsu(aid) || IsTenChiJinNinjutsu(aid);
        var combo = aid switch
        {
            AID.GustSlash => comboLast == AID.SpinningEdge,
            AID.AeolianEdge or AID.ArmorCrush => comboLast == AID.GustSlash,
            AID.HakkeMujinsatsu => comboLast == AID.DeathBlossom,
            _ => false
        };

        // ---- damage, scored with the statuses as they were at the press ----
        var potency = 0f;
        var basePotency = NinPotencyScorer.Base(aid, level, combo, kazematoi, meisui);
        if (basePotency > 0 && aid != AID.RabbitMedium)
        {
            var self = (Has(49) ? PotionMultiplier : 1f) * (ninjutsu && kassatsu && !IsTenChiJinNinjutsu(aid) ? NinPotency.KassatsuBonus : 1f);
            foreach (var (hit, falloff) in HitTargets(aid, target))
            {
                var p = basePotency * falloff * self * TargetMultiplier(hit);
                potency += p;
                if (hit == target && HasOurStatus(hit, (uint)SID.KunaisBane) || hit == target && HasOurStatus(hit, (uint)SID.TrickAttack))
                    KunaiPotency += p;
            }
        }

        // ---- Bunshin: every weaponskill except Phantom Kamaitachi sends the shadow, spends a stack and grants 5 ninki ----
        if (bunshin > 0 && IsWeaponskill(aid) && aid != AID.PhantomKamaitachi)
        {
            var area = aid is AID.DeathBlossom or AID.HakkeMujinsatsu;
            foreach (var (hit, _) in area ? HitTargets(aid, target) : SingleTarget(target))
            {
                var p = (area ? NinPotency.BunshinArea : NinPotency.BunshinMelee) * (Has(49) ? PotionMultiplier : 1f) * TargetMultiplier(hit);
                potency += p;
                if (hit == target && HasOurStatus(hit, (uint)SID.KunaisBane))
                    KunaiPotency += p;
            }
            GainNinki(5);
            if (bunshin > 1)
                Set(SID.Bunshin, Left(SID.Bunshin), bunshin - 1);
            else
                Remove(SID.Bunshin);
        }

        if (gcd && !IsMudra(aid))
        {
            if (player.InCombat)
                ++GCDsExecuted;
            if (target != null && HasOurStatus(target, (uint)SID.KunaisBane))
                ++KunaiGCDs;
            if (_traceEnd != default && now <= _traceEnd)
                _trace.Add(Short(aid));
        }

        // ---- effects ----
        switch (aid)
        {
            case AID.Ten1:
            case AID.Ten2:
                AddMudra(1);
                break;
            case AID.Chi1:
            case AID.Chi2:
                AddMudra(2);
                break;
            case AID.Jin1:
            case AID.Jin2:
                AddMudra(3);
                break;

            case AID.RabbitMedium:
                ++Rabbits;
                Events.Add(FormattableString.Invariant($"rabbit,{(now - BaseTime).TotalSeconds:f2}"));
                FinishNinjutsu(kassatsu);
                break;
            case AID.FumaShuriken:
            case AID.Katon:
            case AID.Hyoton:
            case AID.HyoshoRanryu:
            case AID.GokaMekkyaku:
                FinishNinjutsu(kassatsu);
                break;
            case AID.Raiton:
                FinishNinjutsu(kassatsu);
                GainRaiju();
                _lastRaitonPress = now;
                break;
            case AID.Huton:
            case AID.Suiton:
                FinishNinjutsu(kassatsu);
                Set(SID.ShadowWalker, ShadowWalkerDuration);
                break;
            case AID.Doton:
                FinishNinjutsu(kassatsu);
                break;

            case AID.FumaTen:
            case AID.FumaChi:
            case AID.FumaJin:
            case AID.TCJKaton:
            case AID.TCJRaiton:
            case AID.TCJHyoton:
            case AID.TCJHuton:
            case AID.TCJDoton:
            case AID.TCJSuiton:
                AdvanceTenChiJin(aid);
                if (aid == AID.TCJRaiton)
                    GainRaiju();
                if (aid is AID.TCJSuiton or AID.TCJHuton)
                    Set(SID.ShadowWalker, ShadowWalkerDuration);
                break;
            case AID.TenChiJin:
                _tcjParam = 0;
                _tcjSteps = 0;
                Set(SID.TenChiJin, TenChiJinDuration, 0);
                if (level >= 100)
                    Set(SID.TenriJindoReady, TenriDuration);
                break;

            case AID.Kassatsu:
                Set(SID.Kassatsu, KassatsuDuration);
                // Kassatsu weaved right after Kunai's Bane still puts Hyosho Ranryu in the window: the burst is Kassatsu first
                if (_unclassifiedKunai != default && (now - _unclassifiedKunai).TotalSeconds < KassatsuAfterKunaiLimit)
                {
                    ++KassatsuFirstBursts;
                    _unclassifiedKunai = default;
                    if (_trace.Count >= 2)
                        _trace[1] = "KFafter";
                }
                break;
            case AID.KunaisBane:
            case AID.TrickAttack:
            {
                var sid = aid == AID.KunaisBane ? (uint)SID.KunaisBane : (uint)SID.TrickAttack;
                if (target != null)
                {
                    SetOn(target, sid, now.AddSeconds(KunaiDuration), 0);
                    _refreshes.Add((target.InstanceID, sid, now.AddSeconds(KunaiConfirmDelay), now.AddSeconds(KunaiConfirmDelay + KunaiDuration)));
                }
                Remove(SID.ShadowWalker);
                Remove(SID.Hidden);
                if (player.InCombat && OpenerKunaiGCD < 0)
                    OpenerKunaiGCD = GCDsExecuted;
                var raitonFirst = !kassatsu && (now - _lastRaitonPress).TotalSeconds < 6;
                _unclassifiedKunai = default;
                if (kassatsu)
                    ++KassatsuFirstBursts;
                else if (raitonFirst)
                    ++RaitonFirstBursts;
                else
                    _unclassifiedKunai = now;
                FlushTrace();
                _traceEnd = now.AddSeconds(KunaiConfirmDelay + KunaiDuration);
                _trace.Add(FormattableString.Invariant($"t={(now - BaseTime).TotalSeconds:f2}"));
                _trace.Add(kassatsu ? "KF" : raitonFirst ? "RF" : "noKass");
                break;
            }
            case AID.Dokumori:
            case AID.Mug:
            {
                var sid = aid == AID.Dokumori ? (uint)SID.Dokumori : (uint)SID.VulnerabilityUp;
                foreach (var (hit, _) in HitTargets(aid, target))
                {
                    SetOn(hit, sid, now.AddSeconds(DokumoriDuration), 0);
                    _refreshes.Add((hit.InstanceID, sid, now.AddSeconds(DokumoriConfirmDelay), now.AddSeconds(DokumoriConfirmDelay + DokumoriDuration)));
                }
                if (aid == AID.Dokumori)
                {
                    GainNinki(40);
                    if (level >= 96)
                        Set(SID.Higi, HigiDuration);
                }
                if (player.InCombat && OpenerDokumoriGCD < 0)
                    OpenerDokumoriGCD = GCDsExecuted;
                _lastDokumoriPress = now;
                break;
            }
            case AID.Meisui:
                Remove(SID.ShadowWalker);
                GainNinki(50);
                if (level >= 88)
                    Set(SID.Meisui, MeisuiDuration);
                break;
            case AID.Bunshin:
                SpendNinki(50);
                Set(SID.Bunshin, BunshinDuration, 5);
                if (level >= 82)
                    Set(SID.PhantomKamaitachiReady, PhantomDuration);
                break;
            case AID.PhantomKamaitachi:
                Remove(SID.PhantomKamaitachiReady);
                GainNinki(10);
                break;
            case AID.FleetingRaiju:
            case AID.ForkedRaiju:
            {
                var stacks = Stacks(SID.RaijuReady);
                if (stacks > 1)
                    Set(SID.RaijuReady, Left(SID.RaijuReady), stacks - 1);
                else
                    Remove(SID.RaijuReady);
                GainNinki(5);
                break;
            }
            case AID.Bhavacakra:
            case AID.HellfrogMedium:
            case AID.ZeshoMeppo:
            case AID.DeathfrogMedium:
                SpendNinki(50);
                if (aid is AID.ZeshoMeppo or AID.DeathfrogMedium)
                    Remove(SID.Higi);
                if (aid is AID.Bhavacakra or AID.ZeshoMeppo)
                    Remove(SID.Meisui);
                break;
            case AID.TenriJindo:
                Remove(SID.TenriJindoReady);
                break;
            case AID.Hide:
                Set(SID.Hidden, float.MaxValue);
                ResetMudraCharges();
                break;
            case AID.TrueNorth:
                Set(SID.TrueNorth, 10);
                break;
        }

        // ---- weaponskill resources: ninki (Shukiho), Kazematoi, combo, Raiju Ready lost to melee weaponskills ----
        if (IsMeleeWeaponskill(aid))
        {
            var raiju = Stacks(SID.RaijuReady);
            if (raiju > 0)
            {
                RaijuLost += raiju;
                Events.Add(FormattableString.Invariant($"raiju_lost_to_melee,{(world.CurrentTime - BaseTime).TotalSeconds:f2},{aid},{raiju}"));
                Remove(SID.RaijuReady);
            }
        }
        switch (aid)
        {
            case AID.SpinningEdge:
                GainShukiho(5);
                world.Execute(new ClientState.OpComboChange(new((uint)aid, ComboDuration)));
                break;
            case AID.GustSlash:
                if (combo)
                    GainShukiho(5);
                world.Execute(new ClientState.OpComboChange(combo ? new((uint)aid, ComboDuration) : default));
                break;
            case AID.AeolianEdge:
            case AID.ArmorCrush:
                if (combo)
                    GainShukiho(level >= 84 ? 15 : level >= 78 ? 10 : 5);
                if (aid == AID.ArmorCrush && combo && level >= 54)
                    _kazematoi = Math.Min(5, _kazematoi + 2);
                if (aid == AID.AeolianEdge && _kazematoi > 0)
                    --_kazematoi;
                world.Execute(new ClientState.OpComboChange(default));
                break;
            case AID.DeathBlossom:
                GainShukiho(5);
                world.Execute(new ClientState.OpComboChange(new((uint)aid, ComboDuration)));
                break;
            case AID.HakkeMujinsatsu:
                if (combo)
                    GainShukiho(5);
                world.Execute(new ClientState.OpComboChange(default));
                break;
            case AID.ThrowingDagger:
                GainShukiho(5);
                break;
        }
        return potency;
    }

    private void GainShukiho(int amount)
    {
        if (player.Level >= 62)
            GainNinki(amount);
    }

    private void GainRaiju()
    {
        if (player.Level < 90)
            return;
        Set(SID.RaijuReady, RaijuDuration, Math.Min(3, Stacks(SID.RaijuReady) + 1));
    }

    private AID _ninkiSource;

    private void GainNinki(int amount)
    {
        var over = Math.Max(0, _ninki + amount - 100);
        if (over > 0)
            Events.Add(FormattableString.Invariant($"ninki_overcap,{(world.CurrentTime - BaseTime).TotalSeconds:f2},{_ninkiSource},{over}"));
        NinkiOvercap += over;
        _ninki = Math.Min(100, _ninki + amount);
    }

    private void SpendNinki(int amount) => _ninki = Math.Max(0, _ninki - amount);

    // ---- mudra ----
    private void AddMudra(int mudra)
    {
        if (_mudraCount < 3)
            _mudraParam |= mudra << (2 * _mudraCount);
        ++_mudraCount;
        _mudraExpire = world.CurrentTime.AddSeconds(MudraDuration);
        Set(SID.Mudra, MudraDuration, _mudraParam);
    }

    private void FinishNinjutsu(bool kassatsu)
    {
        ClearMudra();
        if (kassatsu)
            Remove(SID.Kassatsu);
    }

    private void ClearMudra()
    {
        _mudraParam = 0;
        _mudraCount = 0;
        _mudraExpire = default;
        Remove(SID.Mudra);
    }

    // The ninjutsu the current mudra string forms: the last mudra picks it, a repeated mudra (or a fourth) turns it into Rabbit Medium.
    private AID ResolveNinjutsu()
    {
        var m0 = _mudraParam & 3;
        var m1 = (_mudraParam >> 2) & 3;
        var m2 = (_mudraParam >> 4) & 3;
        if (_mudraCount == 0 || _mudraCount > 3 || _mudraCount >= 2 && m1 == m0 || _mudraCount == 3 && (m2 == m0 || m2 == m1))
            return AID.RabbitMedium;
        var kassatsu = Has(SID.Kassatsu);
        return _mudraCount switch
        {
            1 => AID.FumaShuriken,
            2 => m1 switch
            {
                1 => kassatsu && Unlocked(AID.GokaMekkyaku) ? AID.GokaMekkyaku : AID.Katon,
                2 => AID.Raiton,
                _ => kassatsu && Unlocked(AID.HyoshoRanryu) ? AID.HyoshoRanryu : AID.Hyoton
            },
            _ => m2 switch
            {
                1 => AID.Huton,
                2 => AID.Doton,
                _ => AID.Suiton
            }
        };
    }

    // ---- Ten Chi Jin: the same 2-bit encoding as the mudra status, each action spends the mudra it replaces ----
    private static int TenChiJinMudra(AID aid) => aid switch
    {
        AID.FumaTen or AID.TCJKaton or AID.TCJHuton => 1,
        AID.FumaChi or AID.TCJRaiton or AID.TCJDoton => 2,
        _ => 3
    };

    private static int TenChiJinStep(AID aid) => aid switch
    {
        AID.FumaTen or AID.FumaChi or AID.FumaJin => 0,
        AID.TCJKaton or AID.TCJRaiton or AID.TCJHyoton => 1,
        _ => 2
    };

    private bool TenChiJinStepValid(AID aid)
    {
        if (TenChiJinStep(aid) != _tcjSteps)
            return false;
        var mudra = TenChiJinMudra(aid);
        for (var i = 0; i < _tcjSteps; ++i)
            if (((_tcjParam >> (2 * i)) & 3) == mudra)
                return false;
        return true;
    }

    private void AdvanceTenChiJin(AID aid)
    {
        _tcjParam |= TenChiJinMudra(aid) << (2 * _tcjSteps);
        ++_tcjSteps;
        if (_tcjSteps >= 3)
        {
            Remove(SID.TenChiJin);
            _tcjParam = 0;
            _tcjSteps = 0;
        }
        else
        {
            Set(SID.TenChiJin, Left(SID.TenChiJin), _tcjParam);
        }
    }

    // ---- targets and multipliers ----
    private IEnumerable<(Actor Target, float Falloff)> SingleTarget(Actor? target)
    {
        if (target != null && target.Type == ActorType.Enemy && !target.IsDead && target.IsTargetable)
            yield return (target, 1f);
    }

    private IEnumerable<(Actor Target, float Falloff)> HitTargets(AID aid, Actor? target)
    {
        switch (aid)
        {
            case AID.DeathBlossom:
            case AID.HakkeMujinsatsu:
            case AID.Doton:
            case AID.TCJDoton:
                foreach (var e in Enemies())
                    if (player.DistanceToHitbox(e) <= 5)
                        yield return (e, 1f);
                break;
            case AID.Dokumori:
                foreach (var e in Enemies())
                    if (e == target || player.DistanceToHitbox(e) <= 8)
                        yield return (e, 1f);
                break;
            case AID.Katon:
            case AID.TCJKaton:
            case AID.GokaMekkyaku:
            case AID.Huton:
            case AID.TCJHuton:
            case AID.KunaisBane:
            case AID.PhantomKamaitachi:
            case AID.HellfrogMedium:
            case AID.DeathfrogMedium:
            case AID.TenriJindo:
                if (target == null)
                    break;
                var radius = aid is AID.HellfrogMedium or AID.DeathfrogMedium ? 6 : 5;
                foreach (var e in Enemies())
                    if (e == target || e.Position.InCircle(target.Position, radius + e.HitboxRadius))
                        yield return (e, e == target || aid != AID.TenriJindo ? 1f : 0.5f);
                break;
            default:
                foreach (var hit in SingleTarget(target))
                    yield return hit;
                break;
        }
    }

    private IEnumerable<Actor> Enemies()
    {
        foreach (var a in world.Actors)
            if (a.Type == ActorType.Enemy && !a.IsDead && a.IsTargetable)
                yield return a;
    }

    private float TargetMultiplier(Actor target)
    {
        var m = 1f;
        if (HasOurStatus(target, (uint)SID.KunaisBane) || HasOurStatus(target, (uint)SID.TrickAttack))
            m *= NinPotency.KunaiBonus;
        if (HasOurStatus(target, (uint)SID.Dokumori) || HasOurStatus(target, (uint)SID.VulnerabilityUp))
            m *= NinPotency.DokumoriBonus;
        return m;
    }

    private bool HasOurStatus(Actor target, uint sid)
    {
        foreach (ref readonly var s in target.Statuses.AsSpan())
            if (s.ID == sid && s.SourceID == player.InstanceID && s.ExpireAt > world.CurrentTime)
                return true;
        return false;
    }

    private bool AnyEnemyWithOurStatus(uint sid)
    {
        foreach (var e in Enemies())
            if (HasOurStatus(e, sid))
                return true;
        return false;
    }

    private bool AnyEnemyTargetable()
    {
        foreach (var _ in Enemies())
            return true;
        return false;
    }

    // ---- mudra charges (the Ten charge group) ----
    private int MudraMaxCharges() => ActionDefinitions.Instance.Spell(AID.Ten1)!.MaxChargesAtLevel(player.Level);

    private int MudraChargesNow()
    {
        var def = ActionDefinitions.Instance.Spell(AID.Ten1)!;
        if (player.Level < def.MinLevel)
            return 0;
        var cd = world.Client.Cooldowns[def.ActualMainCooldownGroup(world.Client.DutyActions)];
        return cd.Total <= 0 ? MudraMaxCharges() : Math.Clamp((int)MathF.Floor(cd.Elapsed / def.Cooldown + 0.001f), 0, MudraMaxCharges());
    }

    private void ResetMudraCharges()
    {
        var def = ActionDefinitions.Instance.Spell(AID.Ten1)!;
        world.Execute(new ClientState.OpCooldown(false, [(def.ActualMainCooldownGroup(world.Client.DutyActions), default)]));
    }

    // ---- classification ----
    private static bool IsMudra(AID aid) => aid is AID.Ten1 or AID.Ten2 or AID.Chi1 or AID.Chi2 or AID.Jin1 or AID.Jin2;
    private static bool IsNinjutsu(AID aid) => aid is AID.FumaShuriken or AID.Katon or AID.Raiton or AID.Hyoton or AID.Huton or AID.Doton or AID.Suiton or AID.HyoshoRanryu or AID.GokaMekkyaku or AID.RabbitMedium or AID.Ninjutsu;
    private static bool IsTenChiJinNinjutsu(AID aid) => aid is AID.FumaTen or AID.FumaChi or AID.FumaJin or AID.TCJKaton or AID.TCJRaiton or AID.TCJHyoton or AID.TCJHuton or AID.TCJDoton or AID.TCJSuiton;
    private static bool IsMeleeWeaponskill(AID aid) => aid is AID.SpinningEdge or AID.GustSlash or AID.AeolianEdge or AID.ArmorCrush or AID.DeathBlossom or AID.HakkeMujinsatsu;
    private static bool IsWeaponskill(AID aid) => IsMeleeWeaponskill(aid) || aid is AID.ThrowingDagger or AID.FleetingRaiju or AID.ForkedRaiju or AID.PhantomKamaitachi;
    private bool Unlocked(AID aid) => ActionDefinitions.Instance.Spell(aid) is { } d && player.Level >= d.MinLevel;

    private static string Short(AID aid) => aid switch
    {
        AID.SpinningEdge => "SE",
        AID.GustSlash => "GS",
        AID.AeolianEdge => "AE",
        AID.ArmorCrush => "AC",
        AID.HyoshoRanryu => "HYOSHO",
        AID.GokaMekkyaku => "GOKA",
        AID.Raiton => "RAITON",
        AID.Katon => "KATON",
        AID.Suiton => "SUITON",
        AID.Huton => "HUTON",
        AID.FumaShuriken => "FUMA",
        AID.FleetingRaiju or AID.ForkedRaiju => "RAIJU",
        AID.PhantomKamaitachi => "PK",
        AID.FumaTen or AID.FumaChi or AID.FumaJin => "tcjFUMA",
        AID.TCJRaiton => "tcjRAITON",
        AID.TCJSuiton => "tcjSUITON",
        AID.TCJKaton => "tcjKATON",
        AID.TCJHuton => "tcjHUTON",
        AID.TCJHyoton => "tcjHYOTON",
        AID.TCJDoton => "tcjDOTON",
        AID.RabbitMedium => "RABBIT",
        _ => aid.ToString()
    };

    private void FlushTrace()
    {
        if (_trace.Count > 0)
            KunaiWindows.Add(string.Join(' ', _trace));
        _trace.Clear();
        _traceEnd = default;
    }

    // ---- statuses ----
    private bool Has(SID status) => Left(status) > 0;
    private bool Has(uint status) => player.FindStatus(status) is ActorStatus s && s.ExpireAt > world.CurrentTime;
    private float Left(SID status) => MathF.Max(0, (float)((player.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);
    private int Stacks(SID status) => player.FindStatus((uint)status, player.InstanceID) is ActorStatus s && s.ExpireAt > world.CurrentTime ? s.Extra & 0xFF : 0;

    private void Set(SID status, float duration, int extra = 0)
        => SetOn(player, (uint)status, duration == float.MaxValue ? DateTime.MaxValue : world.CurrentTime.AddSeconds(duration), extra);

    private void SetOn(Actor actor, uint status, DateTime expire, int extra)
    {
        var slot = Array.FindIndex(actor.Statuses, s => s.ID == status && s.SourceID == player.InstanceID);
        if (slot < 0)
            slot = Array.FindIndex(actor.Statuses, s => s.ID == 0);
        if (slot < 0)
            throw new InvalidOperationException("NIN simulation ran out of status slots.");
        world.Execute(new ActorState.OpStatus(actor.InstanceID, slot, new(status, (ushort)extra, expire, player.InstanceID)));
    }

    private void Remove(SID status)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot >= 0)
            world.Execute(new ActorState.OpStatus(player.InstanceID, slot, default));
    }

    // NinjaGauge lives at struct offset 0x08 onwards; the same layout trick tools/nin_real_harness used.
    private unsafe void PublishGauge()
    {
        NinjaGauge gauge = default;
        gauge.Ninki = (byte)Math.Clamp(_ninki, 0, 100);
        gauge.Kazematoi = (byte)Math.Clamp(_kazematoi, 0, 5);
        var raw = (ulong*)Unsafe.AsPointer(ref gauge);
        var low = sizeof(NinjaGauge) > 8 ? raw[1] : 0;
        var high = sizeof(NinjaGauge) > 16 ? raw[2] : 0;
        world.Client.GaugePayload = new(low, high);
    }
}

// Value of the ninja resources still held when a scenario ends, so a run that banks a charge or a stack for later is not scored as if
// it had thrown it away. Each item is what it adds over the filler GCD it would displace.
internal static class NinTerminalValue
{
    public static float Estimate(WorldState world, Actor player, NinCombatState combat)
    {
        float Left(uint sid) => player.FindStatus(sid, player.InstanceID) is ActorStatus s ? MathF.Max(0, (float)(s.ExpireAt - world.CurrentTime).TotalSeconds) : 0;
        int Stacks(uint sid) => player.FindStatus(sid, player.InstanceID) is ActorStatus s && s.ExpireAt > world.CurrentTime ? s.Extra & 0xFF : 0;
        var value = 0f;
        var ten = ActionDefinitions.Instance.Spell(AID.Ten1)!;
        if (player.Level >= ten.MinLevel)
        {
            var cd = world.Client.Cooldowns[ten.ActualMainCooldownGroup(world.Client.DutyActions)];
            var max = ten.MaxChargesAtLevel(player.Level);
            var charges = cd.Total <= 0 ? max : MathF.Min(max, cd.Elapsed / ten.Cooldown);
            value += charges * 600;
        }
        if (Left((uint)SID.Kassatsu) > 0)
            value += 1270;
        value += Stacks((uint)SID.RaijuReady) * 280;
        if (Left((uint)SID.PhantomKamaitachiReady) > 0)
            value += 280;
        if (Left((uint)SID.TenriJindoReady) > 0)
            value += 1100;
        if (Left((uint)SID.Higi) > 0)
            value += 300;
        if (Left((uint)SID.Meisui) > 0)
            value += 150;
        value += combat.Ninki * 8;
        value += combat.Kazematoi * 100;
        return value;
    }
}
