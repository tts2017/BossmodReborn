using System;
using BossMod;
using AID = BossMod.GNB.AID;
using SID = BossMod.GNB.SID;

namespace XanTimelineHarness;

// Deterministic action-result simulation for Gunbreaker, mirroring MnkCombatState. Decisions come from production
// AkechiGNB.cs; this only models what the client would do with them: cartridges, the Gnashing Fang / Reign gauge
// combo, the 1-2-3 combo chain and the self-applied buffs AkechiGNB.cs reads back. No party attacks, no packet
// latency and no damage rolls, so two runs of the same build are byte-identical.
internal sealed class GnbCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float> onExecuted)
{
    // The metrics the GNB rotation is actually judged on: burst windows are worth far more than filler, so a build
    // that lands one more Gnashing Fang or Reign chain inside No Mercy beats one with marginally higher raw potency.
    public int CartridgeOvercap { get; private set; }
    public int NoMercyUses { get; private set; }
    public int NoMercyGCDs { get; private set; }
    public int GnashingFangChains { get; private set; }
    public int ReignChains { get; private set; }
    public int DoubleDowns { get; private set; }
    public int SonicBreaks { get; private set; }
    public int Bloodfests { get; private set; }
    public int ContinuationsUsed { get; private set; }
    public int ContinuationsExpired { get; private set; }
    public int ContinuationsOverwritten { get; private set; } // pending procs removed by another weaponskill (client behaviour, see ApplySpell)
    public int ReadyToBreakExpired { get; private set; }
    public int ReadyToReignExpired { get; private set; }
    public int BurstGCDsOutsideNoMercy { get; private set; }
    // split of the above: an opener is a burst GCD the rotation chose freely, so one outside No Mercy is its own
    // decision. Noble Blood and Lion Heart are forced follow-ups once Reign of Beasts is in, so one outside the
    // window usually means a downtime cut the chain, not that the rotation picked badly.
    public int BurstOpenersOutsideNoMercy { get; private set; }

    private int _ammo;
    private int _gunComboStep;
    private uint _sequence;

    private const float NoMercyDuration = 20f;
    private const float ContinuationDuration = 10f;
    private const float ReadyToBreakDuration = 30f;
    private const float ReadyToReignDuration = 30f;
    private const float BloodfestDuration = 30f;
    private const float ComboDuration = 30f;
    private const float AuroraDuration = 18f;
    private const float MeleeRadius = 5f;

    public int Ammo => _ammo;
    public int GunComboStep => _gunComboStep;
    // Gunbreaker has no casts, so nothing can be interrupted by movement.
    public int InterruptedCasts => 0;
    // Set by the disengage driver; range is only enforced while it runs, so every other run stays byte-identical.
    public bool EnforceRangeAndMovement { get; set; }
    public bool Moving { get; set; }

    // GNB_DEBUG_DEFS=1 dumps the cooldown/charge facts the rotation plans against, so charge assumptions in
    // AkechiGNB can be checked against the actual action data instead of inferred from behaviour.
    private static readonly bool DebugDefs = Environment.GetEnvironmentVariable("GNB_DEBUG_DEFS") == "1";
    private bool _defsDumped;

    private void DumpDefs()
    {
        _defsDumped = true;
        foreach (var aid in new[] { AID.GnashingFang, AID.NoMercy, AID.Bloodfest, AID.DoubleDown, AID.SonicBreak, AID.BowShock, AID.DangerZone, AID.BlastingZone })
        {
            var def = ActionDefinitions.Instance.Spell(aid);
            if (def == null)
                continue;
            Console.Error.WriteLine(FormattableString.Invariant(
                $"def {aid} minLevel={def.MinLevel} cooldown={def.Cooldown:f1} chargesAtCap={def.MaxChargesAtCap()} chargesAtLevel={def.MaxChargesAtLevel(player.Level)} isGCD={def.IsGCD}"));
        }
    }

    public void Advance()
    {
        if (DebugDefs && !_defsDumped)
            DumpDefs();

        foreach (var actor in world.Actors)
            for (var i = 0; i < actor.Statuses.Length; ++i)
                if (actor.Statuses[i].ID != 0 && actor.Statuses[i].ExpireAt <= world.CurrentTime)
                {
                    var expired = (SID)actor.Statuses[i].ID;
                    world.Execute(new ActorState.OpStatus(actor.InstanceID, i, default));
                    if (actor != player)
                        continue;
                    if (expired is SID.ReadyToRip or SID.ReadyToTear or SID.ReadyToGouge or SID.ReadyToBlast or SID.ReadyToRaze)
                        ++ContinuationsExpired;
                    else if (expired == SID.ReadyToBreak)
                        ++ReadyToBreakExpired;
                    else if (expired == SID.ReadyToReign)
                        ++ReadyToReignExpired;
                }

        PublishGauge();
    }

