using System;
using System.Collections.Generic;
using BossMod;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using AID = BossMod.PLD.AID;
using SID = BossMod.PLD.SID;

namespace XanTimelineHarness;

// Per-run Paladin counters, folded into RotationMetrics (one field, so the shared record stays small). Format() and Csv() are only
// used for runs that had a Paladin emulator, which keeps every other job's output byte-identical.
internal readonly record struct PldMetrics(
    bool Present = false,
    int MpOvercap = 0, int MpShortGcds = 0, int FightOrFlights = 0, int FofGcds = 0, int Requiescats = 0, int ReqOutsideFof = 0,
    int ChainsStarted = 0, int ChainsCompleted = 0, int ChainsBroken = 0, int GoringBlades = 0, int GoringLost = 0,
    int DivineMightUsed = 0, int DivineMightLost = 0, int SwordOathPresses = 0, int SwordOathLost = 0, int HolyHardCasts = 0,
    int BladeOfHonors = 0, int BladeOfHonorLost = 0, int Intervenes = 0, int Expiacions = 0, int CircleOfScorns = 0,
    int ComboAbandoned = 0, int ReqStacksLost = 0, int CastInterrupts = 0, int Holy = 0, int ReqChainGcds = 0)
{
    public const string CsvHeader = "pld_mp_overcap,pld_mp_short_gcds,pld_fof,pld_fof_gcds,pld_req,pld_req_outside_fof,pld_chains,pld_chains_done,pld_chains_broken,pld_goring,pld_goring_lost,pld_dm_used,pld_dm_lost,pld_oath_presses,pld_oath_lost,pld_hs_hardcast,pld_boh,pld_boh_lost,pld_intervene,pld_expiacion,pld_cos,pld_combo_abandoned,pld_req_stacks_lost,pld_cast_interrupts,pld_holy,pld_req_chain_gcds";

    public PldMetrics Add(PldMetrics o) => new(Present || o.Present,
        MpOvercap + o.MpOvercap, MpShortGcds + o.MpShortGcds, FightOrFlights + o.FightOrFlights, FofGcds + o.FofGcds, Requiescats + o.Requiescats, ReqOutsideFof + o.ReqOutsideFof,
        ChainsStarted + o.ChainsStarted, ChainsCompleted + o.ChainsCompleted, ChainsBroken + o.ChainsBroken, GoringBlades + o.GoringBlades, GoringLost + o.GoringLost,
        DivineMightUsed + o.DivineMightUsed, DivineMightLost + o.DivineMightLost, SwordOathPresses + o.SwordOathPresses, SwordOathLost + o.SwordOathLost, HolyHardCasts + o.HolyHardCasts,
        BladeOfHonors + o.BladeOfHonors, BladeOfHonorLost + o.BladeOfHonorLost, Intervenes + o.Intervenes, Expiacions + o.Expiacions, CircleOfScorns + o.CircleOfScorns,
        ComboAbandoned + o.ComboAbandoned, ReqStacksLost + o.ReqStacksLost, CastInterrupts + o.CastInterrupts, Holy + o.Holy, ReqChainGcds + o.ReqChainGcds);

    public string Format() => FormattableString.Invariant($"pld_mp_overcap={MpOvercap} pld_mp_short_gcds={MpShortGcds} pld_fof={FightOrFlights} pld_fof_gcds={FofGcds} pld_req={Requiescats} pld_req_outside_fof={ReqOutsideFof} pld_chains={ChainsStarted} pld_chains_done={ChainsCompleted} pld_chains_broken={ChainsBroken} pld_goring={GoringBlades} pld_goring_lost={GoringLost} pld_dm_used={DivineMightUsed} pld_dm_lost={DivineMightLost} pld_oath_presses={SwordOathPresses} pld_oath_lost={SwordOathLost} pld_hs_hardcast={HolyHardCasts} pld_boh={BladeOfHonors} pld_boh_lost={BladeOfHonorLost} pld_intervene={Intervenes} pld_expiacion={Expiacions} pld_cos={CircleOfScorns} pld_combo_abandoned={ComboAbandoned} pld_req_stacks_lost={ReqStacksLost} pld_cast_interrupts={CastInterrupts} pld_holy={Holy} pld_req_chain_gcds={ReqChainGcds}");

    public string Csv() => FormattableString.Invariant($"{MpOvercap},{MpShortGcds},{FightOrFlights},{FofGcds},{Requiescats},{ReqOutsideFof},{ChainsStarted},{ChainsCompleted},{ChainsBroken},{GoringBlades},{GoringLost},{DivineMightUsed},{DivineMightLost},{SwordOathPresses},{SwordOathLost},{HolyHardCasts},{BladeOfHonors},{BladeOfHonorLost},{Intervenes},{Expiacions},{CircleOfScorns},{ComboAbandoned},{ReqStacksLost},{CastInterrupts},{Holy},{ReqChainGcds}");
}

