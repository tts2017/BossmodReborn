using System;
using System.Collections.Generic;
using BossMod;
using AID = BossMod.BLM.AID;
using SID = BossMod.BLM.SID;

namespace XanTimelineHarness;

// Deterministic action-result simulation for BLM, not a replacement rotation. Decisions come from production BLM.cs.
// Rules follow the 7.x tooltips (Aspect Mastery I-V, Enhanced Astral Fire): ice spells cost 0 MP and restore
// 2500/5000/10000 MP on landing under UI1/2/3, fire spells cost double under AF without Umbral Hearts, MP recovery
// is 0 under AF (Lucid Dreaming included), AF3/UI3 halve the cast time of the opposite element.
internal sealed class BlmCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float> onExecuted)
{
    public const int MaxMP = 10000;
    private const float ServerTick = 3f;
    private const float PolyglotInterval = 30f;
    private const float ThunderheadDuration = 30f;
    private const float FirestarterDuration = 30f;
    private const float LeyLinesDuration = 20f;
    private const float TriplecastDuration = 15f;
    private const float SwiftcastDuration = 10f;
    private const float LucidDuration = 21f;
    private const uint MedicatedStatus = 49;
    private const float MedicatedMultiplier = 1.06f;
    private const float SplashRadius = 5f;

    private readonly record struct DotSnapshot(SID Status, float TickPotency);

    private ActionQueue.Entry? _casting;
    private DateTime _castFinish;
    private DateTime _nextTick = world.CurrentTime.AddSeconds(ServerTick);
    private uint _sequence;
    private int _element;
    private int _hearts;
    private int _polyglot;
    private int _astralSoul;
    private bool _paradox;
    private float _polyglotTimer;
    private int _mp = MaxMP;
    private bool _hasteApplied;
    private readonly Dictionary<ulong, DotSnapshot> _dots = [];

    public int Element => _element;
    public int Hearts => _hearts;
    public int Polyglot => _polyglot;

    // --start-gauge: Polyglot at a fraction of 3 (MP stays full)
    public void SetGaugeFraction(float fraction)
    {
        _polyglot = (int)MathF.Round(Math.Clamp(fraction, 0, 1) * 3);
        Publish();
    }
    public int AstralSoul => _astralSoul;
    public bool Paradox => _paradox;
    public int MP => _mp;
    public int PolyglotOvercap { get; private set; }
    public float DotPotency { get; private set; }
    public bool Firestarter => Has(SID.Firestarter);
    public bool Thunderhead => Has(SID.Thunderhead);
    public bool InstantCast => Has(SID.Triplecast) || Has(SID.Swiftcast);
    public bool LeyLinesActive => Has(SID.LeyLines);

    // Fork scan (blm-fork-scan). A decision point is a frame on which a GCD is about to start: the baseline run records what
    // it chose and which other GCDs were legal right then, and a fork run replaces the GCD started at that instant with one
    // of those alternatives and lets production BLM.cs carry on from the new state. Both stay null in every other command.
    public readonly record struct DecisionPoint(DateTime At, ActionID Chosen, ActionID[] Alternatives, int Element, int MP, int Hearts, int Polyglot, int AstralSoul, bool Paradox, bool Firestarter, bool Thunderhead, bool InstantCast, bool LeyLines);
    public List<DecisionPoint>? DecisionLog { get; set; }
    public (DateTime At, ActionID Action)? Force { get; set; }
    public string ForceOutcome { get; private set; } = "unreached";
    // Depth search: once Force has been applied, force these at the following GCD decisions, in order. An entry equal to what
    // the baseline would choose there is allowed (it forces nothing). FollowupIllegal is set when one cannot be used on its turn.
    public ActionID[]? ForceFollowups { get; set; }
    public int FollowupsApplied { get; private set; }
    public bool FollowupIllegal { get; private set; }

    // Single-target GCDs worth offering as a deviation. AoE spells are left out because the scan runs single-target scenarios.
    private static readonly AID[] ForkCandidates = [AID.Fire3, AID.Fire4, AID.Despair, AID.FlareStar, AID.Paradox, AID.Xenoglossy, AID.HighThunder, AID.Blizzard3, AID.Blizzard4, AID.UmbralSoul];

