using System;
using BossMod;
using AID = BossMod.MNK.AID;
using SID = BossMod.MNK.SID;
using BeastChakraType = FFXIVClientStructs.FFXIV.Client.Game.Gauge.BeastChakraType;
using NadiFlags = FFXIVClientStructs.FFXIV.Client.Game.Gauge.NadiFlags;

namespace XanTimelineHarness;

// Deterministic action-result simulation for Monk, mirroring RprCombatState. Decisions come from production MNK.cs;
// this only models what the client would do with them: gauge, forms, fury stacks, beast chakra, nadi and the
// self-applied buffs MNK.cs reads back. No party attacks and no packet latency.
internal sealed class MnkCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float> onExecuted)
{
    // agents_mnk.md ranks GCD uptime, buff-window density and Phantom Rush count above raw potency, and unlike a
    // potency table these are measured rather than remembered, so they are what this simulation reports.
    public int ChakraOvercap { get; private set; }
    public int BlitzesUsed { get; private set; }
    public int PhantomRushes { get; private set; }
    public int PerfectBalancesUsed { get; private set; }
    public int ExpiredBlitzes { get; private set; }
    public int DroppedBeastChakra { get; private set; }
    // Perfect Balance that ran its 20s out without banking three beast chakra. DroppedBeastChakra only counts the
    // chakra actually banked, so a window that never landed a single GCD scores zero there while being the worst
    // possible outcome - this counter is the one that says how many charges the rotation threw away.
    public int WastedPerfectBalances { get; private set; }
    public int RiddleOfFireGCDs { get; private set; }
    // Potency that actually landed inside the Riddle of Fire window, and how many of the window's GCDs were opo
    // steps. The rotation cannot hold fury stacks back for the window (spending them late overwrites them), so the
    // only lever on "high potency inside Riddle of Fire" is which form the window opens on - these two counters are
    // what makes that lever measurable.
    public float RiddleOfFirePotency { get; private set; }
    public int OpoGCDs { get; private set; }
    public int RiddleOfFireOpoGCDs { get; private set; }
    // Leaping Opo/Bootshine actually spending an Opo-opo's Fury stack - the single highest potency GCD the job has
    // outside blitz and the replies. The split between the total and the in-Riddle-of-Fire count says whether the
    // Perfect Balance windows open with a fury banked (LO, DK, LO) or without one (DK, LO, DK).
    public int FuryOpoGCDs { get; private set; }
    public int RiddleOfFireFuryOpoGCDs { get; private set; }
    public int PerfectBalanceGCDs { get; private set; }
    public int PerfectBalanceFuryOpoGCDs { get; private set; }
    public int BrotherhoodGCDs { get; private set; }
    public int RepliesUsed { get; private set; }
    public int RepliesExpired { get; private set; }

    private readonly BeastChakraType[] _beast = new BeastChakraType[3];
    private int _chakra;
    private int _beastCount;
    private NadiFlags _nadi;
    private DateTime _blitzEnd;
    private int _opoFury;
    private int _raptorFury;
    private int _coeurlFury;
    // Deep Meditation grants a chakra on critical weaponskills, and Meditative Brotherhood adds the party's crits on
    // top. Both are random in game; a fixed cadence keeps runs reproducible while still overflowing during Brotherhood.
    private int _chakraProgress;
    private uint _sequence;
    private const int ChakraProgressPerCrit = 4;
    private const float BlitzDuration = 20f;
    private const float FormDuration = 30f;

    public int Chakra => _chakra;
    public int BeastCount => _beastCount;
    public NadiFlags Nadi => _nadi;

    // Greased Lightning shortens weaponskill recast by 5%% at 1, 10%% at 20, 15%% at 40 and 20%% at 76, and the client
    // reports it through the same haste field ActionSpeed.AdjustRecastMS multiplies by. Without it the harness ran
    // monk on a 2.50s GCD instead of ~2.00s, so a Riddle of Fire window held 8 GCDs instead of 10 and Perfect
    // Balance's three GCDs took 7.5s instead of 6.0s - every window-density number was measured against the wrong
    // clock.
    private int GreasedLightningHaste => player.Level >= 76 ? 80 : player.Level >= 40 ? 85 : player.Level >= 20 ? 90 : 95;