    public void ExecuteBestAction(AIHints hints)
    {
        if (player.IsDead)
            return;

        // The real client enforces job resources and gauge-combo steps after the shared queue chooses a candidate.
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
        if (Irregular.Refuses("gnb", entry, definition, world, player)) // --irregular lockout statuses, refused after the pick like the client
            return;
        if (ClientReject.Enabled && !CanExecute(entry))
        {
            ClientReject.Record("gnb", entry.Action, world.CurrentTime, player);
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
    private string DescribeForSearch() => FormattableString.Invariant($"ammo={_ammo} combo={_gunComboStep}");

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
            // cartridge spenders
            AID.BurstStrike or AID.FatedCircle => _ammo >= 1,
            AID.GnashingFang => _ammo >= 1 && _gunComboStep == 0,
            AID.DoubleDown => _ammo >= 2,
            // gauge combo steps: these persist across other GCDs, unlike the client combo chain
            AID.SavageClaw => _gunComboStep == 1,
            AID.WickedTalon => _gunComboStep == 2,
            // Reign does not wait for a Gnashing Fang combo to finish: it takes the gauge combo over (step 3) and the rest of the Gnashing
            // Fang combo is lost. Seen in replays: other Gunbreakers pressing Reign 2.5 s after Gnashing Fang or Savage Claw.
            AID.ReignOfBeasts => Has(SID.ReadyToReign),
            AID.NobleBlood => _gunComboStep == 3,
            AID.LionHeart => _gunComboStep == 4,
            // proc-gated weaponskills and continuations
            AID.SonicBreak => Has(SID.ReadyToBreak),
            AID.JugularRip => Has(SID.ReadyToRip),
            AID.AbdomenTear => Has(SID.ReadyToTear),
            AID.EyeGouge => Has(SID.ReadyToGouge),
            AID.Hypervelocity => Has(SID.ReadyToBlast),
            AID.FatedBrand => Has(SID.ReadyToRaze),
            _ => true
        };
    }

    private float GCDLength(ActionDefinition definition)
    {
        var stats = world.Client.PlayerStats;
        var speed = definition.Category == ActionCategory.Spell ? stats.SpellSpeed : stats.SkillSpeed;
        return ActionSpeed.GCDRounded(speed, stats.Haste, player.Level, 2500);
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
            if (Has(SID.NoMercy))
                ++NoMercyGCDs;
            else if (IsBurstGCD((AID)entry.Action.ID))
            {
                ++BurstGCDsOutsideNoMercy;
                if ((AID)entry.Action.ID is AID.DoubleDown or AID.SonicBreak or AID.ReignOfBeasts)
                    ++BurstOpenersOutsideNoMercy;
            }
        }

        // scored before ApplySpell so No Mercy and the combo state are the ones the action is about to use
        var potency = entry.Action.Type == ActionType.Spell
            ? GnbPotencyScorer.Estimate(world, player, (AID)entry.Action.ID, entry.Target)
            : 0f;

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

    // GCDs that have no reason to land outside No Mercy, so each one here is a real loss raw potency hides.
    // The Gnashing Fang chain is deliberately NOT in this list: agents_gnb.md places it in the three GCDs before
    // No Mercy so its Eye Gouge lands inside the window, which means counting it here would penalise correct play.
    private static bool IsBurstGCD(AID action)
        => action is AID.DoubleDown or AID.SonicBreak or AID.ReignOfBeasts or AID.NobleBlood or AID.LionHeart;

    private void ApplySpell(AID action, Actor? target, ActionDefinition definition)
    {
        var combo = (AID)world.Client.ComboState.Action;
        _ammoSource = action;

        // Replays (24 files, 2,542 Continuation procs of other Gunbreakers): a pending Ready to Rip/Tear/Gouge/Blast/Raze never survived
        // another weaponskill - every proc was either used directly or lost the moment the next weaponskill landed (Keen Edge, Sonic
        // Break, Lightning Shot...). So any weaponskill clears the pending procs before granting its own.
        if (definition.IsGCD)
            foreach (var proc in ContinuationProcs)
                if (Has(proc))
                {
                    Remove(proc);
                    ++ContinuationsOverwritten;
                }
        var hitsEnemy = target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true }
            || IsSelfAOE(action) && HasNearbyEnemy();