// Deterministic action-result simulation for Paladin (7.5), in the style of GnbCombatState and RprCombatState. Decisions come from production
// AkechiPLD.cs; this only models what the client does with them. Every rule below was checked against logged Paladin actions of other
// players (pld-replay-check, the numbers are in the report):
//  - the 1-2-3 combo (Fast Blade, Riot Blade, Royal Authority / Rage of Halone) and the AoE combo (Total Eclipse, Prominence). Only the
//    combo actions touch the combo (Action sheet PreservesCombo): Atonement, Supplication, Sepulchre, Holy Spirit/Circle, Confiteor and the
//    Blades, Goring Blade and Shield Lob all leave it running. A combo action pressed out of order resets it (and does not start a new one);
//    a finisher stays as the combo state for its 30 s.
//  - Royal Authority (finisher of the combo) grants Atonement Ready and Divine Might and REMOVES Supplication Ready / Sepulchre Ready;
//    Prominence (finisher) grants Divine Might. Atonement -> Supplication Ready -> Sepulchre Ready, each press removes its own status.
//  - Holy Spirit / Holy Circle: 1.5 s cast, instant with Divine Might or Requiescat; Divine Might is consumed first, otherwise one Requiescat
//    stack. Imperator / Requiescat: 4 stacks and Confiteor Ready. Confiteor, Blade of Faith / Truth / Valor each spend a stack if there is one
//    (the chain still runs without stacks, at its lower potency) and walk the gauge step 0 -> 1 -> 2 -> 3 -> 0; Valor grants Blade of Honor Ready.
//  - MP: Holy Spirit, Holy Circle, Confiteor and every Blade cost 1000 when they resolve (a cast pays at its end); Riot Blade in combo +1000
//    0.75 s later, Prominence in combo +1000 after 0.63 s, Expiacion / Spirits Within +500 after 0.36 / 0.89 s, Atonement, Supplication and
//    Sepulchre +400 after 1.3 / 1.15 / 1.3 s; regeneration +200 every 3 s in combat (+600 out of it).
// No party attacks, no packet latency and no damage rolls, so two runs of the same build are byte-identical.
internal sealed class PldCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float> onExecuted)
{
    // counters: see PldMetrics
    public int MpOvercap { get; private set; }
    public int MpShortGcds { get; private set; }
    public int FightOrFlights { get; private set; }
    public int FofGcds { get; private set; }
    public int Requiescats { get; private set; }
    public int ReqOutsideFof { get; private set; }
    public int ChainsStarted { get; private set; }
    public int ChainsCompleted { get; private set; }
    public int ChainsBroken { get; private set; }
    public int GoringBlades { get; private set; }
    public int GoringLost { get; private set; }
    public int DivineMightUsed { get; private set; }
    public int DivineMightLost { get; private set; }
    public int SwordOathPresses { get; private set; }
    public int SwordOathLost { get; private set; }
    public int HolyHardCasts { get; private set; }
    public int BladeOfHonors { get; private set; }
    public int BladeOfHonorLost { get; private set; }
    public int Intervenes { get; private set; }
    public int Expiacions { get; private set; }
    public int CircleOfScorns { get; private set; }
    public int ComboAbandoned { get; private set; }
    public int ReqStacksLost { get; private set; }
    public int InterruptedCasts { get; private set; }
    public int HolyCasts { get; private set; }
    public int ReqChainGcds { get; private set; }
    public List<string> Events { get; } = [];
    public DateTime BaseTime { get; init; }

    public PldMetrics Metrics => new(true, MpOvercap, MpShortGcds, FightOrFlights, FofGcds, Requiescats, ReqOutsideFof, ChainsStarted, ChainsCompleted, ChainsBroken, GoringBlades, GoringLost,
        DivineMightUsed, DivineMightLost, SwordOathPresses, SwordOathLost, HolyHardCasts, BladeOfHonors, BladeOfHonorLost, Intervenes, Expiacions, CircleOfScorns,
        ComboAbandoned, ReqStacksLost, InterruptedCasts, HolyCasts, ReqChainGcds);