    private void SyncGreasedLightning()
    {
        var stats = world.Client.PlayerStats;
        var haste = GreasedLightningHaste;
        if (stats.Haste != haste)
            world.Execute(new ClientState.OpPlayerStatsChange(new(stats.SkillSpeed, stats.SpellSpeed, haste)));
    }

    public void Advance()
    {
        SyncGreasedLightning();
        foreach (var actor in world.Actors)
            for (var i = 0; i < actor.Statuses.Length; ++i)
                if (actor.Statuses[i].ID != 0 && actor.Statuses[i].ExpireAt <= world.CurrentTime)
                {
                    var expired = (SID)actor.Statuses[i].ID;
                    world.Execute(new ActorState.OpStatus(actor.InstanceID, i, default));
                    if (actor != player)
                        continue;
                    // Perfect Balance running out before the third beast chakra loses the ones already banked
                    if (expired == SID.PerfectBalance && _beastCount < 3)
                    {
                        ++WastedPerfectBalances;
                        DroppedBeastChakra += _beastCount;
                        ClearBeast();
                    }
                    else if (expired is SID.FiresRumination or SID.WindsRumination)
                    {
                        ++RepliesExpired;
                    }
                }

        if (_beastCount == 3 && _blitzEnd <= world.CurrentTime)
        {
            ++ExpiredBlitzes;
            DroppedBeastChakra += 3;
            ClearBeast();
        }

        PublishGauge();
    }

