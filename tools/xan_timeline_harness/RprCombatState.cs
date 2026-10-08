using System;
using System.Collections.Generic;
using BossMod;
using AID = BossMod.RPR.AID;
using SID = BossMod.RPR.SID;

namespace XanTimelineHarness;

// Deterministic action-result simulation, not a replacement rotation. Decisions come from production RPR.cs.
// No synthetic party attacks or network/effect-packet latency: only the modeled player's hits grant sacrifice.
internal sealed class RprCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float> onExecuted, int initialSoul = 0, int initialShroud = 0)
{
    public int SoulOvercap { get; private set; }
    public int ShroudOvercap { get; private set; }
    private readonly HashSet<ulong> _processedDeaths = [];
    private ActionQueue.Entry? _casting;
    private DateTime _castFinish;
    private DateTime _enshroudEnd;
    private int _soul = initialSoul;
    private int _shroud = initialShroud;
    private int _lemure;
    private int _void;
    private uint _sequence;

    public int Shroud => _shroud;
    public int Soul => _soul;
    public int Lemure => _lemure;
    public int Void => _void;

    public void Advance()
    {
        foreach (var actor in world.Actors)
        {
            if (actor.IsDead && _processedDeaths.Add(actor.InstanceID) && Has(SID.DeathsDesign, actor))
                GainSoul(10);
            for (var i = 0; i < actor.Statuses.Length; ++i)
                if (actor.Statuses[i].ID != 0 && actor.Statuses[i].ExpireAt <= world.CurrentTime)
                    world.Execute(new ActorState.OpStatus(actor.InstanceID, i, default));
        }

        if (_lemure > 0 && _enshroudEnd <= world.CurrentTime)
            EndEnshroud();
        if (_casting is { } cast)
        {
            if (cast.Target != null && cast.Target != player && (cast.Target.IsDead || !cast.Target.IsTargetable))
            {
                _casting = null;
                world.Execute(new ActorState.OpCastInfo(player.InstanceID, null));
            }
            else if (Moving && world.CurrentTime < _castFinish.AddSeconds(-SlidecastWindow))
            {
                // moving before the slidecast window interrupts the cast and refunds the recast timer
                _casting = null;
                ++InterruptedCasts;
                world.Execute(new ActorState.OpCastInfo(player.InstanceID, null));
                world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, 0))]));
            }
            else if (world.CurrentTime >= _castFinish)
            {
                _casting = null;
                Complete(cast, ActionDefinitions.Instance[cast.Action]!, casted: true);
                world.Execute(new ActorState.OpCastInfo(player.InstanceID, null));
            }
        }
        PublishGauge();
    }

    public void ExecuteBestAction(AIHints hints)
    {
        if (_casting != null || player.IsDead)
            return;

        // The real client enforces job resources/status requirements after the shared queue chooses a candidate, and a refused
        // choice costs the frame (ActionManagerEx logs "Can't execute" and tries again next frame). By default every infeasible
        // candidate is dropped before the choice instead, which hides pushes the game would refuse; XAN_HARNESS_CLIENT_REJECT=1
        // keeps the job requirements for after the choice. Range and target validity stay before it: the shared queue checks those.
        if (ClientReject.Enabled)
            hints.ActionsToExecute.Entries.RemoveAll(entry => !QueueConsiders(entry));
        else
            hints.ActionsToExecute.Entries.RemoveAll(entry => !CanExecute(entry));
        SearchControl.FilterSuppressed(hints.ActionsToExecute.Entries, world); // oracle-search: actions held by the search stay out of the queue
        var entry = hints.ActionsToExecute.FindBest(world, player, world.Client.Cooldowns, world.Client.AnimationLock, hints, frameStep, true);
        var definition = ActionDefinitions.Instance[entry.Action];
        if (entry.Action.ID == 0 || definition == null
            || MathF.Max(entry.Delay, MathF.Max(world.Client.AnimationLock, definition.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions))) > 0.001f)
            return;
        if (Irregular.Refuses("rpr", entry, definition, world, player)) // --irregular lockout statuses, refused after the pick like the client
            return;
        if (ClientReject.Enabled && !CanExecute(entry))
        {
            ClientReject.Record("rpr", entry.Action, world.CurrentTime, player);
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
        StartCooldown(definition); // Soul Slice/Scythe use both the GCD and a shared charged cooldown.

        var castTime = CastTime(entry.Action, definition);
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

    // Set by the disengage driver. Range and movement are only enforced while it runs, so every other run stays byte-identical.
    public bool EnforceRangeAndMovement { get; set; }
    public bool Moving { get; set; }
    public int InterruptedCasts { get; private set; }
    private const float SlidecastWindow = 0.5f;

    // --irregular: a lockout status lands mid-cast; the cast is interrupted and the recast refunded, as with movement above
    public void InterruptCast()
    {
        if (_casting == null)
            return;
        _casting = null;
        ++InterruptedCasts;
        world.Execute(new ActorState.OpCastInfo(player.InstanceID, null));
        world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, 0))]));
    }

    // the client rejects out-of-range targets and cast starts while moving (casts made instant by Swiftcast and the like are fine)
    private bool InRangeAndAbleToStart(ActionQueue.Entry entry)
    {
        var definition = ActionDefinitions.Instance[entry.Action];
        if (definition == null)
            return true;
        if (entry.Target != null && entry.Target != player && definition.Range > 0 && player.DistanceToHitbox(entry.Target) > definition.Range)
            return false;
        return !Moving || CastTime(entry.Action, definition) <= 0;
    }

    private bool QueueConsiders(ActionQueue.Entry entry)
        => !(entry.Target != null && entry.Target != player && (entry.Target.IsDead || !entry.Target.IsTargetable))
        && (!EnforceRangeAndMovement || InRangeAndAbleToStart(entry));

    // oracle-search: emulator state shown next to each decision of the deviation log
    private string DescribeForSearch() => FormattableString.Invariant($"soul={_soul} shroud={_shroud} lemure={_lemure} void={_void}");

    // burst study (BurstControl.cs): the state shown next to a cycle and the emulator's own readiness rules for a starter
    public string BurstState() => DescribeForSearch();
    public bool BurstFeasible(ActionQueue.Entry entry) => CanExecute(entry);

    private bool CanExecute(ActionQueue.Entry entry)
    {
        if (!QueueConsiders(entry))
            return false;
        if (entry.Action.Type != ActionType.Spell)
            return true;

        var enshrouded = _lemure > 0;
        return (AID)entry.Action.ID switch
        {
            AID.Enshroud => !enshrouded && (_shroud >= 50 || Has(SID.IdealHost)),
            AID.BloodStalk or AID.GrimSwathe or AID.Gluttony => !enshrouded && _soul >= 50,
            AID.UnveiledGibbet => !enshrouded && _soul >= 50 && Has(SID.EnhancedGibbet),
            AID.UnveiledGallows => !enshrouded && _soul >= 50 && Has(SID.EnhancedGallows),
            AID.Gibbet or AID.Gallows or AID.Guillotine => !enshrouded && Has(SID.SoulReaver),
            AID.ExecutionersGibbet or AID.ExecutionersGallows or AID.ExecutionersGuillotine => !enshrouded && Has(SID.Executioner),
            AID.VoidReaping or AID.CrossReaping or AID.GrimReaping or AID.Communio => enshrouded,
            AID.LemuresSlice or AID.LemuresScythe => enshrouded && _void >= 2,
            AID.Sacrificium => enshrouded && Has(SID.Oblatio),
            AID.HarvestMoon => Has(SID.Soulsow),
            AID.PlentifulHarvest => Has(SID.ImmortalSacrifice) && !Has(SID.BloodsownCircle),
            AID.Perfectio => Has(SID.PerfectioParata),
            AID.Slice or AID.WaxingSlice or AID.InfernalSlice or AID.SpinningScythe or AID.NightmareScythe or AID.SoulSlice or AID.SoulScythe => !enshrouded,
            _ => true
        };
    }

    private float GCDLength(ActionID action, ActionDefinition definition)
    {
        if ((AID)action.ID is AID.VoidReaping or AID.CrossReaping or AID.GrimReaping)
            return 1.5f;
        var stats = world.Client.PlayerStats;
        var speed = definition.Category == ActionCategory.Spell ? stats.SpellSpeed : stats.SkillSpeed;
        return ActionSpeed.GCDRounded(speed, stats.Haste, player.Level);
    }

    private float CastTime(ActionID action, ActionDefinition definition)
    {
        if ((AID)action.ID == AID.Harpe && Has(SID.EnhancedHarpe) || (AID)action.ID == AID.Soulsow && !player.InCombat)
            return 0;
        // Casts do not have the 1.5 second recast floor used by ActionSpeed.GCDRounded.
        var stats = world.Client.PlayerStats;
        return MathF.Floor(definition.CastTime * 1000 * ActionSpeed.SpeedStatToModifier(stats.SpellSpeed, player.Level) / 1000 * stats.Haste / 100) / 1000;
    }

    private void StartCooldown(ActionDefinition definition)
    {
        var group = definition.ActualMainCooldownGroup(world.Client.DutyActions);
        if (group < 0 || group == ActionDefinitions.GCDGroup || definition.Cooldown <= 0)
            return;
        var cooldown = definition.ID == ActionID.MakeSpell(AID.Enshroud) ? 5 : definition.Cooldown; // 5 s at every level since patch 7.3
        var capCharges = Math.Max(1, definition.MaxChargesAtCap());
        var levelCharges = Math.Clamp(definition.MaxChargesAtLevel(player.Level), 1, capCharges);
        var current = world.Client.Cooldowns[group];
        var elapsed = current.Total > 0 ? MathF.Max(0, current.Elapsed - cooldown) : cooldown * (levelCharges - 1);
        world.Execute(new ClientState.OpCooldown(false, [(group, new(elapsed, cooldown * capCharges))]));
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
        var potency = entry.Action.Type == ActionType.Spell ? RprPotencyScorer.Estimate(world, player, (AID)entry.Action.ID, entry.Target) : 0f;
        if (entry.Action.Type == ActionType.Spell)
            ApplySpell((AID)entry.Action.ID, entry.Target);
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

    private void ApplySpell(AID action, Actor? target)
    {
        var combo = (AID)world.Client.ComboState.Action;
        var hitsEnemy = target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true }
            || action is AID.SpinningScythe or AID.NightmareScythe or AID.WhorlofDeath or AID.SoulScythe && HasNearbyEnemy();
        switch (action)
        {
            case AID.Slice:
            case AID.SpinningScythe:
                SetCombo(hitsEnemy ? action : AID.None);
                if (hitsEnemy)
                    GainSoul(10);
                break;
            case AID.WaxingSlice:
                SetCombo(combo == AID.Slice ? action : AID.None);
                if (hitsEnemy && combo == AID.Slice)
                    GainSoul(10);
                break;
            case AID.InfernalSlice:
            case AID.NightmareScythe:
                SetCombo(AID.None);
                if (hitsEnemy && combo == (action == AID.InfernalSlice ? AID.WaxingSlice : AID.SpinningScythe))
                    GainSoul(10);
                break;
            case AID.ShadowofDeath:
                if (target != null)
                    Set(SID.DeathsDesign, MathF.Min(60, Left(SID.DeathsDesign, target) + 30), target: target);
                break;
            case AID.WhorlofDeath:
                foreach (var enemy in world.Actors)
                    if (enemy.Type == ActorType.Enemy && !enemy.IsDead && enemy.IsTargetable && player.DistanceToHitbox(enemy) <= 5)
                        Set(SID.DeathsDesign, MathF.Min(60, Left(SID.DeathsDesign, enemy) + 30), target: enemy);
                break;
            case AID.SoulSlice:
            case AID.SoulScythe:
                if (hitsEnemy)
                    GainSoul(50);
                break;
            case AID.Harpe:
                Remove(SID.EnhancedHarpe);
                if (hitsEnemy)
                    GainSoul(10);
                break;
            case AID.HarvestMoon:
                Remove(SID.Soulsow);
                if (hitsEnemy)
                    GainSoul(10);
                break;
            case AID.Soulsow:
                Set(SID.Soulsow, float.MaxValue);
                break;
            case AID.Gluttony:
            case AID.BloodStalk:
            case AID.GrimSwathe:
            case AID.UnveiledGibbet:
            case AID.UnveiledGallows:
                _soul -= 50;
                Remove(SID.SoulReaver);
                Remove(SID.Executioner);
                if (player.Level >= 70)
                    Set(action == AID.Gluttony && player.Level >= 96 ? SID.Executioner : SID.SoulReaver, 30, action == AID.Gluttony ? 2 : 1);
                break;
            case AID.Gibbet:
            case AID.Gallows:
            case AID.Guillotine:
            case AID.ExecutionersGibbet:
            case AID.ExecutionersGallows:
            case AID.ExecutionersGuillotine:
                ConsumeStack(Has(SID.Executioner) ? SID.Executioner : SID.SoulReaver);
                if (player.Level >= 80)
                {
                    ShroudOvercap += Math.Max(0, _shroud + 10 - 100);
                    _shroud = Math.Min(100, _shroud + 10);
                }
                if (action is AID.Gibbet or AID.ExecutionersGibbet)
                    Enhance(SID.EnhancedGibbet, SID.EnhancedGallows, 60);
                else if (action is AID.Gallows or AID.ExecutionersGallows)
                    Enhance(SID.EnhancedGallows, SID.EnhancedGibbet, 60);
                break;
            case AID.ArcaneCircle:
                Set(SID.ArcaneCircle, 20);
                if (player.Level >= 88)
                {
                    Set(SID.CircleofSacrifice, 5);
                    Set(SID.BloodsownCircle, 6);
                }
                break;
            case AID.PlentifulHarvest:
                Remove(SID.ImmortalSacrifice);
                Set(SID.IdealHost, 30);
                if (player.Level >= 100)
                    Set(SID.PerfectioOcculta, 30);
                break;
            case AID.Enshroud:
                if (Has(SID.IdealHost))
                    Remove(SID.IdealHost);
                else
                    _shroud -= 50;
                _lemure = 5;
                _void = 0;
                _enshroudEnd = world.CurrentTime.AddSeconds(30);
                Set(SID.Enshrouded, 30);
                if (player.Level >= 92)
                    Set(SID.Oblatio, 30);
                break;
            case AID.VoidReaping:
            case AID.CrossReaping:
            case AID.GrimReaping:
                --_lemure;
                if (player.Level >= 86)
                    _void = Math.Min(5, _void + 1);
                if (action == AID.VoidReaping)
                    Enhance(SID.EnhancedVoidReaping, SID.EnhancedCrossReaping, 30);
                else if (action == AID.CrossReaping)
                    Enhance(SID.EnhancedCrossReaping, SID.EnhancedVoidReaping, 30);
                if (_lemure == 0)
                    EndEnshroud();
                break;
            case AID.LemuresSlice:
            case AID.LemuresScythe:
                _void -= 2;
                break;
            case AID.Sacrificium:
                Remove(SID.Oblatio);
                break;
            case AID.Communio:
                EndEnshroud();
                if (Has(SID.PerfectioOcculta))
                {
                    Remove(SID.PerfectioOcculta);
                    Set(SID.PerfectioParata, 30);
                }
                break;
            case AID.Perfectio:
                Remove(SID.PerfectioParata);
                break;
            case AID.TrueNorth:
                Set(SID.TrueNorth, 10);
                break;
            case AID.HellsIngress:
            case AID.HellsEgress:
                Set(SID.EnhancedHarpe, 20);
                if (player.Level >= 74)
                    Set(SID.Threshold, 10);
                break;
        }

        var definition = ActionDefinitions.Instance.Spell(action)!;
        if (definition.IsGCD && action is not (AID.Gibbet or AID.Gallows or AID.Guillotine or AID.ExecutionersGibbet or AID.ExecutionersGallows or AID.ExecutionersGuillotine))
        {
            Remove(SID.SoulReaver);
            Remove(SID.Executioner);
        }
        if (hitsEnemy && Has(SID.CircleofSacrifice) && Has(SID.BloodsownCircle) && action is not (AID.Feint or AID.LegSweep))
        {
            Remove(SID.CircleofSacrifice);
            Set(SID.ImmortalSacrifice, 30, Math.Min(8, Stacks(SID.ImmortalSacrifice) + 1));
        }
    }

    private bool HasNearbyEnemy()
    {
        foreach (var actor in world.Actors)
            if (actor.Type == ActorType.Enemy && !actor.IsDead && actor.IsTargetable && player.DistanceToHitbox(actor) <= 5)
                return true;
        return false;
    }

    private void SetCombo(AID action) => world.Execute(new ClientState.OpComboChange(action == AID.None ? default : new((uint)action, 30)));
    private void GainSoul(int amount)
    {
        if (player.Level < 50)
        {
            _soul = 0;
            return;
        }
        SoulOvercap += Math.Max(0, _soul + amount - 100);
        _soul = Math.Min(100, _soul + amount);
    }
    private bool Has(SID status, Actor? target = null) => Left(status, target) > 0;
    private float Left(SID status, Actor? target = null) => MathF.Max(0, (float)(((target ?? player).FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);
    private int Stacks(SID status) => player.FindStatus((uint)status, player.InstanceID)?.Extra & 0xFF ?? 0;

    private void Set(SID status, float duration, int stacks = 0, Actor? target = null)
    {
        var actor = target ?? player;
        var slot = Array.FindIndex(actor.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot < 0)
            slot = Array.FindIndex(actor.Statuses, s => s.ID == 0);
        if (slot < 0)
            throw new InvalidOperationException("RPR simulation ran out of status slots.");
        world.Execute(new ActorState.OpStatus(actor.InstanceID, slot, new((uint)status, (ushort)stacks, duration == float.MaxValue ? DateTime.MaxValue : world.CurrentTime.AddSeconds(duration), player.InstanceID)));
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

    private void Enhance(SID consumed, SID gained, float duration)
    {
        Remove(consumed);
        Set(gained, duration);
    }

    private void EndEnshroud()
    {
        _lemure = _void = 0;
        _enshroudEnd = world.CurrentTime;
        Remove(SID.Enshrouded);
        Remove(SID.Oblatio);
        Remove(SID.EnhancedVoidReaping);
        Remove(SID.EnhancedCrossReaping);
    }

    private void PublishGauge()
    {
        var milliseconds = _lemure > 0 ? (ulong)Math.Clamp((_enshroudEnd - world.CurrentTime).TotalMilliseconds, 0, 30000) : 0;
        world.Client.GaugePayload = new((byte)_soul | (ulong)(byte)_shroud << 8 | milliseconds << 16 | (ulong)(byte)_lemure << 32 | (ulong)(byte)_void << 40, 0);
    }
}
