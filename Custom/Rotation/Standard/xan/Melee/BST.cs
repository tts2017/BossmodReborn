using BossMod.BST;
using SID = BossMod.Autorotation.xan.Custom.BSTStatus;
using static BossMod.AIHints;

namespace BossMod.Autorotation.xan.Custom;

// Beastmaster (limited job, L1-50). The damage loop: the 3-hit axe combo builds TP; instinctual skills (player, shared 5s recast, >=100 TP,
// all TP spent, potency linear in TP) and Trick (familiar skill, 3s recast, familiar TP) each grant a 7s Heart; any two of them within 7s
// form a combo and the multiplier grows with every consecutive link, so the goal is one uninterrupted chain. A link whose affinity is the
// clockwise-next colour (Volant -> Rampant -> Durant -> Eldritch -> Volant) is an intentional combo (grants Sunstrider/Moonstalker); at
// L50 a Sunstrider skill under Moonstalker (or vice versa) is Universality. Summoning a familiar grants One with Nature and (L30+) resets
// Tempered Release; Parting Blow makes the familiar retreat, after which a Battlehorn can resummon it.
// The TP gauges are not readable (no client gauge struct): tiers are inferred from the game's own action status, see ResourceOk/Upgraded.
public sealed class BST(RotationModuleManager manager, Actor player) : Attackxan<AID, TraitID, BST.Strategy>(manager, player, PotionType.Strength)
{
    public struct Strategy : IStrategyCommon
    {
        public Track<Targeting> Targeting;
        public Track<AOEStrategy> AOE;

        [Track("Familiar", Actions = [AID.FirstBattlehorn, AID.SecondBattlehorn, AID.ThirdBattlehorn])]
        public Track<FamiliarStrategy> Familiar;

        [Track("Trick", Action = AID.Trick, MinLevel = 8)]
        public Track<HoldStrategy> Trick;

        [Track("Parting Blow", InternalName = "PartingBlow", Action = AID.PartingBlow, MinLevel = 6)]
        public Track<HoldStrategy> PartingBlow;

        [Track("Tempered Release", InternalName = "TemperedRelease", Actions = [AID.TemperedRelease1, AID.TemperedRelease2], MinLevel = 18)]
        public Track<HoldStrategy> TemperedRelease;

        [Track("Shield Charge", InternalName = "ShieldCharge", Action = AID.ShieldCharge, MinLevel = 24)]
        public Track<HoldStrategy> ShieldCharge;

        [Track("Rally/Rallying Cheer", InternalName = "Rally", Actions = [AID.Rally, AID.RallyingCheer], MinLevel = 28)]
        public Track<HoldStrategy> Rally;

        readonly Targeting IStrategyCommon.Targeting => Targeting.Value;
        readonly AOEStrategy IStrategyCommon.AOE => AOE.Value;
    }

    public enum FamiliarStrategy
    {
        [Option("Summon a familiar whenever none is present (pre-pull, and in combat between weaponskills)")]
        Auto,
        [Option("Do not summon")]
        Off
    }