    public void ExecuteBestAction(AIHints hints)
    {
        if (player.IsDead)
            return;

        // The real client enforces job resources/form requirements after the shared queue chooses a candidate.
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
        if (Irregular.Refuses("mnk", entry, definition, world, player)) // --irregular lockout statuses, refused after the pick like the client
            return;
        if (ClientReject.Enabled && !CanExecute(entry))
        {
            ClientReject.Record("mnk", entry.Action, world.CurrentTime, player);
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
            world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, GCDLength(entry.Action, definition)))]));
        StartCooldown(definition);
        Complete(entry, definition);
    }

    // Set by the disengage driver; range is only enforced while it runs, so every other run stays byte-identical.
    public bool EnforceRangeAndMovement { get; set; }
    public bool Moving { get; set; }
    public int InterruptedCasts => 0; // monk has no casts

    // oracle-search: emulator state shown next to each decision of the deviation log
    private string DescribeForSearch() => FormattableString.Invariant($"chakra={_chakra} beast={_beastCount} opo={_opoFury} raptor={_raptorFury} coeurl={_coeurlFury}");

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

        return (AID)entry.Action.ID switch
        {
            // form-gated weaponskills: the form itself, Formless Fist, or Perfect Balance
            AID.TrueStrike or AID.RisingRaptor or AID.TwinSnakes or AID.FourPointFury => FormAllows(SID.RaptorForm),
            AID.SnapPunch or AID.PouncingCoeurl or AID.Demolish or AID.Rockbreaker => FormAllows(SID.CoeurlForm),
            // opo-opo weaponskills have no form requirement, only a damage bonus
            AID.Bootshine or AID.LeapingOpo or AID.DragonKick or AID.ArmOfTheDestroyer or AID.ShadowOfTheDestroyer => true,
            AID.MasterfulBlitz or AID.ElixirField or AID.ElixirBurst or AID.FlintStrike or AID.RisingPhoenix
                or AID.CelestialRevolution or AID.TornadoKick or AID.PhantomRush => _beastCount == 3,
            AID.PerfectBalance => _beastCount == 0,
            AID.SteelPeak or AID.ForbiddenChakra or AID.HowlingFist or AID.Enlightenment => _chakra >= 5,
            AID.FiresReply => Has(SID.FiresRumination),
            AID.WindsReply => Has(SID.WindsRumination),
            AID.EarthsReply => Has(SID.EarthsRumination),
            AID.SteeledMeditation or AID.InspiritedMeditation or AID.ForbiddenMeditation or AID.EnlightenedMeditation => _chakra < 5,
            _ => true
        };
    }

    private bool FormAllows(SID form) => Has(form) || Has(SID.FormlessFist) || Has(SID.PerfectBalance);

    private float GCDLength(ActionID action, ActionDefinition definition)
    {
        var stats = world.Client.PlayerStats;
        // the client sheet says Recast100ms=50 for Six-sided Star, i.e. a 5s recast that scales with skill speed
        var baseline = (AID)action.ID == AID.SixSidedStar ? 5000 : 2500;
        var speed = definition.Category == ActionCategory.Spell ? stats.SpellSpeed : stats.SkillSpeed;
        return ActionSpeed.GCDRounded(speed, stats.Haste, player.Level, baseline);
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
        if (definition.IsGCD && entry.Action.Type == ActionType.Spell)
        {
            var inFire = Has(SID.RiddleOfFire);
            if (inFire)
                ++RiddleOfFireGCDs;
            if (Has(SID.Brotherhood))
                ++BrotherhoodGCDs;
            var inPB = Has(SID.PerfectBalance);
            if (inPB)
                ++PerfectBalanceGCDs;
            if ((AID)entry.Action.ID is AID.Bootshine or AID.LeapingOpo or AID.DragonKick or AID.ArmOfTheDestroyer or AID.ShadowOfTheDestroyer)
            {
                ++OpoGCDs;
                if (inFire)
                    ++RiddleOfFireOpoGCDs;
            }
            if (_opoFury > 0 && (AID)entry.Action.ID is AID.Bootshine or AID.LeapingOpo)
            {
                ++FuryOpoGCDs;
                if (inFire)
                    ++RiddleOfFireFuryOpoGCDs;
                if (inPB)
                    ++PerfectBalanceFuryOpoGCDs;
            }
        }

        // scored before ApplySpell so the fury stacks are the ones the action is about to consume
        var potency = entry.Action.Type == ActionType.Spell
            ? MnkPotencyScorer.Estimate(world, player, (AID)entry.Action.ID, entry.Target, _opoFury, _raptorFury, _coeurlFury, _chakra)
            : 0f;

        if (Has(SID.RiddleOfFire))
            RiddleOfFirePotency += potency;

        if (entry.Action.Type == ActionType.Spell)
            ApplySpell((AID)entry.Action.ID, entry.Target, definition);
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

    private void ApplySpell(AID action, Actor? target, ActionDefinition definition)
    {
        var hitsEnemy = target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true }
            || IsSelfAOE(action) && HasNearbyEnemy();

        switch (action)
        {
            // ---- opo-opo step ----
            case AID.Bootshine:
            case AID.LeapingOpo:
                _opoFury = Math.Max(0, _opoFury - 1);
                AdvanceForm(SID.OpoOpoForm, SID.RaptorForm, BeastChakraType.OpoOpo);
                break;
            case AID.DragonKick:
                _opoFury = 1;
                AdvanceForm(SID.OpoOpoForm, SID.RaptorForm, BeastChakraType.OpoOpo);
                break;
            case AID.ArmOfTheDestroyer:
            case AID.ShadowOfTheDestroyer:
                AdvanceForm(SID.OpoOpoForm, SID.RaptorForm, BeastChakraType.OpoOpo);
                break;

            // ---- raptor step ----
            case AID.TrueStrike:
            case AID.RisingRaptor:
                _raptorFury = Math.Max(0, _raptorFury - 1);
                AdvanceForm(SID.RaptorForm, SID.CoeurlForm, BeastChakraType.Raptor);
                break;
            case AID.TwinSnakes:
                _raptorFury = 1;
                AdvanceForm(SID.RaptorForm, SID.CoeurlForm, BeastChakraType.Raptor);
                break;
            case AID.FourPointFury:
                AdvanceForm(SID.RaptorForm, SID.CoeurlForm, BeastChakraType.Raptor);
                break;

            // ---- coeurl step ----
            case AID.SnapPunch:
            case AID.PouncingCoeurl:
                _coeurlFury = Math.Max(0, _coeurlFury - 1);
                AdvanceForm(SID.CoeurlForm, SID.OpoOpoForm, BeastChakraType.Coeurl);
                break;
            case AID.Demolish:
                _coeurlFury = 2;
                AdvanceForm(SID.CoeurlForm, SID.OpoOpoForm, BeastChakraType.Coeurl);
                break;
            case AID.Rockbreaker:
                AdvanceForm(SID.CoeurlForm, SID.OpoOpoForm, BeastChakraType.Coeurl);
                break;

            // ---- masterful blitz ----
            case AID.ElixirField:
            case AID.ElixirBurst:
                ConsumeBlitz();
                _nadi |= NadiFlags.Lunar;
                break;
            case AID.FlintStrike:
            case AID.RisingPhoenix:
                ConsumeBlitz();
                _nadi |= NadiFlags.Solar;
                break;
            case AID.CelestialRevolution:
                ConsumeBlitz();
                if ((_nadi & NadiFlags.Lunar) == 0)
                    _nadi |= NadiFlags.Lunar;
                else
                    _nadi |= NadiFlags.Solar;
                break;
            case AID.TornadoKick:
            case AID.PhantomRush:
                ConsumeBlitz();
                ++PhantomRushes;
                _nadi = 0;
                break;

            // ---- buffs / resources ----
            case AID.PerfectBalance:
                ++PerfectBalancesUsed;
                Set(SID.PerfectBalance, 20, 3);
                break;
            case AID.FormShift:
                Set(SID.FormlessFist, FormDuration);
                break;
            case AID.RiddleOfFire:
                Set(SID.RiddleOfFire, 20);
                if (player.Level >= 100)
                    Set(SID.FiresRumination, 20);
                break;
            case AID.Brotherhood:
                Set(SID.Brotherhood, 20);
                Set(SID.MeditativeBrotherhood, 20);
                break;
            case AID.RiddleOfWind:
                Set(SID.RiddleOfWind, 15);
                if (player.Level >= 96)
                    Set(SID.WindsRumination, 15);
                break;
            case AID.RiddleOfEarth:
                Set(SID.RiddleOfEarth, 10);
                if (player.Level >= 64)
                    Set(SID.EarthsRumination, 30);
                break;
            case AID.FiresReply:
                Remove(SID.FiresRumination);
                Set(SID.FormlessFist, FormDuration);
                ++RepliesUsed;
                break;
            case AID.WindsReply:
                Remove(SID.WindsRumination);
                ++RepliesUsed;
                break;
            case AID.EarthsReply:
                Remove(SID.EarthsRumination);
                break;
            case AID.SixSidedStar:
                Set(SID.SixSidedStar, 5);
                break;
            case AID.SteeledMeditation:
            case AID.InspiritedMeditation:
            case AID.ForbiddenMeditation:
            case AID.EnlightenedMeditation:
                GainChakra(1);
                break;
            case AID.SteelPeak:
            case AID.ForbiddenChakra:
            case AID.HowlingFist:
            case AID.Enlightenment:
                // "Five chakra close upon execution" - during Brotherhood the gauge holds ten, so one spender does
                // not empty it
                _chakra = Math.Max(0, _chakra - 5);
                break;
            case AID.TrueNorth:
                Set(SID.TrueNorth, 10);
                break;
        }

        // Deep Meditation / Meditative Brotherhood: weaponskills that connect build chakra
        if (definition.IsGCD && hitsEnemy && !IsMeditation(action))
            GainChakraFromWeaponskill();
    }

    private static bool IsMeditation(AID action)
        => action is AID.SteeledMeditation or AID.InspiritedMeditation or AID.ForbiddenMeditation or AID.EnlightenedMeditation;

    private static bool IsSelfAOE(AID action)
        => action is AID.ArmOfTheDestroyer or AID.ShadowOfTheDestroyer or AID.FourPointFury or AID.Rockbreaker
            or AID.ElixirField or AID.ElixirBurst or AID.FlintStrike or AID.RisingPhoenix;

    // Perfect Balance banks a beast chakra instead of advancing the form; otherwise the combo steps forward.
    private void AdvanceForm(SID consumedForm, SID nextForm, BeastChakraType beast)
    {
        if (Has(SID.PerfectBalance))
        {
            // Masterful Blitz - and with it the beast chakra gauge - unlocks at 60; below that Perfect Balance only
            // lifts the form requirement, so nothing is banked and nothing can be dropped
            if (player.Level >= 60 && _beastCount < 3)
                _beast[_beastCount++] = beast;
            ConsumeStack(SID.PerfectBalance);
            if (_beastCount == 3)
            {
                _blitzEnd = world.CurrentTime.AddSeconds(BlitzDuration);
                Remove(SID.PerfectBalance);
            }
            return;
        }

        if (!Has(consumedForm) && Has(SID.FormlessFist))
            Remove(SID.FormlessFist);

        Remove(SID.OpoOpoForm);
        Remove(SID.RaptorForm);
        Remove(SID.CoeurlForm);
        Set(nextForm, FormDuration);
    }

    private void ConsumeBlitz()
    {
        ++BlitzesUsed;
        ClearBeast();
    }

    private void ClearBeast()
    {
        Array.Clear(_beast);
        _beastCount = 0;
        _blitzEnd = world.CurrentTime;
    }

    private void GainChakraFromWeaponskill()
    {
        if (player.Level < 15)
            return;

        // one crit in four for the player alone; Brotherhood adds the rest of the party's crits on top
        _chakraProgress += Has(SID.MeditativeBrotherhood) ? ChakraProgressPerCrit : 1;
        while (_chakraProgress >= ChakraProgressPerCrit)
        {
            _chakraProgress -= ChakraProgressPerCrit;
            GainChakra(1);
        }
    }

    // Brotherhood says "Allows the opening of up to ten chakra". Capping at five here made the gauge saturate every
    // burst, counted the excess as overcap, and meant MNK.cs never reached its Chakra >= 8 / >= 10 branches. The
    // excess is kept when the buff falls off - only new chakra are gated by the cap.
    private int ChakraCap => Has(SID.Brotherhood) ? 10 : 5;

    private void GainChakra(int amount)
    {
        var cap = Math.Max(ChakraCap, _chakra);
        ChakraOvercap += Math.Max(0, _chakra + amount - cap);
        _chakra = Math.Min(cap, _chakra + amount);
    }

    private bool HasNearbyEnemy()
    {
        foreach (var actor in world.Actors)
            if (actor.Type == ActorType.Enemy && !actor.IsDead && actor.IsTargetable && player.DistanceToHitbox(actor) <= 5)
                return true;
        return false;
    }

    private bool Has(SID status) => Left(status) > 0;
    private float Left(SID status) => MathF.Max(0, (float)((player.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);
    private int Stacks(SID status) => player.FindStatus((uint)status, player.InstanceID)?.Extra & 0xFF ?? 0;

    private void Set(SID status, float duration, int stacks = 0)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot < 0)
            slot = Array.FindIndex(player.Statuses, s => s.ID == 0);
        if (slot < 0)
            throw new InvalidOperationException("MNK simulation ran out of status slots.");
        world.Execute(new ActorState.OpStatus(player.InstanceID, slot, new((uint)status, (ushort)stacks, duration == float.MaxValue ? DateTime.MaxValue : world.CurrentTime.AddSeconds(duration), player.InstanceID)));
    }

    private void Remove(SID status)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot >= 0)
            world.Execute(new ActorState.OpStatus(player.InstanceID, slot, default));
    }

    private void ConsumeStack(SID status)
    {
        var remaining = Stacks(status) - 1;
        if (remaining > 0)
            Set(status, Left(status), remaining);
        else
            Remove(status);
    }

    // MonkGauge starts at struct offset 0x08, which is bit 0 of GaugePayload.Low:
    // 0x08 Chakra, 0x09..0x0B BeastChakra1..3, 0x0C BeastChakraStacks, 0x0D Nadi, 0x0E BlitzTimeRemaining (u16 ms).
    private void PublishGauge()
    {
        var blitzMs = _beastCount == 3 ? (ulong)Math.Clamp((_blitzEnd - world.CurrentTime).TotalMilliseconds, 0, BlitzDuration * 1000) : 0;
        var furyStacks = (ulong)(byte)(Math.Clamp(_opoFury, 0, 3) | Math.Clamp(_raptorFury, 0, 3) << 2 | Math.Clamp(_coeurlFury, 0, 3) << 4);
        world.Client.GaugePayload = new(
            (byte)_chakra
            | (ulong)(byte)_beast[0] << 8
            | (ulong)(byte)_beast[1] << 16
            | (ulong)(byte)_beast[2] << 24
            | furyStacks << 32
            | (ulong)(byte)_nadi << 40
            | blitzMs << 48,
            0);
    }
}