    private ActionQueue.Entry RetargetFor(ActionID action, ActionQueue.Entry chosen)
    {
        var selfTargeted = action == ActionID.MakeSpell(AID.UmbralSoul);
        var target = selfTargeted ? player : chosen.Target != null && chosen.Target != player ? chosen.Target : world.Actors.Find(player.TargetID);
        return new(action, target, chosen.Priority, chosen.Expire, 0, 0, target?.PosRot.XYZ() ?? chosen.TargetPos, null, false, false);
    }

    private bool LegalNow(ActionQueue.Entry entry)
    {
        var definition = ActionDefinitions.Instance[entry.Action];
        return definition != null && definition.IsGCD && (entry.Target != null || entry.Action == ActionID.MakeSpell(AID.UmbralSoul)) && CanExecute(entry)
            && definition.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions) <= 0.001f;
    }

    private ActionID[] LegalAlternatives(ActionQueue.Entry chosen)
    {
        var result = new List<ActionID>();
        foreach (var aid in ForkCandidates)
        {
            var action = ActionID.MakeSpell(aid);
            if (action != chosen.Action && LegalNow(RetargetFor(action, chosen)))
                result.Add(action);
        }
        return [.. result];
    }

    private int Level => player.Level;
    private int MaxHearts => Level >= 58 ? 3 : 0;
    private int MaxPolyglot => Level >= 98 ? 3 : Level >= 80 ? 2 : Level >= 70 ? 1 : 0;
    private int MaxElementStacks => Level >= 35 ? 3 : Level >= 20 ? 2 : 1;

    public void Advance()
    {
        foreach (var actor in world.Actors)
            for (var i = 0; i < actor.Statuses.Length; ++i)
                if (actor.Statuses[i].ID != 0 && actor.Statuses[i].ExpireAt <= world.CurrentTime)
                    world.Execute(new ActorState.OpStatus(actor.InstanceID, i, default));

        while (world.CurrentTime >= _nextTick)
        {
            Tick();
            _nextTick = _nextTick.AddSeconds(ServerTick);
        }

        if (_element != 0 && MaxPolyglot > 0)
        {
            _polyglotTimer -= frameStep;
            while (_polyglotTimer <= 0)
            {
                if (_polyglot < MaxPolyglot)
                    ++_polyglot;
                else
                    ++PolyglotOvercap;
                _polyglotTimer += PolyglotInterval;
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

        SyncLeyLinesHaste();
        Publish();
    }

    private void Tick()
    {
        // Natural MP recovery is 2% per tick; Astral Fire reduces all MP recovery (Lucid Dreaming included) to 0.
        if (_element <= 0)
        {
            var regen = MaxMP / 50;
            if (Has(SID.LucidDreaming))
                regen += 550;
            _mp = Math.Min(MaxMP, _mp + regen);
        }

        foreach (var (targetID, dot) in _dots)
        {
            var target = world.Actors.Find(targetID);
            if (target is not { IsDead: false, IsTargetable: true } || target.FindStatus((uint)dot.Status, player.InstanceID) == null)
                continue;
            DotPotency += dot.TickPotency;
        }
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
        if (Irregular.Refuses("blm", entry, definition, world, player)) // --irregular lockout statuses, refused after the pick like the client
            return;
        if (ClientReject.Enabled && !CanExecute(entry))
        {
            ClientReject.Record("blm", entry.Action, world.CurrentTime, player);
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
        {
            DecisionLog?.Add(new(world.CurrentTime, entry.Action, LegalAlternatives(entry), _element, _mp, _hearts, _polyglot, _astralSoul, _paradox, Firestarter, Has(SID.Thunderhead),
                Has(SID.Triplecast) || Has(SID.Swiftcast), Has(SID.LeyLines)));
            if (Force is { } force && ForceOutcome == "unreached" && world.CurrentTime >= force.At)
            {
                var forced = RetargetFor(force.Action, entry);
                if (LegalNow(forced))
                {
                    entry = forced;
                    definition = ActionDefinitions.Instance[forced.Action]!;
                    ForceOutcome = "applied";
                }
                else
                {
                    ForceOutcome = "illegal";
                }
            }
            else if (ForceOutcome == "applied" && ForceFollowups is { } followups && FollowupsApplied < followups.Length && !FollowupIllegal)
            {
                var forced = RetargetFor(followups[FollowupsApplied], entry);
                if (LegalNow(forced))
                {
                    entry = forced;
                    definition = ActionDefinitions.Instance[forced.Action]!;
                    ++FollowupsApplied;
                }
                else
                {
                    FollowupIllegal = true;
                }
            }
            world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, GCDLength()))]));
        }
        StartCooldown(definition);

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

    // oracle-search: emulator state shown next to each decision of the deviation log
    private string DescribeForSearch() => FormattableString.Invariant($"el={_element} mp={_mp} hearts={_hearts} poly={_polyglot} soul={_astralSoul} paradox={(_paradox ? 1 : 0)}");

    // burst study (BurstControl.cs): the state shown next to a cycle and the emulator's own readiness rules for a starter
    public string BurstState() => DescribeForSearch();
    public bool BurstFeasible(ActionQueue.Entry entry) => CanExecute(entry);

    private bool CanExecute(ActionQueue.Entry entry, bool jobRules = true)
    {
        if (entry.Target != null && entry.Target != player && (entry.Target.IsDead || !entry.Target.IsTargetable))
            return false;
        if (EnforceRangeAndMovement && !InRangeAndAbleToStart(entry))
            return false;
        if (!jobRules)
            return true;
        if (entry.Action.Type != ActionType.Spell)
            return true;

        var aid = (AID)entry.Action.ID;
        return aid switch
        {
            AID.Fire1 or AID.Fire2 or AID.HighFire2 or AID.Fire3 or AID.Scathe or AID.Blizzard1 or AID.Blizzard2 or AID.HighBlizzard2 or AID.Blizzard3 => _mp >= ManaCost(aid),
            AID.Fire4 or AID.Despair or AID.Flare or AID.Manafont => _element > 0 && _mp >= ManaCost(aid),
            AID.FlareStar => _element > 0 && _astralSoul >= 6,
            AID.Blizzard4 or AID.Freeze or AID.UmbralSoul => _element < 0,
            AID.Paradox => _paradox && _mp >= ManaCost(aid),
            AID.Xenoglossy or AID.Foul => _polyglot > 0,
            AID.Thunder1 or AID.Thunder2 or AID.Thunder3 or AID.Thunder4 or AID.HighThunder or AID.HighThunder2 => Has(SID.Thunderhead),
            AID.Amplifier or AID.Transpose => _element != 0,
            AID.LeyLines => !Has(SID.LeyLines),
            AID.Retrace or AID.BetweenTheLines => Has(SID.LeyLines),
            _ => true
        };
    }

    private int ManaCost(AID aid)
    {
        // The opposite element is free: ice spells cost nothing under Astral Fire (Despair -> Blizzard III at 0 MP) and fire spells
        // cost nothing under Umbral Ice (the standard Fire III -> 6x Fire IV + Paradox + Despair line only fits 10000 MP that way).
        int fire(int cost) => _element < 0 ? 0 : _element > 0 && _hearts == 0 ? cost * 2 : cost;
        int ice(int cost) => _element != 0 ? 0 : cost;
        return aid switch
        {
            AID.Fire1 or AID.Fire4 => fire(800),
            AID.Fire2 or AID.HighFire2 => fire(1500),
            AID.Fire3 => Firestarter ? 0 : fire(2000),
            AID.Despair or AID.Flare => 800,
            AID.Paradox => _element > 0 ? 1600 : 0,
            AID.Scathe => 800,
            AID.Blizzard1 => ice(400),
            AID.Blizzard2 or AID.HighBlizzard2 or AID.Blizzard3 or AID.Blizzard4 => ice(800),
            AID.Freeze => ice(1000),
            _ => 0
        };
    }

    private float GCDLength()
    {
        var stats = world.Client.PlayerStats;
        return ActionSpeed.GCDRounded(stats.SpellSpeed, stats.Haste, Level);
    }

    private bool NaturallyInstant(AID aid, ActionDefinition definition)
        => definition.CastTime <= 0
        || aid == AID.Fire3 && Firestarter
        || aid == AID.Foul && Level >= 80
        || aid == AID.Despair && Level >= 100;

    private float CastTime(ActionID action, ActionDefinition definition)
    {
        var aid = (AID)action.ID;
        if (NaturallyInstant(aid, definition) || Has(SID.Swiftcast) || Has(SID.Triplecast))
            return 0;

        var castTime = definition.CastTime;
        if (_element == -3 && definition.Aspect == ActionAspect.Fire || _element == 3 && definition.Aspect == ActionAspect.Ice)
            castTime *= 0.5f;
        var stats = world.Client.PlayerStats;
        return MathF.Floor(castTime * 1000 * ActionSpeed.SpeedStatToModifier(stats.SpellSpeed, Level) / 1000 * stats.Haste / 100) / 1000;
    }

    private void StartCooldown(ActionDefinition definition)
    {
        var group = definition.ActualMainCooldownGroup(world.Client.DutyActions);
        if (group < 0 || group == ActionDefinitions.GCDGroup || definition.Cooldown <= 0)
            return;
        // ActionDefinition.Cooldown is the untraited recast from the action sheet, so the two traits that shorten a
        // recast BLM uses have to be applied here: Enhanced Manafont (trait 463, level 84, 120s -> 100s) and
        // Enhanced Swiftcast (trait 644, level 94, 60s -> 40s).
        var cooldown = definition.ID == ActionID.MakeSpell(AID.Manafont) && Level >= 84 ? 100
            : definition.ID == ActionID.MakeSpell(AID.Swiftcast) && Level >= 94 ? 40
            : definition.Cooldown;
        var capCharges = Math.Max(1, definition.MaxChargesAtCap());
        var levelCharges = Math.Clamp(definition.MaxChargesAtLevel(Level), 1, capCharges);
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
        var potency = 0f;
        if (entry.Action.Type == ActionType.Spell)
        {
            var aid = (AID)entry.Action.ID;
            if (!NaturallyInstant(aid, definition))
                SpendInstantCast();
            potency = ApplySpell(aid, entry.Target);
        }
        else if (entry.Action.Type == ActionType.Item)
        {
            var quantity = world.Client.GetInventoryItemQuantity(entry.Action.ID);
            world.Execute(new ClientState.OpInventoryChange(entry.Action.ID, quantity > 0 ? quantity - 1 : 0));
            if (entry.Action == ActionDefinitions.IDPotionInt)
            {
                var item = Service.LuminaRow<Lumina.Excel.Sheets.Item>(entry.Action.ID % 500000)!.Value;
                var data = item.ItemAction.Value.DataHQ;
                SetStatus(data[0], data[2], data[1] + 10000);
            }
        }
        SyncLeyLinesHaste();
        Publish();
        onExecuted(entry.Action, targetID, definition.IsGCD, potency);
    }

    // Swiftcast is consumed before Triplecast stacks when both are active.
    private void SpendInstantCast()
    {
        if (Has(SID.Swiftcast))
        {
            Remove(SID.Swiftcast);
            return;
        }
        if (Has(SID.Triplecast))
            ConsumeStack(SID.Triplecast);
    }

    // Damage is scored with the element the spell was cast UNDER: a spell that grants Astral Fire or Umbral Ice applies
    // that change after its own damage (and after Enochian is decided), so Fire III from Umbral Ice III is the weak 0.7x
    // cast the rotation guides call "weakened", Fire III straight after a Transpose is a 1.4x AF1 cast, and Blizzard III
    // from Astral Fire III eats the 0.7x ice penalty. Measured 2026-09-23 on the player own 7.5 replays; see the note in
    // BLM.EffectivePotency. Umbral Heart / MP bookkeeping keeps its old order (PayFire before, RestoreIceMP after), which
    // is what makes Blizzard III from Astral Fire refill the whole bar.
    private float ApplySpell(AID aid, Actor? target)
    {
        switch (aid)
        {
            case AID.Fire1:
                PayFire(800);
                var dmgFire1 = Damage(180, ActionAspect.Fire, target);
                EnterBasicElement(fire: true);
                return dmgFire1;
            case AID.Fire2:
            case AID.HighFire2:
                PayFire(1500);
                var dmgFire2 = Damage(aid == AID.HighFire2 ? 100 : 80, ActionAspect.Fire, target, falloff: 1f);
                EnterElement(Level >= 35 ? 3 : Math.Max(1, _element + 1));
                return dmgFire2;
            case AID.Fire3:
                if (Firestarter)
                    Remove(SID.Firestarter);
                else
                    PayFire(2000);
                var dmgFire3 = Damage(290, ActionAspect.Fire, target);
                EnterElement(3);
                return dmgFire3;
            case AID.Fire4:
                PayFire(800);
                if (Level >= 100)
                    _astralSoul = Math.Min(6, _astralSoul + 1);
                return Damage(300, ActionAspect.Fire, target);
            case AID.Despair:
                _mp = 0;
                var dmgDespair = Damage(350, ActionAspect.Fire, target);
                EnterElement(3);
                return dmgDespair;
            case AID.Flare:
                _mp = _hearts > 0 ? _mp / 3 : 0;
                _hearts = 0;
                var dmgFlare = Damage(240, ActionAspect.Fire, target, falloff: 0.7f);
                EnterElement(3);
                if (Level >= 100)
                    _astralSoul = Math.Min(6, _astralSoul + 3);
                return dmgFlare;
            case AID.FlareStar:
                _astralSoul = 0;
                return Damage(500, ActionAspect.Fire, target, falloff: 0.35f);
            case AID.Blizzard1:
                _mp -= ManaCost(aid);
                var dmgBlizzard1 = Damage(180, ActionAspect.Ice, target);
                EnterBasicElement(fire: false);
                RestoreIceMP();
                return dmgBlizzard1;
            case AID.Blizzard2:
            case AID.HighBlizzard2:
                _mp -= ManaCost(aid);
                var dmgBlizzard2 = Damage(aid == AID.HighBlizzard2 ? 100 : 80, ActionAspect.Ice, target, falloff: 1f);
                EnterElement(Level >= 35 ? -3 : Math.Min(-1, _element - 1));
                RestoreIceMP();
                return dmgBlizzard2;
            case AID.Blizzard3:
                _mp -= ManaCost(aid);
                var dmgBlizzard3 = Damage(290, ActionAspect.Ice, target);
                EnterElement(-3);
                RestoreIceMP();
                return dmgBlizzard3;
            case AID.Blizzard4:
                _hearts = MaxHearts;
                RestoreIceMP();
                return Damage(300, ActionAspect.Ice, target);
            case AID.Freeze:
                _hearts = MaxHearts;
                RestoreIceMP();
                return Damage(120, ActionAspect.Ice, target, falloff: 1f);
            case AID.UmbralSoul:
                EnterBasicElement(fire: false);
                _hearts = Math.Min(MaxHearts, _hearts + 1);
                RestoreIceMP();
                return 0;
            case AID.Paradox:
                if (_element > 0)
                {
                    _mp -= 1600;
                    Set(SID.Firestarter, FirestarterDuration);
                }
                _paradox = false;
                return Damage(540, ActionAspect.None, target);
            case AID.Xenoglossy:
                _polyglot = Math.Max(0, _polyglot - 1);
                return Damage(890, ActionAspect.None, target);
            case AID.Foul:
                _polyglot = Math.Max(0, _polyglot - 1);
                return Damage(600, ActionAspect.None, target, falloff: 0.75f);
            case AID.Scathe:
                _mp -= 800;
                return Damage(100, ActionAspect.None, target);
            case AID.Thunder1:
                return ApplyThunder(target, SID.Thunder, 100, 45, 24, aoe: false);
            case AID.Thunder3:
                return ApplyThunder(target, SID.ThunderIII, 120, 50, 27, aoe: false);
            case AID.HighThunder:
                return ApplyThunder(target, SID.HighThunder, 150, 60, 30, aoe: false);
            case AID.Thunder2:
                return ApplyThunder(target, SID.ThunderII, 60, 30, 18, aoe: true);
            case AID.Thunder4:
                return ApplyThunder(target, SID.ThunderIV, 80, 35, 21, aoe: true);
            case AID.HighThunder2:
                return ApplyThunder(target, SID.HighThunderII, 100, 40, 24, aoe: true);
            case AID.Transpose:
                if (_element > 0)
                    EnterElement(-1);
                else if (_element < 0)
                    EnterElement(1);
                return 0;
            case AID.Manafont:
                _mp = MaxMP;
                EnterElement(3);
                _hearts = MaxHearts;
                if (Level >= 90)
                    _paradox = true;
                Set(SID.Thunderhead, ThunderheadDuration);
                return 0;
            case AID.Amplifier:
                _polyglot = Math.Min(MaxPolyglot, _polyglot + 1);
                return 0;
            case AID.LeyLines:
                Set(SID.LeyLines, LeyLinesDuration);
                Set(SID.CircleOfPower, LeyLinesDuration);
                return 0;
            case AID.Triplecast:
                Set(SID.Triplecast, TriplecastDuration, 3);
                return 0;
            case AID.Swiftcast:
                Set(SID.Swiftcast, SwiftcastDuration);
                return 0;
            case AID.LucidDreaming:
                Set(SID.LucidDreaming, LucidDuration);
                return 0;
            default:
                return 0;
        }
    }

    private void PayFire(int baseCost)
    {
        // Fire spells cost no MP under Umbral Ice (see ManaCost above); only the Astral Fire surcharge and Umbral Heart consumption apply in fire.
        if (_element < 0)
            return;
        var inFire = _element > 0;
        var cost = inFire && _hearts == 0 ? baseCost * 2 : baseCost;
        _mp = Math.Max(0, _mp - cost);
        if (inFire && _hearts > 0)
            --_hearts;
    }

    private void EnterBasicElement(bool fire)
    {
        var sign = fire ? 1 : -1;
        var next = Math.Sign(_element) == sign
            ? sign * Math.Min(MaxElementStacks, Math.Abs(_element) + 1)
            : _element == 0 ? sign : 0;
        EnterElement(next);
    }

    private void EnterElement(int next)
    {
        var previous = _element;
        _element = Math.Clamp(next, -3, 3);
        if (previous == 0 && _element != 0)
            _polyglotTimer = PolyglotInterval;
        if (_element <= 0)
            _astralSoul = 0;
        if (_element == 0)
        {
            _paradox = false;
            _hearts = 0;
            return;
        }

        var swapped = previous != 0 && Math.Sign(previous) != Math.Sign(_element);
        if (previous == 0 || swapped)
            Set(SID.Thunderhead, ThunderheadDuration);
        if (swapped && Level >= 90)
        {
            if (_element < 0 && Math.Abs(previous) == 3)
                _paradox = true;
            else if (_element > 0 && previous == -3 && _hearts == MaxHearts)
                _paradox = true;
        }
    }

    private void RestoreIceMP()
    {
        if (_element >= 0)
            return;
        var restored = -_element switch { 1 => 2500, 2 => 5000, _ => MaxMP };
        _mp = Math.Min(MaxMP, _mp + restored);
    }

    private float ApplyThunder(Actor? target, SID status, int initial, int dot, float duration, bool aoe)
    {
        Remove(SID.Thunderhead);
        var multiplier = GlobalMultiplier();
        var total = 0f;
        var hit = 0;
        foreach (var enemy in HitEnemies(target, aoe))
        {
            foreach (var existing in new[] { SID.Thunder, SID.ThunderII, SID.ThunderIII, SID.ThunderIV, SID.HighThunder, SID.HighThunderII })
                Remove(existing, enemy);
            Set(status, duration, target: enemy);
            _dots[enemy.InstanceID] = new(status, dot * multiplier);
            total += initial * multiplier;
            ++hit;
        }
        return total;
    }

    private float Damage(float potency, ActionAspect aspect, Actor? target, float falloff = 0f)
    {
        var multiplier = GlobalMultiplier() * ElementMultiplier(aspect);
        if (falloff <= 0)
            return target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true } ? potency * multiplier : 0;

        var total = 0f;
        var hit = 0;
        foreach (var _ in HitEnemies(target, aoe: true))
        {
            total += potency * multiplier * (hit == 0 ? 1f : falloff);
            ++hit;
        }
        return total;
    }

    private IEnumerable<Actor> HitEnemies(Actor? target, bool aoe)
    {
        var primaryHit = target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true };
        if (primaryHit)
            yield return target!;
        if (!aoe || target == null)
            yield break;
        foreach (var enemy in world.Actors)
        {
            if (enemy == target || enemy.Type != ActorType.Enemy || enemy.IsDead || !enemy.IsTargetable)
                continue;
            if (target.DistanceToHitbox(enemy) > SplashRadius)
                continue;
            yield return enemy;
        }
    }

    private float ElementMultiplier(ActionAspect aspect)
    {
        var stacks = Math.Abs(_element);
        if (aspect == ActionAspect.Fire)
            return _element > 0 ? 1.2f + 0.2f * stacks : _element < 0 ? 1f - 0.1f * stacks : 1f;
        if (aspect == ActionAspect.Ice)
            return _element > 0 ? 1f - 0.1f * stacks : 1f;
        return 1f;
    }

    private float GlobalMultiplier()
    {
        var multiplier = 1f;
        if (_element != 0)
            multiplier *= Level >= 96 ? 1.27f : Level >= 86 ? 1.22f : Level >= 78 ? 1.15f : Level >= 70 ? 1.10f : Level >= 56 ? 1.05f : 1f;
        if (player.FindStatus(MedicatedStatus) != null)
            multiplier *= MedicatedMultiplier;
        return multiplier;
    }

    private void SyncLeyLinesHaste()
    {
        var inLeyLines = Has(SID.CircleOfPower);
        if (inLeyLines == _hasteApplied)
            return;
        _hasteApplied = inLeyLines;
        var stats = world.Client.PlayerStats;
        world.Execute(new ClientState.OpPlayerStatsChange(new(stats.SkillSpeed, stats.SpellSpeed, inLeyLines ? 85 : 100)));
    }

    private bool Has(SID status, Actor? target = null) => Left(status, target) > 0;
    private float Left(SID status, Actor? target = null) => MathF.Max(0, (float)(((target ?? player).FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);
    private int Stacks(SID status) => player.FindStatus((uint)status, player.InstanceID)?.Extra & 0xFF ?? 0;

    private void Set(SID status, float duration, int stacks = 0, Actor? target = null) => SetStatus((uint)status, duration, stacks, target);

    private void SetStatus(uint status, float duration, int stacks, Actor? target = null)
    {
        var actor = target ?? player;
        var slot = Array.FindIndex(actor.Statuses, s => s.ID == status && s.SourceID == player.InstanceID);
        if (slot < 0)
            slot = Array.FindIndex(actor.Statuses, s => s.ID == 0);
        if (slot < 0)
            throw new InvalidOperationException("BLM simulation ran out of status slots.");
        world.Execute(new ActorState.OpStatus(actor.InstanceID, slot, new(status, (ushort)stacks, duration == float.MaxValue ? DateTime.MaxValue : world.CurrentTime.AddSeconds(duration), player.InstanceID)));
    }

    private void Remove(SID status, Actor? target = null)
    {
        var actor = target ?? player;
        var slot = Array.FindIndex(actor.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot >= 0)
            world.Execute(new ActorState.OpStatus(actor.InstanceID, slot, default));
    }

    private void ConsumeStack(SID status)
    {
        var remaining = Stacks(status) - 1;
        if (remaining > 0)
            Set(status, Left(status), remaining);
        else
            Remove(status);
    }

    private void Publish()
    {
        _mp = Math.Clamp(_mp, 0, MaxMP);
        if (player.HPMP.CurMP != (uint)_mp)
            world.Execute(new ActorState.OpHPMP(player.InstanceID, new(player.HPMP.CurHP, player.HPMP.MaxHP, player.HPMP.Shield, (uint)_mp, MaxMP)));

        // FFXIVClientStructs BlackMageGauge: 0x08 EnochianTimer (ms), 0x0A ElementStance, 0x0B UmbralHearts, 0x0C PolyglotStacks, 0x0D EnochianFlags (bit0 Enochian, bit1 Paradox, bits 2+ Astral Soul).
        var timerMs = _element != 0 ? (ulong)Math.Clamp((int)MathF.Round(_polyglotTimer * 1000), 0, 30000) : 0;
        var flags = (ulong)((_element != 0 ? 1 : 0) | (_paradox ? 2 : 0) | _astralSoul << 2);
        world.Client.GaugePayload = new(timerMs | (ulong)(byte)(sbyte)_element << 16 | (ulong)(byte)_hearts << 24 | (ulong)(byte)_polyglot << 32 | flags << 40, 0);
    }
}

// Value of resources still held when a run ends, in the same potency units as the executed-action score, so a
// scenario that ends while holding a Polyglot for movement is not penalised the way a truncated fight would be.
// A spent resource displaces one filler GCD (Fire IV under AF3 with Enochian IV ≈ 686 potency).
internal static class BlmTerminalValue
{
    private const float Filler = 300f * 1.8f * 1.27f;
    private const float Enochian = 1.27f;
    public const float PolyglotValue = 890f * Enochian - Filler;
    public const float AstralSoulValue = 500f * 1.8f * Enochian / 6f;
    public const float ParadoxValue = 540f * Enochian - Filler;
    public const float FirestarterValue = 290f * 1.8f * Enochian - Filler + 100f; // instant + 2000 MP saved

    public static float Estimate(BlmCombatState state)
        => state.Polyglot * PolyglotValue
        + state.AstralSoul * AstralSoulValue
        + (state.Paradox ? ParadoxValue : 0)
        + (state.Firestarter ? FirestarterValue : 0);
}