    public enum HoldStrategy
    {
        [Option("Use according to standard rotation")]
        Auto,
        [Option("Hold")]
        Hold
    }

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("xan BST [Custom]", "Beastmaster", "Standard rotation (xan)|Melee", "xan", RotationModuleQuality.WIP, BitMask.Build(Class.BST), 100).WithStrategies<Strategy>();
    }

    public float VolantHeart;
    public float RampantHeart;
    public float DurantHeart;
    public float EldritchHeart;
    public float SunstriderLeft;
    public float MoonstalkerLeft;
    public float OneWithNature;
    public Actor? Familiar;
    public bool Upgraded; // L50 with >=250 TP: the four skills are transformed into their 1200-potency AoE versions

    public int NumSplashTargets; // upgraded skills: 6y circle around target, 25y range
    public int NumPartingBlowTargets; // 8y circle around target, 25y range
    public int NumChargeTargets; // 6y circle around target, 20y range

    private Enemy? BestSplashTarget;
    private Enemy? BestPartingBlowTarget;
    private Enemy? BestChargeTarget;
    private readonly Dictionary<Actor, int> _circle6Counts = [];

    private int _familiarSlot = -1; // bestiary slot of the familiar we last summoned, -1 = unknown
    private int _masteredStacks; // Mastered Instinct (Rally) estimate, see NoteCasts
    private int _naturalStacks; // Natural Instinct (Rallying Cheer) estimate
    private DateTime _lastSeenCast;
    private float _prevChainLeft;

    private const int OGCDPriorityChainSave = 5; // pushed at GCD priority so it goes before the weaponskill
    private const int OGCDPriorityTemperedRelease = 4;
    private const int OGCDPriorityClockwiseLink = 4;
    private const int OGCDPriorityLink = 3;
    private const int OGCDPriorityPartingBlow = 2;
    private const int OGCDPriorityRally = 2;
    private const int OGCDPriorityShieldCharge = 1;
    private const float SummonSettleTime = 2; // the pet actor appears a moment after the horn resolves; do not queue a second horn meanwhile
    private const int InstinctStackCap = 3;

    private static readonly PositionCheck Circle6 = (primary, other) => TargetInAOECircle(other, primary.Position, 6);
    private static readonly PositionCheck Circle8 = (primary, other) => TargetInAOECircle(other, primary.Position, 8);

    public override void Exec(in Strategy strategy, Enemy? primaryTarget)
    {
        SelectPrimaryTarget(strategy, ref primaryTarget, 3);

        VolantHeart = StatusLeft(SID.VolantHeart);
        RampantHeart = StatusLeft(SID.RampantHeart);
        DurantHeart = StatusLeft(SID.DurantHeart);
        EldritchHeart = StatusLeft(SID.EldritchHeart);
        SunstriderLeft = StatusLeft(SID.Sunstrider);
        MoonstalkerLeft = StatusLeft(SID.Moonstalker);
        OneWithNature = StatusLeft(SID.OneWithNature);
        Familiar = FindFamiliar();
        Upgraded = Unlocked(TraitID.InstinctualMastery) && ActionManagerEx.Instance is { } amex && amex.GetAdjustedActionID((uint)AID.AvalancheAxe) != (uint)AID.AvalancheAxe;

        NoteCasts();

        _circle6Counts.Clear();
        (BestSplashTarget, NumSplashTargets) = SelectTarget(strategy, primaryTarget, Upgraded ? 25 : 3, Circle6, _circle6Counts);
        (BestChargeTarget, NumChargeTargets) = SelectTarget(strategy, primaryTarget, 20, Circle6, _circle6Counts);
        (BestPartingBlowTarget, NumPartingBlowTargets) = SelectTarget(strategy, primaryTarget, 25, Circle8);

        var chainLeft = ChainLeft;

        Summon(strategy);

        if (primaryTarget == null)
        {
            _prevChainLeft = chainLeft;
            return;
        }

        if (CountdownRemaining > 0)
        {
            if (CountdownRemaining < 0.7f && Player.DistanceToHitbox(primaryTarget) <= 3)
                PushGCD(AID.SmashAxe, primaryTarget);
            _prevChainLeft = chainLeft;
            return;
        }

        // the only GCDs: the 3-hit combo (Shieldsplitter's combo bonus is +15 TP); Capture/Beast Mode are not damage
        if (ComboLastMove == AID.AxebladeBite)
            PushGCD(AID.Shieldsplitter, primaryTarget);

        if (ComboLastMove == AID.SmashAxe)
            PushGCD(AID.AxebladeBite, primaryTarget);

        PushGCD(AID.SmashAxe, primaryTarget);

        OGCD(strategy, primaryTarget, chainLeft);

        Hints.GoalZones.Add(Hints.GoalSingleTarget(primaryTarget.Actor, Player, World.Actors, 3f));
        _prevChainLeft = chainLeft;
    }

    private void OGCD(in Strategy strategy, Enemy primaryTarget, float chainLeft)
    {
        var chainAlive = chainLeft > 0;
        var heart = CurrentHeart;
        var familiarAffinity = FamiliarAffinity;

        // player link: the skill the chain wants, gated on TP (>=100) and the shared 5s recast
        var skill = NextInstinctual(heart, chainAlive, familiarAffinity);
        var skillTpOk = skill != AID.None && ResourceOk(skill, primaryTarget.Actor);
        var skillReady = skillTpOk && ReadyIn(skill) <= AnimLock + AnimationLockDelay;

        // familiar link: Trick costs 100 familiar TP (regained from its auto-attacks, Parting Blow, Rallying Cheer)
        var trickUsable = strategy.Trick.Value == HoldStrategy.Auto && Unlocked(AID.Trick) && Familiar != null && Player.InCombat && ResourceOk(AID.Trick, primaryTarget.Actor);
        var trickReady = trickUsable && ReadyIn(AID.Trick) <= AnimLock + AnimationLockDelay;

        // Tempered Release is reset by every summon (L30+) and from L44 grants Lingering Vantage (buffs Parting Blow): use it as soon as the familiar is out
        var temperedRelease = TemperedReleaseAction();
        var temperedUsable = strategy.TemperedRelease.Value == HoldStrategy.Auto && Unlocked(temperedRelease) && OneWithNature > 0 && Player.InCombat;
        if (temperedUsable && CanWeave(temperedRelease))
            PushOGCD(temperedRelease, temperedRelease == AID.TemperedRelease2 ? primaryTarget.Actor : Player, OGCDPriorityTemperedRelease);

        // chain links: both kinds whenever usable (every link raises the multiplier); the clockwise one goes first. A chain whose window
        // ends before the next weave slot is saved by pushing the link ahead of the weaponskill.
        var urgent = chainAlive && chainLeft <= GCD + AnimationLockDelay + 0.6f;
        // when both could start a chain and our skill's colour precedes the familiar's, the skill goes first so Trick lands as the clockwise link
        var skillLeadsIntoTrick = skill != AID.None && Next(AffinityOf(skill)) == familiarAffinity;
        if (trickReady && (chainAlive || !skillReady || !skillLeadsIntoTrick))
        {
            var clockwise = chainAlive && heart != ActionAffinity.None && Next(heart) == familiarAffinity;
            PushLink(AID.Trick, primaryTarget, clockwise ? OGCDPriorityClockwiseLink : OGCDPriorityLink, urgent);
        }

        if (skillReady)
        {
            var clockwise = chainAlive && heart != ActionAffinity.None && Next(heart) == AffinityOf(skill);
            PushLink(skill, Upgraded ? BestSplashTarget ?? primaryTarget : primaryTarget, clockwise ? OGCDPriorityClockwiseLink : OGCDPriorityLink, urgent);
        }

        // Parting Blow (1000 potency, 8y splash) retreats the familiar: not while it can still add links to a live chain, and not ahead
        // of a Tempered Release that is about to be free (its Lingering Vantage from L44 raises Parting Blow's potency)
        var temperedSoon = temperedUsable && ReadyIn(temperedRelease) < 3;
        if (strategy.PartingBlow.Value == HoldStrategy.Auto && Familiar != null && Player.InCombat && !temperedSoon && !(chainAlive && trickUsable) && CanWeave(AID.PartingBlow))
            PushOGCD(AID.PartingBlow, BestPartingBlowTarget ?? primaryTarget, OGCDPriorityPartingBlow);

        if (strategy.Rally.Value == HoldStrategy.Auto && Player.InCombat)
        {
            // Rally: 40 TP + 70 per Mastered Instinct stack (3 stacks = 250 = full gauge); banked until 2+ stacks unless the chain would
            // otherwise break for lack of TP. Only when TP is short, so the gauge never overcaps.
            if (skill != AID.None && !skillTpOk && (_masteredStacks >= 2 || chainAlive && !trickUsable) && CanWeave(AID.Rally))
                PushOGCD(AID.Rally, Player, OGCDPriorityRally);

            // Rallying Cheer: 30 familiar TP + 70 per Natural Instinct stack, same policy for Trick
            if (Familiar != null && Unlocked(AID.Trick) && !ResourceOk(AID.Trick, primaryTarget.Actor) && (_naturalStacks >= 2 || chainAlive && !skillTpOk) && CanWeave(AID.RallyingCheer))
                PushOGCD(AID.RallyingCheer, Player, OGCDPriorityRally);
        }

        // Shield Charge (300 potency from L38, up to 3 charges from L36) moves the player: only to avoid capping charges, or as a gap
        // closer when the target is out of melee range
        var distance = Player.DistanceToHitbox(primaryTarget);
        if (strategy.ShieldCharge.Value == HoldStrategy.Auto && Player.InCombat && distance <= 20 && (MaxChargesIn(AID.ShieldCharge) <= GCD || distance > 3) && CanWeave(AID.ShieldCharge))
            PushOGCD(AID.ShieldCharge, BestChargeTarget ?? primaryTarget, OGCDPriorityShieldCharge);
    }

    private void PushLink(AID aid, Enemy target, int priority, bool urgent)
    {
        if (urgent)
            PushGCD(aid, target, OGCDPriorityChainSave);
        else if (CanWeave(aid))
            PushOGCD(aid, target, priority);
    }

    // Summon whichever unlocked Battlehorn with a beast in its slot is ready first. It is a 1.0s hard cast on its own recast: cast freely
    // while the GCD is idle (pre-pull, no target), otherwise only when it fits before the next weaponskill, never while moving.
    private void Summon(in Strategy strategy)
    {
        if (strategy.Familiar.Value != FamiliarStrategy.Auto || Familiar != null || IsMoving)
            return;

        if (Manager.LastCast.Data is { } last && IsBattlehorn((AID)last.Action.ID) && (World.CurrentTime - Manager.LastCast.Time).TotalSeconds < SummonSettleTime)
            return;

        var best = AID.None;
        var bestReady = float.MaxValue;
        for (var slot = 0; slot < ClientState.NumBeastmasterBeasts; ++slot)
        {
            var horn = Battlehorn(slot);
            if (World.Client.BeastmasterBeasts[slot] == 0 || !Unlocked(horn))
                continue;

            var ready = ReadyIn(horn);
            if (ready < bestReady)
                (best, bestReady) = (horn, ready);
        }

        if (best == AID.None)
            return;

        if (GCD == 0 || CanWeave(bestReady, GetCastTime(best) + 0.1f))
            PushOGCD(best, Player);
    }

    // chain window: the latest link's Heart (or Sunstrider/Moonstalker) is still running
    private float ChainLeft => Utils.MaxAll(VolantHeart, RampantHeart, DurantHeart, EldritchHeart, SunstriderLeft, MoonstalkerLeft);

    // only one Heart should be up; the freshest one is the latest link
    private ActionAffinity CurrentHeart
    {
        get
        {
            var best = ActionAffinity.None;
            var left = 0f;
            if (VolantHeart > left)
                (best, left) = (ActionAffinity.Volant, VolantHeart);
            if (RampantHeart > left)
                (best, left) = (ActionAffinity.Rampant, RampantHeart);
            if (DurantHeart > left)
                (best, left) = (ActionAffinity.Durant, DurantHeart);
            if (EldritchHeart > left)
                best = ActionAffinity.Eldritch;
            return best;
        }
    }

    // Base id to push (the client transforms it at >=250 TP from L50), or None if no instinctual skill is unlocked.
    private AID NextInstinctual(ActionAffinity heart, bool chainAlive, ActionAffinity familiarAffinity)
    {
        if (Upgraded)
        {
            // all four transform, so the Heart colour no longer matters: alternate Sunstrider/Moonstalker for Universality, preferring the
            // shapes that do not move the player (Risen Fall 6y circle, Calamity 10y line) over the rushes (Brutal Rage, Hawkish Talons)
            if (SunstriderLeft > 0)
                return AID.GaleAxe; // -> Calamity (Moonstalker)
            return AID.SpinningAxe; // -> Risen Fall (Sunstrider)
        }

        if (heart != ActionAffinity.None && Unlocked(SkillOf(Next(heart))))
            return SkillOf(Next(heart));

        // starting a chain: the colour whose clockwise-next is the familiar's, so its Trick completes an intentional link
        if (!chainAlive && familiarAffinity != ActionAffinity.None && Unlocked(SkillOf(Prev(familiarAffinity))))
            return SkillOf(Prev(familiarAffinity));

        // otherwise (no chain, or the clockwise skill is not unlocked at this sync level) the head of the longest clockwise run:
        // the full cycle from L16, else Avalanche -> Mistral -> Spinning; a non-clockwise link still extends a live chain
        if (Unlocked(AID.GaleAxe))
            return AID.GaleAxe;
        if (Unlocked(AID.AvalancheAxe))
            return AID.AvalancheAxe;
        return AID.None;
    }

    private static ActionAffinity Next(ActionAffinity a) => a switch
    {
        ActionAffinity.Volant => ActionAffinity.Rampant,
        ActionAffinity.Rampant => ActionAffinity.Durant,
        ActionAffinity.Durant => ActionAffinity.Eldritch,
        ActionAffinity.Eldritch => ActionAffinity.Volant,
        _ => ActionAffinity.None
    };

    private static ActionAffinity Prev(ActionAffinity a) => a switch
    {
        ActionAffinity.Rampant => ActionAffinity.Volant,
        ActionAffinity.Durant => ActionAffinity.Rampant,
        ActionAffinity.Eldritch => ActionAffinity.Durant,
        ActionAffinity.Volant => ActionAffinity.Eldritch,
        _ => ActionAffinity.None
    };

    private static AID SkillOf(ActionAffinity a) => a switch
    {
        ActionAffinity.Rampant => AID.AvalancheAxe,
        ActionAffinity.Durant => AID.MistralAxe,
        ActionAffinity.Eldritch => AID.SpinningAxe,
        ActionAffinity.Volant => AID.GaleAxe,
        _ => AID.None
    };

    private static ActionAffinity AffinityOf(AID aid) => aid switch
    {
        AID.AvalancheAxe => ActionAffinity.Rampant,
        AID.MistralAxe => ActionAffinity.Durant,
        AID.SpinningAxe => ActionAffinity.Eldritch,
        AID.GaleAxe => ActionAffinity.Volant,
        _ => ActionAffinity.None
    };

    // affinity of the summoned familiar's Trick skill (ActionDefinitions.TrickAffinity by bestiary id); None when we do not know which slot is out
    private ActionAffinity FamiliarAffinity
    {
        get
        {
            var slot = _familiarSlot;
            if (slot < 0)
            {
                // a single assigned beast is the only one that can be out
                for (var i = 0; i < ClientState.NumBeastmasterBeasts; ++i)
                {
                    if (World.Client.BeastmasterBeasts[i] == 0)
                        continue;
                    if (slot >= 0)
                        return ActionAffinity.None;
                    slot = i;
                }
                if (slot < 0)
                    return ActionAffinity.None;
            }

            var beast = World.Client.BeastmasterBeasts[slot];
            return beast < ActionDefinitions.TrickAffinity.Length ? ActionDefinitions.TrickAffinity[beast] : ActionAffinity.None;
        }
    }

    private static bool IsBattlehorn(AID aid) => aid is AID.FirstBattlehorn or AID.SecondBattlehorn or AID.ThirdBattlehorn;
    private static AID Battlehorn(int slot) => slot switch { 0 => AID.FirstBattlehorn, 1 => AID.SecondBattlehorn, _ => AID.ThirdBattlehorn };

    private static bool IsPlayerInstinctual(AID aid) => aid is AID.AvalancheAxe or AID.MistralAxe or AID.SpinningAxe or AID.GaleAxe or AID.BrutalRage or AID.HawkishTalons or AID.RisenFall or AID.Calamity;

    // The hotbar holds the self-targeted id (44890); the client substitutes 47092 when the familiar's technique needs a target.
    // Offline there is no client to ask: the targeted id is pushed (guess).
    private AID TemperedReleaseAction() => ActionManagerEx.Instance is { } amex ? (AID)amex.GetAdjustedActionID((uint)AID.TemperedRelease1) : AID.TemperedRelease2;

    // TP (player and familiar gauges) is not exposed to us, so the game is asked whether the action is usable ignoring its recast:
    // nonzero means short on TP (or otherwise blocked). The query goes to the id the client would actually use (the L50 transform).
    // Offline (harness, replays) there is no ActionManager: assume usable.
    private unsafe bool ResourceOk(AID aid, Actor target)
        => ActionManagerEx.Instance is not { } amex || amex.GetActionStatus(new(ActionType.Spell, amex.GetAdjustedActionID((uint)aid)), target.InstanceID, checkRecastActive: false, checkCastingActive: false) == 0;

    private Actor? FindFamiliar()
    {
        foreach (var a in World.Actors)
            if (a.Type == ActorType.Pet && a.OwnerID == Player.InstanceID && !a.IsDead)
                return a;
        return null;
    }

    // Bookkeeping from our own casts: which slot the familiar came from, and the instinct stacks. The stacks are gauge-only values (no
    // status), so they are estimated: a link landed while the previous frame already had a chain running counts as "a combo completed".
    private void NoteCasts()
    {
        var (time, data) = Manager.LastCast;
        if (data == null || time == _lastSeenCast)
            return;
        _lastSeenCast = time;

        var aid = (AID)data.Action.ID;
        if (IsBattlehorn(aid))
            _familiarSlot = aid switch { AID.FirstBattlehorn => 0, AID.SecondBattlehorn => 1, _ => 2 };
        else if (IsPlayerInstinctual(aid))
        {
            if (_prevChainLeft > 0 && Unlocked(TraitID.WildHeartIII))
                _masteredStacks = Math.Min(InstinctStackCap, _masteredStacks + 1);
        }
        else if (aid == AID.Trick)
        {
            if (_prevChainLeft > 0 && Unlocked(TraitID.WildHeartIV))
                _naturalStacks = Math.Min(InstinctStackCap, _naturalStacks + 1);
        }
        else if (aid == AID.Rally)
            _masteredStacks = 0;
        else if (aid == AID.RallyingCheer)
            _naturalStacks = 0;
    }
}