    // the gauge step the module reads (PaladinGauge.ConfiteorComboStep): 0 = Confiteor next, 1 = Faith, 2 = Truth, 3 = Valor
    private int _bladeStep;
    private DateTime _bladeStepEnd;
    private bool _chainOpen;
    private uint _sequence;
    private ActionQueue.Entry? _casting;
    private DateTime _castFinish;
    private DateTime _nextRegen;
    private readonly List<(DateTime At, int Amount)> _mpEffects = [];

    public int BladeStep => _bladeStep;
    public int RequiescatStacks => Stacks(SID.Requiescat);
    public int Mp => (int)player.HPMP.CurMP;
    public bool EnforceRangeAndMovement { get; set; }
    public bool Moving { get; set; }
    // replay check: when set, every status the emulator sets or removes on the player is appended here
    public List<(DateTime At, SID Status, bool Gain, int Stacks)>? StatusTrace { get; set; }
    // replay check: first MP regeneration tick (the server tick is not aligned with the pull); null anchors the ticks at BaseTime
    public DateTime? RegenAnchor { get; set; }
    // replay check: every change of the emulator's MP, so a logged MP value can be matched against the recent past
    public List<(DateTime At, int Mp)>? MpTrace { get; set; }
    internal bool Holds(SID status) => Has(status);
    internal int StackCount(SID status) => Stacks(status);

    private static readonly bool DropLocked = Environment.GetEnvironmentVariable("XAN_HARNESS_DROP_LOCKED") == "1";

    private const float FightOrFlightDuration = 20f;
    private const float ReadyDuration = 30f;
    private const float RequiescatDuration = 30f;
    private const float DivineMightDuration = 30f;
    private const float ComboDuration = 30f;
    private const float BladeChainDuration = 30f;
    private const float SlidecastWindow = 0.5f;
    private const float MeleeRadius = 5f;
    private const int HolyCost = 1000;
    private const int ClemencyCost = 2000;
    private const int RegenPerTick = 200;
    private const int RegenPerTickOutOfCombat = 600;
    private const float RegenInterval = 3f;

    // delays of the MP restores after the action resolves, measured as the median offset between the logged action and the MP change
    private const int RiotBladeMp = 1000;
    private const float RiotBladeMpDelay = 0.75f;
    private const int ProminenceMp = 1000;
    private const float ProminenceMpDelay = 0.63f;
    private const int SpiritsMp = 500;
    private const float ExpiacionMpDelay = 0.36f;
    private const float SpiritsWithinMpDelay = 0.89f;
    private const int SwordOathMp = 400;
    private const float AtonementMpDelay = 1.3f;
    private const float SupplicationMpDelay = 1.15f;
    private const float SepulchreMpDelay = 1.3f;