        switch (action)
        {
            // ---- single-target combo ----
            case AID.KeenEdge:
                SetCombo(hitsEnemy ? action : AID.None);
                break;
            case AID.BrutalShell:
                SetCombo(hitsEnemy && combo == AID.KeenEdge ? action : AID.None);
                break;
            case AID.SolidBarrel:
                SetCombo(AID.None);
                if (hitsEnemy && combo == AID.BrutalShell)
                    GainAmmo(1);
                break;

            // ---- AoE combo ----
            case AID.DemonSlice:
                SetCombo(hitsEnemy ? action : AID.None);
                break;
            case AID.DemonSlaughter:
                SetCombo(AID.None);
                if (hitsEnemy && combo == AID.DemonSlice)
                    GainAmmo(1);
                break;

            // ---- cartridge spenders ----
            case AID.BurstStrike:
                SpendAmmo(1);
                SetCombo(AID.None);
                if (Unlocked(AID.Hypervelocity))
                    Set(SID.ReadyToBlast, ContinuationDuration);
                break;
            case AID.FatedCircle:
                SpendAmmo(1);
                SetCombo(AID.None);
                if (Unlocked(AID.FatedBrand))
                    Set(SID.ReadyToRaze, ContinuationDuration);
                break;
            case AID.DoubleDown:
                SpendAmmo(2);
                SetCombo(AID.None);
                ++DoubleDowns;
                break;

            // ---- Gnashing Fang chain ----
            // Continuation itself is L70, so the Gnashing Fang chain grants nothing to follow up on below that.
            case AID.GnashingFang:
                SpendAmmo(1);
                _gunComboStep = 1;
                SetCombo(action);
                if (Unlocked(AID.JugularRip))
                    Set(SID.ReadyToRip, ContinuationDuration);
                break;
            case AID.SavageClaw:
                _gunComboStep = 2;
                SetCombo(action);
                if (Unlocked(AID.AbdomenTear))
                    Set(SID.ReadyToTear, ContinuationDuration);
                break;
            case AID.WickedTalon:
                _gunComboStep = 0;
                SetCombo(AID.None);
                if (Unlocked(AID.EyeGouge))
                    Set(SID.ReadyToGouge, ContinuationDuration);
                ++GnashingFangChains;
                break;

            // ---- Reign chain ----
            case AID.ReignOfBeasts:
                Remove(SID.ReadyToReign);
                _gunComboStep = 3;
                SetCombo(action);
                break;
            case AID.NobleBlood:
                _gunComboStep = 4;
                SetCombo(action);
                break;
            case AID.LionHeart:
                _gunComboStep = 0;
                SetCombo(AID.None);
                ++ReignChains;
                break;

            // ---- continuations ----
            case AID.JugularRip:
                Remove(SID.ReadyToRip);
                ++ContinuationsUsed;
                break;
            case AID.AbdomenTear:
                Remove(SID.ReadyToTear);
                ++ContinuationsUsed;
                break;
            case AID.EyeGouge:
                Remove(SID.ReadyToGouge);
                ++ContinuationsUsed;
                break;
            case AID.Hypervelocity:
                Remove(SID.ReadyToBlast);
                ++ContinuationsUsed;
                break;
            case AID.FatedBrand:
                Remove(SID.ReadyToRaze);
                ++ContinuationsUsed;
                break;

            // ---- buffs and resources ----
            case AID.NoMercy:
                ++NoMercyUses;
                Set(SID.NoMercy, NoMercyDuration);
                if (Unlocked(AID.SonicBreak))
                    Set(SID.ReadyToBreak, ReadyToBreakDuration);
                break;
            case AID.SonicBreak:
                Remove(SID.ReadyToBreak);
                SetCombo(AID.None);
                ++SonicBreaks;
                break;
            case AID.Bloodfest:
                ++Bloodfests;
                // Bloodfest grants a full magazine on top of what is already banked, and raises the cartridge cap
                // for 30s. The cap extension comes with Bloodfest itself (L76) - both AkechiGNB's MaxCartridges and
                // xan's GNB.MaxAmmo read the status with no level gate. Only Ready to Reign is the L100 addition.
                Set(SID.Bloodfest, BloodfestDuration);
                if (Unlocked(AID.ReignOfBeasts))
                    Set(SID.ReadyToReign, ReadyToReignDuration);
                GainAmmo(BaseMaxAmmo());
                break;
            case AID.Aurora:
                Set(SID.Aurora, AuroraDuration);
                break;
        }