    public void Advance()
    {
        foreach (var actor in world.Actors)
            for (var i = 0; i < actor.Statuses.Length; ++i)
                if (actor.Statuses[i].ID != 0 && actor.Statuses[i].ExpireAt <= world.CurrentTime)
                {
                    var expired = (SID)actor.Statuses[i].ID;
                    var stacks = actor.Statuses[i].Extra & 0xFF;
                    var mine = actor == player && actor.Statuses[i].SourceID == player.InstanceID;
                    world.Execute(new ActorState.OpStatus(actor.InstanceID, i, default));
                    if (mine)
                    {
                        StatusTrace?.Add((world.CurrentTime, expired, false, 0));
                        OnExpired(expired, stacks);
                    }
                }

        // MP: the regeneration ticks, then the scheduled restores of earlier actions
        if (_nextRegen == default)
            _nextRegen = (RegenAnchor ?? BaseTime).AddSeconds(RegenInterval);
        while (world.CurrentTime >= _nextRegen)
        {
            SetMp(Math.Min((int)player.HPMP.MaxMP, Mp + (player.InCombat ? RegenPerTick : RegenPerTickOutOfCombat)));
            _nextRegen = _nextRegen.AddSeconds(RegenInterval);
        }
        if (_mpEffects.Count > 0)
        {
            _mpEffects.Sort((a, b) => a.At.CompareTo(b.At));
            var due = 0;
            while (due < _mpEffects.Count && _mpEffects[due].At <= world.CurrentTime)
                RestoreMp(_mpEffects[due++].Amount);
            _mpEffects.RemoveRange(0, due);
        }

        if (_bladeStep > 0 && world.CurrentTime >= _bladeStepEnd)
            EndChain(completed: false, "timer");

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

    public void ExecuteBestAction(AIHints hints)
    {
        if (_casting != null || player.IsDead)
            return;

        // The real client enforces job resources and statuses after the shared queue chooses a candidate, and a refused choice costs the
        // frame (ActionManagerEx logs "Can't execute" and tries again next frame). By default every infeasible candidate is dropped before
        // the choice instead; XAN_HARNESS_CLIENT_REJECT=1 keeps the job requirements for after the choice. Range and target validity stay
        // before it: the shared queue checks those.
        if (ClientReject.Enabled)
            hints.ActionsToExecute.Entries.RemoveAll(entry => !QueueConsiders(entry));
        else
            hints.ActionsToExecute.Entries.RemoveAll(entry => !CanExecute(entry));
        // XAN_HARNESS_DROP_LOCKED=1 (experiment): drop the entries of a locked category before the queue sees them, which is what a module that reads
        // ActionLocks does (AkechiGNB.DropLockedQueuedActions); AkechiPLD does not, and this measures what the change is worth
        if (DropLocked && Irregular.Current is { } irregular)
            hints.ActionsToExecute.Entries.RemoveAll(e => ActionDefinitions.Instance[e.Action] is { } locked && irregular.LockReason(locked, player) != null);
        SearchControl.FilterSuppressed(hints.ActionsToExecute.Entries, world); // oracle-search: actions held by the search stay out of the queue
        var entry = hints.ActionsToExecute.FindBest(world, player, world.Client.Cooldowns, world.Client.AnimationLock, hints, frameStep, true);
        var definition = ActionDefinitions.Instance[entry.Action];
        if (entry.Action.ID == 0 || definition == null
            || MathF.Max(entry.Delay, MathF.Max(world.Client.AnimationLock, definition.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions))) > 0.001f)
            return;
        if (Irregular.Refuses("pld", entry, definition, world, player)) // --irregular lockout statuses, refused after the pick like the client
            return;
        if (ClientReject.Enabled && !CanExecute(entry))
        {
            ClientReject.Record("pld", entry.Action, world.CurrentTime, player);
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

    // oracle-search: emulator state shown next to each decision of the deviation log
    private string DescribeForSearch() => FormattableString.Invariant($"mp={Mp} step={_bladeStep} req={Stacks(SID.Requiescat)} dm={(Has(SID.DivineMight) ? 1 : 0)} ao={(Has(SID.AtonementReady) ? 1 : 0)}{(Has(SID.SupplicationReady) ? 1 : 0)}{(Has(SID.SepulchreReady) ? 1 : 0)} fof={(Has(SID.FightOrFlight) ? 1 : 0)} gb={(Has(SID.GoringBladeReady) ? 1 : 0)}");

    // burst study (BurstControl.cs): the state shown next to a cycle and the emulator's own readiness rules for a starter
    public string BurstState() => DescribeForSearch();
    public bool BurstFeasible(ActionQueue.Entry entry) => CanExecute(entry);

    // the client rejects out-of-range targets and cast starts while moving (casts made instant by Divine Might / Requiescat are fine)
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

    private bool CanExecute(ActionQueue.Entry entry)
    {
        if (!QueueConsiders(entry))
            return false;
        return entry.Action.Type != ActionType.Spell || JobRulesAllow((AID)entry.Action.ID);
    }

    // what the game itself checks when the action is pressed: statuses, the gauge step, MP, and the Imperator / Blade of Honor share of one hotbar slot
    private bool JobRulesAllow(AID action) => action switch
    {
        AID.HolySpirit or AID.HolyCircle => Mp >= HolyCost,
        AID.Clemency => Mp >= ClemencyCost,
        AID.Confiteor => Has(SID.ConfiteorReady) && _bladeStep == 0 && Mp >= HolyCost,
        AID.BladeOfFaith => _bladeStep == 1 && Mp >= HolyCost,
        AID.BladeOfTruth => _bladeStep == 2 && Mp >= HolyCost,
        AID.BladeOfValor => _bladeStep == 3 && Mp >= HolyCost,
        AID.Atonement => Has(SID.AtonementReady),
        AID.Supplication => Has(SID.SupplicationReady),
        AID.Sepulchre => Has(SID.SepulchreReady),
        AID.GoringBlade => Has(SID.GoringBladeReady),
        AID.BladeOfHonor => Has(SID.BladeOfHonorReady),
        // Imperator and Blade of Honor are one button: while Blade of Honor Ready is up the press is Blade of Honor
        AID.Imperator => !Has(SID.BladeOfHonorReady),
        _ => true
    };

    // logged-action check (pld-replay-check): why the emulator would have refused the press, or null
    internal string? RefusalReason(ActionID action)
    {
        if (action.Type != ActionType.Spell || ActionDefinitions.Instance[action] is not { } definition)
            return null;
        if (!JobRulesAllow((AID)action.ID))
            return "job-rule";
        if (definition.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions) > 0.15f)
            return "cooldown";
        return null;
    }

    private float GCDLength(ActionDefinition definition)
    {
        var stats = world.Client.PlayerStats;
        var speed = definition.Category == ActionCategory.Spell ? stats.SpellSpeed : stats.SkillSpeed;
        return ActionSpeed.GCDRounded(speed, stats.Haste, player.Level);
    }

    private float CastTime(ActionID action, ActionDefinition definition)
    {
        if ((AID)action.ID is AID.HolySpirit or AID.HolyCircle && (Has(SID.DivineMight) || Has(SID.Requiescat)) || (AID)action.ID == AID.Clemency && Has(SID.Requiescat))
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
        var capCharges = Math.Max(1, definition.MaxChargesAtCap());
        var levelCharges = Math.Clamp(definition.MaxChargesAtLevel(player.Level), 1, capCharges);
        var current = world.Client.Cooldowns[group];
        var elapsed = current.Total > 0 ? MathF.Max(0, current.Elapsed - definition.Cooldown) : definition.Cooldown * (levelCharges - 1);
        world.Execute(new ClientState.OpCooldown(false, [(group, new(elapsed, definition.Cooldown * capCharges))]));
        if (definition.ExtraCooldownGroup >= 0 && definition.ExtraCooldownGroup != ActionDefinitions.GCDGroup)
            world.Execute(new ClientState.OpCooldown(false, [(definition.ExtraCooldownGroup, new(0, 1))]));
    }

    // the game runs an upgraded action as its upgrade (GetAdjustedActionId): Rage of Halone is Royal Authority from level 60, Spirits Within is
    // Expiacion from level 86. AkechiPLD already pushes the upgrades by name; the xan PLD module pushes the old names and relies on this.
    private AID Adjusted(AID action) => action == AID.RageOfHalone && player.Level >= 60 ? AID.RoyalAuthority
        : action == AID.SpiritsWithin && player.Level >= 86 ? AID.Expiacion : action;

    private void Complete(ActionQueue.Entry entry, ActionDefinition definition, bool casted)
    {
        if (entry.Action.Type == ActionType.Spell && Adjusted((AID)entry.Action.ID) is var adjusted && adjusted != (AID)entry.Action.ID)
            entry = new ActionQueue.Entry(ActionID.MakeSpell(adjusted), entry.Target, entry.Priority, entry.Expire, entry.Delay, entry.CastTime, entry.TargetPos, entry.FacingAngle, entry.Manual, entry.Force);
        var targetID = entry.Target?.InstanceID ?? player.InstanceID;
        var animationLock = (casted ? definition.CastAnimLock : definition.InstantAnimLock) + frameStep;
        world.Execute(new ClientState.OpAnimationLockChange(animationLock));
        var castEvent = new ActorCastEvent(entry.Action, targetID, animationLock, 1, entry.Target?.PosRot.XYZ() ?? entry.TargetPos, ++_sequence, _sequence, player.Rotation);
        world.Execute(new ActorState.OpCastEvent(player.InstanceID, castEvent));

        // scored before Resolve so the combo, Divine Might, Requiescat and Fight or Flight are the ones the action is about to use
        var potency = entry.Action.Type == ActionType.Spell
            ? PldPotencyScorer.Estimate(world, player, (AID)entry.Action.ID, entry.Target)
            : 0f;

        if (entry.Action.Type == ActionType.Spell)
            Resolve((AID)entry.Action.ID, entry.Target, definition, casted);
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

    // pld-replay-check: apply the result of an action another player pressed (the log time is when the effect resolved, so a cast is complete)
    internal void ResolveLogged(ActionID action, Actor? target, bool hardCast)
    {
        if (action.Type != ActionType.Spell || ActionDefinitions.Instance[action] is not { } definition)
            return;
        if (definition.Cooldown > 0 && !definition.IsGCD)
            StartCooldown(definition);
        Resolve((AID)action.ID, target, definition, casted: hardCast);
        PublishGauge();
    }

    private void Resolve(AID action, Actor? target, ActionDefinition definition, bool casted)
    {
        var combo = (AID)world.Client.ComboState.Action;
        var hitsEnemy = target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true } || IsSelfAOE(action) && HasNearbyEnemy();

        if (definition.IsGCD)
        {
            if (Has(SID.FightOrFlight))
                ++FofGcds;
            // an MP-gated proc was waiting and the GCD that went out is not the one that uses it: the rotation ran out of MP
            if (Mp < HolyCost && !IsMpAction(action) && (Has(SID.DivineMight) || Has(SID.ConfiteorReady) || _bladeStep > 0))
            {
                ++MpShortGcds;
                Events.Add(FormattableString.Invariant($"mp_short,{Now():f2},{action},mp={Mp}"));
            }
        }

        switch (action)
        {
            // ---- single-target combo ----
            case AID.FastBlade:
                if (combo is AID.FastBlade or AID.RiotBlade or AID.TotalEclipse)
                    ++ComboAbandoned;
                SetCombo(hitsEnemy ? action : AID.None);
                break;
            case AID.RiotBlade:
                {
                    var linked = hitsEnemy && combo == AID.FastBlade;
                    SetCombo(linked ? action : AID.None);
                    if (linked && player.Level >= 58)
                        ScheduleMp(RiotBladeMp, RiotBladeMpDelay);
                    break;
                }
            case AID.RageOfHalone:
            case AID.RoyalAuthority:
                {
                    var linked = hitsEnemy && combo == AID.RiotBlade;
                    // the finisher stays as the combo state with a fresh timer (the logged client combo of other jobs shows the same: the last combo
                    // action, 30 s, until the next combo action); only an out-of-order press resets it. AkechiPLD reads ComboLastMove == Royal Authority.
                    SetCombo(linked ? action : AID.None);
                    // Rage of Halone grants nothing; Royal Authority (L60) grants Divine Might (L64) and Atonement Ready (L76) and ends Sword Oath
                    if (linked && action == AID.RoyalAuthority)
                    {
                        if (player.Level >= 64)
                            GrantDivineMight();
                        if (player.Level >= 76)
                        {
                            // the chain restarts: Supplication Ready and Sepulchre Ready are removed, an Atonement Ready still held is refreshed
                            if (Has(SID.SupplicationReady))
                            {
                                Remove(SID.SupplicationReady);
                                ++SwordOathLost;
                            }
                            if (Has(SID.SepulchreReady))
                            {
                                Remove(SID.SepulchreReady);
                                ++SwordOathLost;
                            }
                            if (Has(SID.AtonementReady))
                                ++SwordOathLost;
                            Set(SID.AtonementReady, ReadyDuration);
                        }
                    }
                    break;
                }

            case AID.ShieldBash:
                SetCombo(AID.None);
                break;

            // ---- AoE combo ----
            case AID.TotalEclipse:
                if (combo is AID.FastBlade or AID.RiotBlade or AID.TotalEclipse)
                    ++ComboAbandoned;
                SetCombo(hitsEnemy ? action : AID.None);
                break;
            case AID.Prominence:
                {
                    var linked = hitsEnemy && combo == AID.TotalEclipse;
                    SetCombo(linked ? action : AID.None);
                    if (linked && player.Level >= 72)
                    {
                        GrantDivineMight();
                        ScheduleMp(ProminenceMp, ProminenceMpDelay);
                    }
                    break;
                }

            // ---- Sword Oath ----
            case AID.Atonement:
                Remove(SID.AtonementReady);
                Set(SID.SupplicationReady, ReadyDuration);
                ++SwordOathPresses;
                ScheduleMp(SwordOathMp, AtonementMpDelay);
                break;
            case AID.Supplication:
                Remove(SID.SupplicationReady);
                Set(SID.SepulchreReady, ReadyDuration);
                ++SwordOathPresses;
                ScheduleMp(SwordOathMp, SupplicationMpDelay);
                break;
            case AID.Sepulchre:
                Remove(SID.SepulchreReady);
                ++SwordOathPresses;
                ScheduleMp(SwordOathMp, SepulchreMpDelay);
                break;

            // ---- Holy Spirit / Holy Circle ----
            case AID.HolySpirit:
            case AID.HolyCircle:
                ++HolyCasts;
                if (casted)
                    ++HolyHardCasts;
                SpendMp(HolyCost);
                if (Has(SID.DivineMight))
                {
                    Remove(SID.DivineMight);
                    ++DivineMightUsed;
                }
                else if (Has(SID.Requiescat))
                {
                    ++ReqChainGcds;
                    SpendRequiescatStack();
                }
                break;

            // Clemency (a heal, not pressed by the module): 2000 MP, instant under Requiescat and then spending a stack (Divine Might does not help it)
            case AID.Clemency:
                SpendMp(ClemencyCost);
                if (Has(SID.Requiescat))
                    SpendRequiescatStack();
                break;

            // ---- Confiteor chain ----
            case AID.Confiteor:
                SpendMp(HolyCost);
                Remove(SID.ConfiteorReady);
                SpendRequiescatStack();
                ++ReqChainGcds;
                if (player.Level >= 90)
                {
                    _bladeStep = 1;
                    _bladeStepEnd = world.CurrentTime.AddSeconds(BladeChainDuration);
                    _chainOpen = true;
                    ++ChainsStarted;
                }
                break;
            case AID.BladeOfFaith:
            case AID.BladeOfTruth:
            case AID.BladeOfValor:
                SpendMp(HolyCost);
                SpendRequiescatStack();
                ++ReqChainGcds;
                if (action == AID.BladeOfValor)
                {
                    EndChain(completed: true, "done");
                    if (player.Level >= 100)
                        Set(SID.BladeOfHonorReady, ReadyDuration);
                }
                else
                {
                    ++_bladeStep;
                }
                break;
            case AID.BladeOfHonor:
                Remove(SID.BladeOfHonorReady);
                ++BladeOfHonors;
                break;

            // ---- buffs and the rest of the oGCDs ----
            case AID.FightOrFlight:
                ++FightOrFlights;
                Set(SID.FightOrFlight, FightOrFlightDuration);
                if (Unlocked(AID.GoringBlade))
                {
                    if (Has(SID.GoringBladeReady))
                        ++GoringLost;
                    Set(SID.GoringBladeReady, ReadyDuration);
                }
                break;
            case AID.GoringBlade:
                Remove(SID.GoringBladeReady);
                ++GoringBlades;
                break;
            case AID.Requiescat:
            case AID.Imperator:
                ++Requiescats;
                if (!Has(SID.FightOrFlight))
                    ++ReqOutsideFof;
                if (Has(SID.Requiescat))
                    ReqStacksLost += Stacks(SID.Requiescat);
                Set(SID.Requiescat, RequiescatDuration, 4);
                if (player.Level >= 80)
                    Set(SID.ConfiteorReady, ReadyDuration);
                break;
            case AID.Intervene:
                ++Intervenes;
                break;
            case AID.SpiritsWithin:
                ++Expiacions;
                ScheduleMp(SpiritsMp, SpiritsWithinMpDelay);
                break;
            case AID.Expiacion:
                ++Expiacions;
                ScheduleMp(SpiritsMp, ExpiacionMpDelay);
                break;
            case AID.CircleOfScorn:
                ++CircleOfScorns;
                break;
        }
    }

    // ---- statuses and gauge ----

    private void GrantDivineMight()
    {
        if (Has(SID.DivineMight))
        {
            ++DivineMightLost;
            Events.Add(FormattableString.Invariant($"dm_overwritten,{Now():f2}"));
        }
        Set(SID.DivineMight, DivineMightDuration);
    }

    private void SpendRequiescatStack()
    {
        var remaining = Stacks(SID.Requiescat) - 1;
        if (remaining > 0)
            Set(SID.Requiescat, Left(SID.Requiescat), remaining);
        else
            Remove(SID.Requiescat);
    }

    private void EndChain(bool completed, string why)
    {
        if (_chainOpen)
        {
            if (completed)
                ++ChainsCompleted;
            else
            {
                ++ChainsBroken;
                Events.Add(FormattableString.Invariant($"chain_broken,{Now():f2},{why},step={_bladeStep}"));
            }
        }
        _chainOpen = false;
        _bladeStep = 0;
    }

    // a status that ran out unused: the counters the rotation is judged on
    private void OnExpired(SID status, int stacks)
    {
        switch (status)
        {
            case SID.AtonementReady or SID.SupplicationReady or SID.SepulchreReady:
                ++SwordOathLost;
                Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{status}"));
                break;
            case SID.DivineMight:
                ++DivineMightLost;
                Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{status}"));
                break;
            case SID.GoringBladeReady:
                ++GoringLost;
                Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{status}"));
                break;
            case SID.BladeOfHonorReady:
                ++BladeOfHonorLost;
                Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{status}"));
                break;
            case SID.ConfiteorReady:
                // Confiteor Ready that ran out before the chain started
                ++ChainsBroken;
                Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{status}"));
                break;
            case SID.Requiescat:
                ReqStacksLost += stacks;
                if (stacks > 0)
                    Events.Add(FormattableString.Invariant($"proc_lost,{Now():f2},{status},stacks={stacks}"));
                break;
        }
    }

    private static bool IsMpAction(AID action) => action is AID.HolySpirit or AID.HolyCircle or AID.Confiteor or AID.BladeOfFaith or AID.BladeOfTruth or AID.BladeOfValor;
    private static bool IsSelfAOE(AID action) => action is AID.TotalEclipse or AID.Prominence or AID.HolyCircle or AID.CircleOfScorn;

    private bool Unlocked(AID action) => ActionDefinitions.Instance.Spell(action) is { } def && player.Level >= def.MinLevel;

    private bool HasNearbyEnemy()
    {
        foreach (var actor in world.Actors)
            if (actor.Type == ActorType.Enemy && !actor.IsDead && actor.IsTargetable && player.DistanceToHitbox(actor) <= MeleeRadius)
                return true;
        return false;
    }

    private float Now() => (float)(world.CurrentTime - BaseTime).TotalSeconds;
    private bool Has(SID status) => Left(status) > 0;
    private float Left(SID status) => MathF.Max(0, (float)((player.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);
    private int Stacks(SID status) => player.FindStatus((uint)status, player.InstanceID)?.Extra & 0xFF ?? 0;

    private void SetCombo(AID action) => world.Execute(new ClientState.OpComboChange(action == AID.None ? default : new((uint)action, ComboDuration)));

    private void Set(SID status, float duration, int stacks = 0)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot < 0)
            slot = Array.FindIndex(player.Statuses, s => s.ID == 0);
        if (slot < 0)
            throw new InvalidOperationException("PLD simulation ran out of status slots.");
        world.Execute(new ActorState.OpStatus(player.InstanceID, slot, new((uint)status, (ushort)stacks, duration == float.MaxValue ? DateTime.MaxValue : world.CurrentTime.AddSeconds(duration), player.InstanceID)));
        StatusTrace?.Add((world.CurrentTime, status, true, stacks));
    }

    private void Remove(SID status)
    {
        var slot = Array.FindIndex(player.Statuses, s => s.ID == (uint)status && s.SourceID == player.InstanceID);
        if (slot >= 0)
        {
            world.Execute(new ActorState.OpStatus(player.InstanceID, slot, default));
            StatusTrace?.Add((world.CurrentTime, status, false, 0));
        }
    }

    // ---- MP ----

    private void SetMp(int value)
    {
        var hp = player.HPMP;
        var clamped = (uint)Math.Clamp(value, 0, (int)hp.MaxMP);
        if (clamped == hp.CurMP)
            return;
        world.Execute(new ActorState.OpHPMP(player.InstanceID, new(hp.CurHP, hp.MaxHP, hp.Shield, clamped, hp.MaxMP)));
        MpTrace?.Add((world.CurrentTime, (int)clamped));
    }

    // replay check: start from the MP the log shows
    internal void InitMp(int value) => SetMp(value);

    private void SpendMp(int amount) => SetMp(Mp - amount);

    private void ScheduleMp(int amount, float delay) => _mpEffects.Add((world.CurrentTime.AddSeconds(delay), amount));

    private void RestoreMp(int amount)
    {
        var max = (int)player.HPMP.MaxMP;
        MpOvercap += Math.Max(0, Mp + amount - max);
        SetMp(Math.Min(max, Mp + amount));
    }

    // FFXIVClientStructs PaladinGauge: written through the struct itself, so the layout is whatever the game's is; GetGauge reads it back the same way
    private unsafe void PublishGauge()
    {
        PaladinGauge gauge = default;
        gauge.OathGauge = 100;
        gauge.ConfiteorComboStep = (byte)_bladeStep;
        world.Client.GaugePayload = new(((ulong*)&gauge)[1], 0);
    }
}