        // Any other weaponskill breaks the 1-2-3 chain the same way the client does. Lightning Shot is not one of them: in real replays
        // (7 GNB, 206 uses) a running combo was never cleared by it.
        if (definition.IsGCD && !TouchesCombo(action))
            SetCombo(AID.None);
    }

    private static readonly SID[] ContinuationProcs = [SID.ReadyToRip, SID.ReadyToTear, SID.ReadyToGouge, SID.ReadyToBlast, SID.ReadyToRaze];

    private static bool TouchesCombo(AID action)
        => action is AID.KeenEdge or AID.BrutalShell or AID.SolidBarrel or AID.DemonSlice or AID.DemonSlaughter
            or AID.BurstStrike or AID.FatedCircle or AID.DoubleDown or AID.SonicBreak or AID.LightningShot
            or AID.GnashingFang or AID.SavageClaw or AID.WickedTalon
            or AID.ReignOfBeasts or AID.NobleBlood or AID.LionHeart;

    private static bool IsSelfAOE(AID action)
        => action is AID.DemonSlice or AID.DemonSlaughter or AID.FatedCircle or AID.DoubleDown or AID.BowShock;

    // Cartridge Charge (L30) gives 2, Cartridge Charge II (L88) gives 3.
    private int BaseMaxAmmo() => player.Level >= 88 ? 3 : player.Level >= 30 ? 2 : 0;

    // While the Bloodfest buff is up the magazine holds double, the same cap AkechiGNB's MaxCartridges reports.
    private int MaxAmmo()
    {
        var baseMax = BaseMaxAmmo();
        return baseMax > 0 && Has(SID.Bloodfest) ? baseMax * 2 : baseMax;
    }

    // GNB_DEBUG_OVERCAP=1 prints every wasted cartridge with the action that wasted it, so an overcap total can be
    // attributed to a source instead of guessed at.
    private static readonly bool DebugOvercap = Environment.GetEnvironmentVariable("GNB_DEBUG_OVERCAP") == "1";
    private AID _ammoSource;

    private void GainAmmo(int amount)
    {
        var max = MaxAmmo();
        if (max == 0)
            return;
        var wasted = Math.Max(0, _ammo + amount - max);
        if (wasted > 0 && DebugOvercap)
            Console.Error.WriteLine(FormattableString.Invariant(
                $"overcap lv={player.Level} src={_ammoSource} before={_ammo} gain={amount} max={max} wasted={wasted} bloodfest_buff={Has(SID.Bloodfest)}"));
        CartridgeOvercap += wasted;
        _ammo = Math.Min(max, _ammo + amount);
    }

    private void SpendAmmo(int amount) => _ammo = Math.Max(0, _ammo - amount);

    private bool Unlocked(AID action) => ActionDefinitions.Instance.Spell(action) is { } def && player.Level >= def.MinLevel;

    private bool HasNearbyEnemy()
    {
        foreach (var actor in world.Actors)
            if (actor.Type == ActorType.Enemy && !actor.IsDead && actor.IsTargetable && player.DistanceToHitbox(actor) <= MeleeRadius)
                return true;
        return false;
    }

    private bool Has(SID status) => Left(status) > 0;
    private float Left(SID status) => MathF.Max(0, (float)((player.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);

    private void SetCombo(AID action) => world.Execute(new ClientState.OpComboChange(action == AID.None ? default : new((uint)action, ComboDuration)));

    private void Set(SID status, float duration, int stacks = 0)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot < 0)
            slot = Array.FindIndex(player.Statuses, s => s.ID == 0);
        if (slot < 0)
            throw new InvalidOperationException("GNB simulation ran out of status slots.");
        world.Execute(new ActorState.OpStatus(player.InstanceID, slot, new((uint)status, (ushort)stacks, duration == float.MaxValue ? DateTime.MaxValue : world.CurrentTime.AddSeconds(duration), player.InstanceID)));
    }

    private void Remove(SID status)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot >= 0)
            world.Execute(new ActorState.OpStatus(player.InstanceID, slot, default));
    }

    // FFXIVClientStructs GunbreakerGauge: 0x08 Ammo, 0x0A MaxTimerDuration (u16 ms), 0x0C AmmoComboStep.
    // ClientState.GetGauge maps struct offset 0x08 onto bit 0 of GaugePayload.Low.
    private void PublishGauge()
        => world.Client.GaugePayload = new((ulong)(byte)_ammo | (ulong)(byte)_gunComboStep << 32, 0);
}
