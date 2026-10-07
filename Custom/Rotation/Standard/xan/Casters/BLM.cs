using BossMod.BLM;
using BossMod.Data;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.xan.Custom;

public sealed class BLM(RotationModuleManager manager, Actor player) : Castxan<AID, TraitID, BLM.Strategy>(manager, player, PotionType.Intelligence)
{
    public struct Strategy : IStrategyCommon
    {
        public Track<Targeting> Targeting;
        public Track<AOEStrategy> AOE;

        [Track("Rotation mode", Actions = [AID.Xenoglossy, AID.Foul])]
        public Track<RotationStrategy> Rotation;

        [Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)]
        public Track<MechanicHintStrategy> MechanicHints;

        [Track(Action = AID.Scathe)]
        public Track<ScatheStrategy> Scathe;

        [Track(InternalName = "DoT", Actions = [AID.Thunder1, AID.Thunder2, AID.Thunder3, AID.Thunder4, AID.HighThunder, AID.HighThunder2])]
        public Track<ThunderStrategy> Thunder;

        [Track(InternalName = "LL", Actions = [AID.LeyLines, AID.Retrace, AID.BetweenTheLines])]
        public Track<LeylinesStrategy> Leylines;

        [Track(InternalName = "TC", Action = AID.Triplecast)]
        public Track<TriplecastStrategy> Triplecast;

        // Swiftcast used to ride on the Triplecast track, so turning Triplecast off silently took Swiftcast with it.
        [Track(InternalName = "SC", Action = AID.Swiftcast)]
        public Track<SwiftcastStrategy> Swiftcast;

        [Track("Phantom Samurai: Use Iainuki on cooldown", UiPriority = -10, MinLevel = 100, Action = PhantomID.Iainuki)]
        public Track<EnabledByDefault> Iainuki;

        [Track("Phantom Samurai: Use Zeninage under raid buffs (coffer required)", UiPriority = -10, MinLevel = 100, Action = PhantomID.Zeninage)]
        public Track<EnabledByDefault> Zeninage;

        [Track("Allow automatic usage of Leylines while moving", Action = AID.LeyLines)]
        public Track<DisabledByDefault> LLMove;

        [Track("Phantom Time Mage: Use Occult Quick/Occult Comet on cooldown", UiPriority = -10, MinLevel = 100, Actions = [PhantomID.OccultQuick, PhantomID.OccultComet])]
        public Track<EnabledByDefault> AutoTimeMage;

        [Track(Action = AID.Manafont)]
        public Track<OffensiveStrategy> Manafont;

        readonly Targeting IStrategyCommon.Targeting => Targeting.Value;
        readonly AOEStrategy IStrategyCommon.AOE => AOE.Value;
    }

    public enum RotationStrategy
    {
        [Option("通常ローテ: fixed standard rotation, no FuturePlanner")]
        Automatic,
        [Option("おすすめ: オート: FuturePlanner / DTR / NearManafont / timeline-aware planning", Targets = ActionTargets.Hostile)]
        FuturePlanner,
        [Option("ウィンダス・ザ・サードウォーク（上位）: ボス別FuturePlanner / 黒魔紋 / アンプリファイア調整", Targets = ActionTargets.Hostile)]
        WindurstThirdWalk,
        [Option("手動補助: only spend Polyglot to prevent overcap; Manafont follows Manafont setting, no other abilities", Targets = ActionTargets.Hostile)]
        PolyglotOvercapOnly
    }

    public enum ScatheStrategy
    {
        [Option("おすすめ: Do not use")]
        Forbid,
        Allow
    }

    public enum ThunderStrategy
    {
        [Option("おすすめ: Automatically refresh on main target according to standard rotation", Targets = ActionTargets.Hostile)]
        Automatic,
        [Option("Don't apply")]
        Delay,
        [Option("Force refresh ASAP", Targets = ActionTargets.Hostile)]
        Force,
        [Option("Allow Thunder if an instant cast is needed, but don't try to maintain uptime", Targets = ActionTargets.Hostile)]
        InstantOnly,
        [Option("Use only for DoT refresh, not as a utility instant cast", Targets = ActionTargets.Hostile)]
        ForbidInstant
    }

    public enum LeylinesStrategy
    {
        [Option("おすすめ: Use in opener, then every time the recast is ready (keeps the even-minute raid buffs without waiting for them)")]
        EvenBurst,
        [Option("Use Leylines in opener, otherwise do not use automatically")]
        OpenerOnly,
        [Option("Do not use")]
        Delay,
        [Option("Use ASAP", Effect = 20, DefaultPriority = DefaultOGCDPriority)]
        Force,
        [Option("Use when the future planner predicts higher PPT than holding Leylines")]
        FuturePlanner
    }

    public enum SwiftcastStrategy
    {
        [Option("おすすめ: Only automatically use as a cast enabler")]
        Automatic,
        [Option("Don't use")]
        Delay,
        [Option("Use ASAP", Effect = 10, DefaultPriority = DefaultOGCDPriority)]
        Force
    }

    public enum TriplecastStrategy
    {
        [Option("おすすめ: Only automatically use for instant fire/ice swaps")]
        Automatic,
        [Option("Don't use")]
        Delay,
        [Option("Use ASAP", Effect = 15, DefaultPriority = DefaultOGCDPriority)]
        Force
    }

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("xan BLM [Custom]", "Black Mage", "Standard rotation (xan)|Casters", "xan", RotationModuleQuality.Basic, BitMask.Build(Class.BLM, Class.THM), 100).WithStrategies<Strategy>();
    }

    public int Element; // -3 (ice) <=> 3 (fire), 0 for none
    public float NextPolyglot; // max 30
    public int Hearts; // max 3
    public int Polyglot;
    public int AstralSoul; // max 6
    public bool Paradox;

    public float TriplecastLeft => Triplecast.Left;
    public (float Left, int Stacks) Triplecast;
    public bool Thunderhead;
    public bool Firestarter;
    public bool InLeyLines;
    public bool HaveLeyLines;

    public int Fire => Math.Max(0, Element);
    public int Ice => Math.Max(0, -Element);

    public int MaxPolyglot => Unlocked(TraitID.EnhancedPolyglotII) ? 3 : Unlocked(TraitID.EnhancedPolyglot) ? 2 : 1;
    public int MaxHearts => Unlocked(TraitID.UmbralHeart) ? 3 : 0;

    public int NumAOETargets;
    public int NumAOEDotTargets;
    private bool WasInAOE;
    private bool AOEToSingleBridgeActive;
    private DateTime AOEToSingleTransitionUntil;
    private DateTime SingleToAOETransitionUntil;

    private Enemy? BestAOETarget;
    private Enemy? BestThunderTarget;
    private Enemy? BestAOEThunderTarget;

    public float TargetThunderLeft;
    private MechanicHintStrategy CurrentExternalHintStrategy;
    private DateTime NoPriorityTargetSince;
    private TimelineContext CachedTimelineContext;
    private DateTime CachedTimelineContextAt;
    private MechanicHintStrategy CachedTimelineContextStrategy;
    private bool CachedTimelineContextOpenerScript;
    private bool Standard57ScriptHidesUpcomingLoss;
    private bool Standard57ScriptStartedClear = true;
    private DateTime NextExternalHintTestPushAt;
    private DateTime CachedDyingAddsCheckAt;
    private bool CachedDyingAddsHold;

    public const int AOEBreakpoint = BLMTuning.AOEBreakpoint;
    public const int FreezeBreakpoint = BLMTuning.FreezeBreakpoint;

    private static class BLMGameConstants
    {
        public const int MaxMP = 10000;
        public const float PolyglotInterval = 30;
        // The real Astral Fire / Umbral Ice timer (15s, refreshed by every element spell) is deliberately NOT modelled:
        // the rotation never idles long enough inside a phase to drop it, and pricing it made the search buy pointless
        // refreshes. float.MaxValue makes the whole element-timer path inert by construction - PlannerState.ElementTimer,
        // RefreshPlannerElementTimer, the ElementTimer terms in both terminal values, the ElementTimer omission from
        // SameState, and ElementTimerLeft all reduce to no-ops. The plumbing is kept because the harness self-test
        // addresses those fields by name; do not "fix" it by dropping in the real 15s timer without measuring that
        // change on its own.
        public const float ElementTimer = float.MaxValue;
        public const float TransposeRecast = 5;
        public const float ManafontRecast = 100;
        public const float ManafontPreEnhancedRecast = 120;
        public const float AmplifierRecast = 120;
        public const float LucidDreamingRecast = 60;
        public const float LucidDreamingDuration = 21;
        public const float LucidDreamingTickInterval = 3;
        public const int LucidDreamingMPTick = 550;
        public const float LeyLinesSpeedBonus = 0.15f;
        public const float LeyLinesGCDMultiplier = 0.85f;
        public const uint LeyLinesGroundOID = 0x179; // the ground object Ley Lines spawns, owned by the caster
        public const float OppositeElementCastMultiplier = 0.5f;
        public const int DespairMinMP = 800;
        public const int FlareMinMP = 800;
        public const int ScatheMP = 800;
        public const int ParadoxFireMP = 1600;
        public const int FireSpellBaseMP = 800;
        public const int Fire2MP = 1500;
        public const int Fire3MP = 2000;
        public const int FullIceMP = 9600;
        public const int Fire4Potency = 300;
        public const int DespairPotency = 350;
        public const int FlareStarPotency = 500;
        public const int Blizzard4Potency = 300;
        public const int Blizzard2Potency = 80;
        public const int HighBlizzard2Potency = 100;
        public const int ParadoxPotency = 540;
        public const int XenoglossyPotency = 890;
        public const int FoulPotency = 600;
        public const int HighThunderInitialPotency = 150;
        public const int HighThunderDotPotency = 60;
        public const float HighThunderDuration = 30;
        public const int Thunder3InitialPotency = 120;
        public const int Thunder3DotPotency = 50;
        public const float Thunder3Duration = 27;
        public const int Thunder1InitialPotency = 100;
        public const int Thunder1DotPotency = 45;
        public const float Thunder1Duration = 24;
        public const int Thunder2InitialPotency = 60;
        public const int Thunder2DotPotency = 30;
        public const float Thunder2Duration = 18;
        public const int Thunder4InitialPotency = 80;
        public const int Thunder4DotPotency = 35;
        public const float Thunder4Duration = 21;
        public const int HighThunder2InitialPotency = 100;
        public const int HighThunder2DotPotency = 40;
        public const float HighThunder2Duration = 24;
        public const int FreezePotency = 120;
        public const int FlarePotency = 240;
        public const float FoulFalloffMultiplier = 0.75f;
        public const float FlareFalloffMultiplier = 0.7f;
        public const float FlareStarFalloffMultiplier = 0.35f;
        public const float FireMultiplierAF3 = 1.8f;
        public const float FireMultiplierAF1 = 1.4f;
        public const float FireMultiplierAF2 = 1.6f;
    }

    private static class BLMTuning
    {
        public const int AOEBreakpoint = 2;
        public const int FreezeBreakpoint = 3;
        public const float ThunderRefreshWindow = 3;
        public const float PlannerMovementThunderRefreshWindow = 9;
        public const float ThunderMinTargetLife = 21;
        public const float AOEThunderMinTargetLife = 12;
        public const float ThunderLowHPNoRefreshRatio = 0.05f;
        public const float LeyLinesMinTargetLife = 18;
        public const float LeyLinesLowHPHoldRatio = 0.08f;
        public const float TriplecastMinTargetLife = 12;
        public const float TriplecastLowHPHoldRatio = 0.05f;
        public const float AOEToSingleTransitionWindow = 8;
        public const float ElementTimerSafetyBuffer = 0.5f;
        public const float DefaultTargetRange = 25;
        public const float SplashRadius = 5;
        public const int EnemyLifeCacheSize = 100;
        public const float EnemyLifeWarmupSeconds = 2;
        public const float EnemyLifeRatioResetTolerance = 0.05f;
        public const float EnemyLifeSampleMinSeconds = 1;
        public const float EnemyLifeStableDelta = 0.001f;
        public const float EnemyLifeStableToleranceMin = 0.0005f;
        public const float EnemyLifeStableToleranceScale = 0.35f;
        public const float EnemyLifeDrainSmoothing = 0.35f;
        public const float EnemyLifeHealResetDelta = -0.02f;
        public const float EnemyLifeDecayOnHeal = 0.5f;
        public const float EnemyLifeMinDrain = 0.0005f;
        public const float EnemyLifeMaxSeconds = 1800;
        public const float PhantomActionCombatWarmup = 10;
        public const float ManualControlGapSeconds = 2;
        public const float HandoffWindowSeconds = 6;
        public const float PlannerLockWindowSeconds = 20;
        public const float RecentSpellWindowSeconds = 4;
        public const float RecentlySpentPolyglotLockoutSeconds = 12;
        public const float ForcedMoveSoonSeconds = 5;
        public const float LeyLinesUnsafeSoonSeconds = 8;
        public const float LeyLinesMinUsefulUptime = 12;
        public const float LeyLinesForcedMoveHoldWindow = 6;
        public const float DowntimeSoonSeconds = 15;
        public const float ShortTargetLossTransposeGrace = 2.5f;
        public const float ShortTargetReturnTransposeSuppress = 8.0f;
        public const float OpenerOnlyLeyLinesWindow = 20;
        public const float Standard57OpenerWindow = 60;
        public const float NearManafontReadyMin = 5.5f;
        public const float NearManafontReadyMax = 8.5f;
        public const float NearManafontSecondFillerReadyMin = 6.5f;
        public const float NearManafontMinDowntime = 12;
        public const int NearManafontHoldMaxFillers = 3; // instant fillers counted toward holding AF for Manafont (see NearManafontHoldWindow)
        public const float NearManafontHoldMargin = 0.1f; // Manafont must come off cooldown this long before the GCD slot after the last filler
        public const float RaidBuffWindowSeconds = 15;
        public const float BurstWarmupSeconds = 10;
        public const float EvenBurstRecoveryReleaseLeadSeconds = 5;
        public const float EvenBurstRecoveryRecastSafetySeconds = 5;
        public const float EvenBurstRecoveryTargetStableSeconds = 1.5f;
        public const float PlannerOGCDMinDowntime = 1;
        public const float ExternalHintTestPushInterval = 1.0f;
        public const float Standard57ManafontXenoWaitTimeout = 0.8f;
        public const float ManafontBlockedLogInterval = 1.0f;
        public const float PostCombatUmbralSoulDelay = 3.0f;
        public const float MovementKeyGraceSeconds = 0.12f;
        // Disengage forecast: how early a predicted forced move counts as "move soon" for the cast enabler, and how long that move has
        // to last before it is worth a Swiftcast or Triplecast charge. A step shorter than this does not cost a GCD.
        public const float ForecastMoveEnablerLead = 0.6f;
        public const float ForecastMoveEnablerMinMove = 0.8f;
        public const float MovementCastEnablerRetryLockoutSeconds = 0.25f;
    }

    private static class BLMPlannerTuning
    {
        public const float PlannerWeaveElapsed = 0.6f;
        public const float CasterTax = 0.1f;
        public const float PlanReuseSeconds = 2.5f; // a reused plan is re-searched at least once per GCD even if nothing observable changed
        public const float PlanReuseTimeQuantumSeconds = 0.5f; // cooldown-timer bucket width in the plan reuse key
        public const int STPlannerMaxDepth = 40; // enough GCDs and weaves to carry every candidate to the 45s horizon
        public const int STPlannerBeamWidth = 16;
        public const float STPlannerHorizonSeconds = 45;
        public const float STPlannerMinUsefulTimeSeconds = 20;
        public const int AOEPlannerMaxDepth = 32;
        public const int AOEPlannerBeamWidth = 12;
        public const float AOEPlannerHorizonSeconds = 35;
        public const float AOEPlannerMinUsefulTimeSeconds = 12;
        public const float DTRRouteWinMargin = 20;
        public const float LeyLinesUseNowWinMargin = 20;
        public const float PlannerEvaluationMinDuration = 20;
        public const float AOEPlannerEvaluationMinDuration = 10;
        public const float PlannerCacheLifetimeSeconds = 0.05f;
        public const float PlannerCacheTimeQuantumSeconds = 0.05f;
        public const float PlannerCachePreciseGCDWindowSeconds = 0.75f;
        public const float PlannerCachePreciseLifetimeSeconds = 0.05f;
    }

    // Per-frame constants read inside the beam search. Built once per frame (and again if the level, spell speed,
    // haste or Ley Lines state changes within it) so the expanded states never touch action definitions, trait
    // lookups or player stats. Everything here is a pure function of the frame, never of a planner state.
    private struct PlannerEnv
    {
        public DateTime Frame;
        public int Level;
        public int SpellSpeed;
        public int Haste;
        public bool InLeyLines;
        public int MaxHearts;
        public int MaxPolyglot;
        public bool HasParadox;
        public bool HasFlareStar;
        public bool HasDespair;
        public bool HasFire4;
        public bool HasFire3;
        public bool HasFire1;
        public bool HasFlare;
        public bool HasBlizzard4;
        public bool HasBlizzard3;
        public bool HasBlizzard1;
        public bool HasFreeze;
        public bool HasXenoglossy;
        public bool HasFoul;
        public bool HasAmplifier;
        public bool HasTranspose;
        public bool HasAnyThunder;
        public bool HighLevelPlanner;
        public bool FoulInstant;
        public bool DespairInstant;
        public AID BestThunder;
        public AID BestAOEThunder;
        public float Enochian;
        public float ManafontRecast;
        public float GCD;
        public float GCDLength;
        public float ContinuousInstantLeft;
        public bool CanWeaveAmplifier;
        public bool MovementPriority;
        public float GCDNormal; // recast of a 2.5s spell outside Circle of Power
        public float GCDLeyLines; // recast of a 2.5s spell inside Circle of Power
        public float FillerPPS; // steady-state single-target filler rate: Fire IV under AF3 per GCD
        public float FillerGCDPotency; // one filler GCD
        public float PolyglotValue; // Xenoglossy minus the filler GCD it displaces
        public float AstralSoulValue; // one sixth of a Flare Star
        public float FirestarterValue; // instant Fire III: the hardcast time saved plus the 2000 MP kept
        public float ThunderTickPPS; // best Thunder tick potency per second (single target, Enochian applied)
        public float AOEThunderTickPPS; // best AoE Thunder tick potency per second, per target (no Enochian: the AoE planner scores in raw potency)
    }

    private const int PlannerCastSlots = 13;
    private PlannerEnv _plannerEnv;
    private readonly float[] PlannerCastTimes = new float[PlannerCastSlots * 4]; // [slot * 4 + leyLines * 2 + halved]
    private readonly bool[] PlannerFireAspect = new bool[PlannerCastSlots];
    private readonly bool[] PlannerIceAspect = new bool[PlannerCastSlots];

    private ref readonly PlannerEnv Env
    {
        get
        {
            EnsurePlannerEnv();
            return ref _plannerEnv;
        }
    }

    private static int PlannerCastSlot(AID aid) => aid switch
    {
        AID.Fire1 => 0,
        AID.Fire2 or AID.HighFire2 => 1,
        AID.Fire3 => 2,
        AID.Fire4 => 3,
        AID.Despair => 4,
        AID.Flare => 5,
        AID.FlareStar => 6,
        AID.Blizzard1 => 7,
        AID.Blizzard2 or AID.HighBlizzard2 => 8,
        AID.Blizzard3 => 9,
        AID.Blizzard4 => 10,
        AID.Freeze => 11,
        AID.Foul => 12,
        _ => -1
    };

    private static bool IsFireSpell(AID aid) => aid is AID.Fire1 or AID.Fire2 or AID.HighFire2 or AID.Fire3 or AID.Fire4 or AID.Despair or AID.Flare or AID.FlareStar;
    private static bool IsIceSpell(AID aid) => aid is AID.Blizzard1 or AID.Blizzard2 or AID.HighBlizzard2 or AID.Blizzard3 or AID.Blizzard4 or AID.Freeze or AID.UmbralSoul;

    private static readonly AID[] PlannerCastSlotActions = [AID.Fire1, AID.Fire2, AID.Fire3, AID.Fire4, AID.Despair, AID.Flare, AID.FlareStar, AID.Blizzard1, AID.Blizzard2, AID.Blizzard3, AID.Blizzard4, AID.Freeze, AID.Foul];

    private void EnsurePlannerEnv()
    {
        var stats = World.Client.PlayerStats;
        ref var env = ref _plannerEnv;
        if (env.Frame == World.CurrentTime && env.Level == Player.Level && env.SpellSpeed == stats.SpellSpeed && env.Haste == stats.Haste && env.InLeyLines == InLeyLines)
            return;

        env.Frame = World.CurrentTime;
        env.Level = Player.Level;
        env.SpellSpeed = stats.SpellSpeed;
        env.Haste = stats.Haste;
        env.InLeyLines = InLeyLines;
        env.MaxHearts = MaxHearts;
        env.MaxPolyglot = MaxPolyglot;
        env.HasParadox = Unlocked(AID.Paradox);
        env.HasFlareStar = Unlocked(AID.FlareStar);
        env.HasDespair = Unlocked(AID.Despair);
        env.HasFire4 = Unlocked(AID.Fire4);
        env.HasFire3 = Unlocked(AID.Fire3);
        env.HasFire1 = Unlocked(AID.Fire1);
        env.HasFlare = Unlocked(AID.Flare);
        env.HasBlizzard4 = Unlocked(AID.Blizzard4);
        env.HasBlizzard3 = Unlocked(AID.Blizzard3);
        env.HasBlizzard1 = Unlocked(AID.Blizzard1);
        env.HasFreeze = Unlocked(AID.Freeze);
        env.HasXenoglossy = Unlocked(AID.Xenoglossy);
        env.HasFoul = Unlocked(AID.Foul);
        env.HasAmplifier = Unlocked(AID.Amplifier);
        env.HasTranspose = Unlocked(AID.Transpose);
        env.HasAnyThunder = Unlocked(AID.HighThunder) || Unlocked(AID.Thunder3) || Unlocked(AID.Thunder1);
        env.HighLevelPlanner = CanUseHighLevelPlanner();
        env.FoulInstant = Unlocked(TraitID.EnhancedFoul);
        env.DespairInstant = Unlocked(TraitID.EnhancedAstralFire);
        env.BestThunder = BestPlannerThunder();
        env.BestAOEThunder = BestAOEThunder();
        // Enochian damage ladder straight from the trait sheet: Enochian +5% (L56), Enhanced Enochian +10% (L70),
        // II +15% (L78), III +22% (L86), IV +27% (L96). The two low rungs only matter below level 78; the planner
        // itself needs Xenoglossy (L80), so they are here for correctness rather than for any reachable decision.
        env.Enochian = Unlocked(TraitID.EnhancedEnochianIV) ? 1.27f : Unlocked(TraitID.EnhancedEnochian3) ? 1.22f : Unlocked(TraitID.EnhancedEnochian2) ? 1.15f : Unlocked(TraitID.EnhancedEnochian1) ? 1.10f : Unlocked(TraitID.Enochian) ? 1.05f : 1f;
        env.ManafontRecast = CurrentManafontRecast;
        env.GCD = GCD;
        env.GCDLength = GCDLength;
        env.ContinuousInstantLeft = PlannerContinuousInstantCastLeft;
        env.CanWeaveAmplifier = env.HasAmplifier && CanWeave(AID.Amplifier);
        env.MovementPriority = MovementSkillPriorityRequested();
        env.GCDNormal = Math.Max(1.5f, ApplyLeyLinesSpeed(2.5f, 0));
        env.GCDLeyLines = Math.Max(1.5f, ApplyLeyLinesSpeed(2.5f, 1));
        env.FillerGCDPotency = BLMGameConstants.Fire4Potency * BLMGameConstants.FireMultiplierAF3 * env.Enochian;
        // The steady-state average sits below the Fire IV rate (ice phase, Fire III, Despair), so the filler credit for
        // time a route did not simulate must not exceed what a real route earns in it.
        env.FillerPPS = env.FillerGCDPotency / env.GCDNormal * 0.92f;
        env.PolyglotValue = Math.Max(0, BLMGameConstants.XenoglossyPotency * env.Enochian - env.FillerGCDPotency);
        env.AstralSoulValue = env.HasFlareStar ? BLMGameConstants.FlareStarPotency * BLMGameConstants.FireMultiplierAF3 * env.Enochian / 6 : 0;
        // Fire III out of UI3 is already a halved cast and fire spells cost nothing under UI, so a held Firestarter is
        // worth only the movement instant it provides plus the second it saves on a Transpose-line hardcast.
        env.FirestarterValue = env.FillerPPS * 0.6f;
        env.ThunderTickPPS = ThunderDataFor(env.BestThunder).Dot * env.Enochian / BLMGameConstants.LucidDreamingTickInterval;
        // AOEPlannerPotency / AOEFillerPPS deliberately leave Enochian out (2026-09-20 Fix 4), so the AoE tick rate must too.
        env.AOEThunderTickPPS = ThunderDataFor(env.BestAOEThunder).Dot / BLMGameConstants.LucidDreamingTickInterval;

        for (var slot = 0; slot < PlannerCastSlots; ++slot)
        {
            var def = ActionDefinitions.Instance.Spell(PlannerCastSlotActions[slot]);
            var castTime = def?.CastTime ?? 0;
            // Aspects come from a static table so the planner does not depend on ActionDefinition.Aspect (restored 2026-09-19; was a None stub).
            PlannerFireAspect[slot] = IsFireSpell(PlannerCastSlotActions[slot]);
            PlannerIceAspect[slot] = IsIceSpell(PlannerCastSlotActions[slot]);
            for (var leyLines = 0; leyLines < 2; ++leyLines)
                for (var halved = 0; halved < 2; ++halved)
                    PlannerCastTimes[slot * 4 + leyLines * 2 + halved] = castTime <= 0 ? 0 : ApplyLeyLinesSpeed(halved == 1 ? castTime * BLMGameConstants.OppositeElementCastMultiplier : castTime, leyLines);
        }
    }

    private static class BLMPlannerWeights
    {
        // Route-kind bonuses are zero: a route has to win on modelled potency (including the resources it leaves behind),
        // not on being a DTR, an Amplifier or a Manafont route by name.
        public const float ReserveInstantScore = 0;
        public const float DTRTransposeScore = 0;
        public const float AmplifierScore = 0;
        public const float AOEAmplifierScoreMin = 0;
        public const float AOEAmplifierPolyglotScale = 0;
        public const float LucidDreamingScore = 0;
        public const float ManafontScore = 0;
        public const float PolyglotOvercapPenalty = 650;
        // Potency per second charged while Polyglot sits at its cap: a full gauge has no buffer against an unplanned target
        // loss, Manafont or Amplifier tick (2026-09-19 combat matrix: holding 3 stacks through a fire phase raised overcaps 222->601).
        public const float PolyglotCapHoldPenaltyPerSecond = 8;
        public const float STShortDowntimeThreshold = 4;
        public const float STMediumDowntimeThreshold = 10;
        public const float STShortDowntimeCarryScale = 0.15f;
        public const float STMediumDowntimeCarryScale = 0.45f;
        public const float AOEShortDowntimeCarryScale = 0.1f;
        public const float AOEMediumDowntimeCarryScale = 0.4f;
        public const float CarryScaleFull = 1;
        // Astral Soul, Polyglot and Firestarter terminal values are no longer tuned by hand: they are derived per frame in
        // PlannerEnv (AstralSoulValue, PolyglotValue, FirestarterValue) from the same potency model the search scores with.
        public const float AOEPolyglotTerminalScale = 0.28f;
        public const float ParadoxTerminalValue = 100; // a held marker is one free instant later; its potency is scored when it is cast
        public const float InstantBudgetTerminalValue = 75;
        public const float ElementTimerTerminalCap = 12;
        public const float ElementTimerTerminalValue = 8;
        public const float AOEElementTimerTerminalValue = 6;
        public const float ThunderTimerTerminalCap = 15;
        // Leftover MP only postpones the next (short) ice phase, so a full bar is worth well under one filler GCD; a large
        // value here makes the search farm Blizzard III refills and Transpose loops instead of finishing fire phases.
        public const float MPTerminalValue = 400;
        public const float FireElementTerminalValue = 90;
        public const float IceElementTerminalValue = 45;
        // Ley Lines time is priced as the filler it buys (LeyLinesLeft * FillerPPS * haste), not as a flat constant.
        public const float AmplifierCooldownProgressValue = 70;
        public const float ReadyAmplifierPenalty = 60;
        public const float AOEReadyAmplifierPenaltyScale = 0.10f;
        public const float RaidBuffPolyglotValue = 9;
        public const float RaidBuffAstralSoulValue = 4;
        public const float UpcomingRaidBuffPolyglotValue = 8;
        public const float DTRCompleteValue = 0;
        public const float DTRStartedValue = 0;
        public const float DowntimePenaltyThreshold = 6;
        public const float FireDowntimePenalty = 120;
        public const float IceDowntimePenalty = 30;
        public const float AOEHeartValue = 35;
        public const float AOERaidBuffMultiplier = 1.05f;
        public const float AOERaidBuffPolyglotMultiplier = 0.012f;
        public const float AOEUpcomingRaidBuffPolyglotMultiplier = 0.01f;
    }

    private const uint WindurstThirdWalkCFCID = 1117;
    private const uint WindurstShantottoNameID = 14778;
    private const uint WindurstAlexanderNameID = 14529;
    private const uint WindurstPromathiaNameID = 14779;
    private const uint WindurstHollowKingNameID = 14729;
    private const float WindurstBurstHoldLead = 10.0f;

    private readonly record struct WindurstBurstWindow(float Time, float Before, float After);

    // Existing Windurst top-log raid-buff anchors shared with RPR; state-based rotation remains the fallback outside each window.
    private static readonly WindurstBurstWindow[] WindurstShantottoBurstWindows =
    [
        new(6.355f, 1.5f, 6.0f),
        new(128.072f, 2.0f, 6.0f),
        new(246.771f, 2.5f, 7.0f)
    ];

    private static readonly WindurstBurstWindow[] WindurstAlexanderBurstWindows =
    [
        new(7.170f, 1.5f, 6.0f),
        new(126.910f, 2.0f, 7.0f),
        new(225.698f, 2.5f, 7.0f)
    ];

    private static readonly WindurstBurstWindow[] WindurstPromathiaBurstWindows =
    [
        new(10.759f, 2.0f, 7.0f),
        new(101.586f, 2.5f, 7.0f),
        new(211.897f, 2.5f, 7.0f),
        new(323.602f, 2.5f, 7.0f)
    ];

    private static readonly WindurstBurstWindow[] WindurstHollowKingBurstWindows =
    [
        new(7.798f, 1.5f, 6.0f),
        new(131.173f, 2.0f, 7.0f),
        new(248.859f, 2.5f, 7.0f),
        new(369.886f, 2.5f, 8.0f)
    ];

    private static readonly WindurstBurstWindow[] NoWindurstBurstWindows = [];

    private bool ActiveTriplecast => Triplecast.Stacks > 0 && TriplecastLeft > GCD;
    private bool ActiveSwiftcast => SwiftcastLeft > GCD;
    private bool ActiveInstantCast => ActiveTriplecast || ActiveSwiftcast;
    private float InstantCastLeft => Math.Max(ActiveTriplecast ? TriplecastLeft : 0, ActiveSwiftcast ? SwiftcastLeft : 0);
    private bool MovementRequested => MovementOverride.Instance?.IsMoveRequested() == true;

    protected override float GetCastTime(AID aid)
    {
        if (ActiveInstantCast)
            return 0;

        if (aid == AID.Despair && Unlocked(TraitID.EnhancedAstralFire))
            return 0;

        var spell = ActionDefinitions.Instance.Spell(aid);
        if (spell == null)
            return base.GetCastTime(aid);

        if (aid == AID.Fire3 && Firestarter
            || aid == AID.Foul && Unlocked(TraitID.EnhancedFoul)
            || IsThunderAction(aid) && Thunderhead)
            return 0;

        var castTime = base.GetCastTime(aid);
        if (castTime == 0)
            return 0;

        if (Element == -3 && IsFireSpell(aid) || Element == 3 && IsIceSpell(aid))
            castTime *= BLMGameConstants.OppositeElementCastMultiplier;

        return castTime;
    }

    private int GetManaCost(AID aid)
    {
        int adjustFire(int cost) => Ice > 0
            ? 0
            : Fire > 0 && Hearts == 0
                ? cost * 2
                : cost;

        return aid switch
        {
            AID.Despair or AID.Flare => BLMGameConstants.DespairMinMP, // min cost
            AID.Paradox => Fire > 0 ? BLMGameConstants.ParadoxFireMP : 0, // unaspected, unaffected by elemental gauge
            AID.Scathe => BLMGameConstants.ScatheMP, // unaspected
            AID.Fire3 => Firestarter ? 0 : adjustFire(BLMGameConstants.Fire3MP),
            AID.Fire1 or AID.Fire4 => adjustFire(BLMGameConstants.FireSpellBaseMP),
            AID.Fire2 or AID.HighFire2 => adjustFire(BLMGameConstants.Fire2MP),
            _ => 0
        };
    }

    private readonly float[] EnemyDotTimers = new float[BLMTuning.EnemyLifeCacheSize];
    private readonly ulong[] ThunderLifeActorIDs = new ulong[BLMTuning.EnemyLifeCacheSize];
    private readonly float[] ThunderLifeSampleTimes = new float[BLMTuning.EnemyLifeCacheSize];
    private readonly float[] ThunderLifeSampleRatios = new float[BLMTuning.EnemyLifeCacheSize];
    private readonly float[] ThunderLifeDrainPerSecond = new float[BLMTuning.EnemyLifeCacheSize];
    private readonly int[] ThunderLifeStableSamples = new int[BLMTuning.EnemyLifeCacheSize];

    private float CalculateDotTimer(Actor? t) => t == null || !ThunderTargetWorthDot(t, BLMTuning.AOEThunderMinTargetLife) ? float.MaxValue : Utils.MaxAll(
        StatusDetails(t, SID.Thunder, Player.InstanceID, BLMGameConstants.Thunder1Duration).Left,
        StatusDetails(t, SID.ThunderII, Player.InstanceID, BLMGameConstants.Thunder2Duration).Left,
        StatusDetails(t, SID.ThunderIII, Player.InstanceID, BLMGameConstants.Thunder3Duration).Left,
        StatusDetails(t, SID.ThunderIV, Player.InstanceID, BLMGameConstants.Thunder4Duration).Left,
        StatusDetails(t, SID.HighThunder, Player.InstanceID, BLMGameConstants.HighThunderDuration).Left,
        StatusDetails(t, SID.HighThunderII, Player.InstanceID, BLMGameConstants.HighThunder2Duration).Left
    );

    private float GetTargetThunderLeft(Actor? t)
    {
        if (t == null)
            return float.MaxValue;

        var index = t.CharacterSpawnIndex;
        return index >= 0 && index < EnemyDotTimers.Length ? EnemyDotTimers[index] : float.MaxValue;
    }

    private float GetSingleTargetThunderLeft(Actor? t)
        => ThunderTargetWorthDot(t) ? GetTargetThunderLeft(t) : float.MaxValue;

    private float GetAOETargetThunderLeft(Actor? t)
        => ThunderTargetWorthDot(t, BLMTuning.AOEThunderMinTargetLife) ? GetTargetThunderLeft(t) : float.MaxValue;

    private bool DotExpiring(float timer) => timer <= BLMTuning.ThunderRefreshWindow;

    private void UpdateThunderLifeEstimate(Actor? target)
    {
        if (target == null || target.CharacterSpawnIndex < 0 || target.CharacterSpawnIndex >= ThunderLifeActorIDs.Length)
            return;

        var index = target.CharacterSpawnIndex;
        if (target.PendingDead || target.HPMP.MaxHP == 0)
        {
            ResetThunderLifeEstimate(index);
            return;
        }

        var ratio = Math.Clamp(target.PendingHPRatio, 0, 1);
        if (ThunderLifeActorIDs[index] != target.InstanceID || CombatTimer < BLMTuning.EnemyLifeWarmupSeconds || ratio > ThunderLifeSampleRatios[index] + BLMTuning.EnemyLifeRatioResetTolerance)
        {
            ThunderLifeActorIDs[index] = target.InstanceID;
            ThunderLifeSampleTimes[index] = CombatTimer;
            ThunderLifeSampleRatios[index] = ratio;
            ThunderLifeDrainPerSecond[index] = 0;
            ThunderLifeStableSamples[index] = 0;
            return;
        }

        var dt = CombatTimer - ThunderLifeSampleTimes[index];
        if (dt < BLMTuning.EnemyLifeSampleMinSeconds)
            return;

        var delta = ThunderLifeSampleRatios[index] - ratio;
        if (delta > BLMTuning.EnemyLifeStableDelta)
        {
            var observedDrain = delta / dt;
            var previousDrain = ThunderLifeDrainPerSecond[index];
            if (previousDrain > 0 && Math.Abs(observedDrain - previousDrain) <= Math.Max(BLMTuning.EnemyLifeStableToleranceMin, previousDrain * BLMTuning.EnemyLifeStableToleranceScale))
                ++ThunderLifeStableSamples[index];
            else
                ThunderLifeStableSamples[index] = 0;

            ThunderLifeDrainPerSecond[index] = previousDrain > 0
                ? previousDrain + (observedDrain - previousDrain) * BLMTuning.EnemyLifeDrainSmoothing
                : observedDrain;
        }
        else if (delta < BLMTuning.EnemyLifeHealResetDelta)
        {
            ThunderLifeDrainPerSecond[index] *= BLMTuning.EnemyLifeDecayOnHeal;
            ThunderLifeStableSamples[index] = 0;
        }
        else
        {
            ThunderLifeStableSamples[index] = 0;
        }

        ThunderLifeSampleTimes[index] = CombatTimer;
        ThunderLifeSampleRatios[index] = ratio;
    }

    private void ResetThunderLifeEstimate(int index)
    {
        ThunderLifeActorIDs[index] = 0;
        ThunderLifeSampleTimes[index] = 0;
        ThunderLifeSampleRatios[index] = 0;
        ThunderLifeDrainPerSecond[index] = 0;
        ThunderLifeStableSamples[index] = 0;
    }

    private float EstimatedThunderTargetLife(Actor target)
    {
        if (target.PendingDead)
            return 0;

        if (target.CharacterSpawnIndex < 0 || target.CharacterSpawnIndex >= ThunderLifeActorIDs.Length || ThunderLifeActorIDs[target.CharacterSpawnIndex] != target.InstanceID)
            return float.MaxValue;

        var drain = ThunderLifeDrainPerSecond[target.CharacterSpawnIndex];
        return drain > BLMTuning.EnemyLifeMinDrain && ThunderLifeStableSamples[target.CharacterSpawnIndex] > 0
            ? Math.Clamp(Math.Clamp(target.PendingHPRatio, 0, 1) / drain, 0, BLMTuning.EnemyLifeMaxSeconds)
            : float.MaxValue;
    }

    private bool ThunderTargetWorthDot(Actor? target, float minLifeSeconds = BLMTuning.ThunderMinTargetLife)
    {
        if (target == null)
            return false;

        if (target.PendingDead || target.HPMP.CurHP == 0 || target.HPMP.MaxHP == 0)
            return false;

        // FightRemaining (value-of-information experiment): a DoT that cannot tick long enough before the fight ends is not worth a GCD
        if (Voi.GateLife > 0 && VoiEndIn(Voi.GateStat) <= Voi.GateLife * (minLifeSeconds / BLMTuning.ThunderMinTargetLife))
            return false;

        var estimatedLife = EstimatedThunderTargetLife(target);
        if (estimatedLife != float.MaxValue)
            return estimatedLife > minLifeSeconds;

        return target.PendingHPRatio > BLMTuning.ThunderLowHPNoRefreshRatio;
    }

    private bool TargetLikelySurvivesForCooldown(Actor? target, float minLifeSeconds, float lowHPRatio)
    {
        if (target == null || target.PendingDead || target.HPMP.CurHP == 0 || target.HPMP.MaxHP == 0)
            return false;

        var estimatedLife = EstimatedThunderTargetLife(target);
        if (estimatedLife != float.MaxValue)
            return estimatedLife > minLifeSeconds;

        return target.PendingHPRatio > lowHPRatio;
    }

    private bool TargetLikelySurvivesLeyLines(Actor? target)
        => TargetLikelySurvivesForCooldown(target, BLMTuning.LeyLinesMinTargetLife, BLMTuning.LeyLinesLowHPHoldRatio);

    private bool TargetLikelySurvivesTriplecast(Actor? target)
        => TargetLikelySurvivesForCooldown(target, BLMTuning.TriplecastMinTargetLife, BLMTuning.TriplecastLowHPHoldRatio);

    private bool AnyRelevantTargetLikelySurvivesForCooldown(float minLifeSeconds, float lowHPRatio)
    {
        foreach (var enemy in Hints.PriorityTargetsSpan)
        {
            var actor = enemy.Actor;
            if (actor.HPMP.CurHP <= 0 || actor.PendingDead)
                continue;

            if (TargetLikelySurvivesForCooldown(actor, minLifeSeconds, lowHPRatio))
                return true;
        }

        return false;
    }

    private bool ShouldHoldLeylinesForDyingTarget(Enemy? primaryTarget)
    {
        if (!Player.InCombat)
            return false;

        if (UseAOERotation())
            return !AnyRelevantTargetLikelySurvivesForCooldown(BLMTuning.LeyLinesMinTargetLife, BLMTuning.LeyLinesLowHPHoldRatio);

        return !TargetLikelySurvivesLeyLines(primaryTarget?.Actor);
    }

    private bool ShouldHoldTriplecastForDyingTarget(Enemy? primaryTarget)
    {
        if (!Player.InCombat)
            return false;

        if (UseAOERotation())
            return !AnyRelevantTargetLikelySurvivesForCooldown(BLMTuning.TriplecastMinTargetLife, BLMTuning.TriplecastLowHPHoldRatio);

        return !TargetLikelySurvivesTriplecast(primaryTarget?.Actor);
    }

    private bool CanUseTriplecastForTarget(Enemy? primaryTarget)
        => !ShouldHoldTriplecastForDyingTarget(primaryTarget);

    private uint WindurstEncounterNameID()
    {
        var info = Bossmods.ActiveModule?.Info;
        return info?.GroupType == BossModuleInfo.GroupType.CFC && info.GroupID == WindurstThirdWalkCFCID
            ? info.NameID
            : 0;
    }

    private bool IsWindurstThirdWalk()
        => World.CurrentCFCID == WindurstThirdWalkCFCID
        || Bossmods.ActiveModule?.Info?.GroupType == BossModuleInfo.GroupType.CFC && Bossmods.ActiveModule.Info.GroupID == WindurstThirdWalkCFCID;

    private bool ShouldUseWindurstThirdWalkProfile(in Strategy strategy)
        => strategy.Rotation.Value == RotationStrategy.WindurstThirdWalk
        && IsWindurstThirdWalk()
        && WindurstEncounterNameID() != 0
        && Player.InCombat;

    private WindurstBurstWindow[] WindurstBurstWindows()
        => WindurstEncounterNameID() switch
        {
            WindurstShantottoNameID => WindurstShantottoBurstWindows,
            WindurstAlexanderNameID => WindurstAlexanderBurstWindows,
            WindurstPromathiaNameID => WindurstPromathiaBurstWindows,
            WindurstHollowKingNameID => WindurstHollowKingBurstWindows,
            _ => NoWindurstBurstWindows
        };

    private bool WindurstBurstWindowOpen(in Strategy strategy)
    {
        if (!ShouldUseWindurstThirdWalkProfile(strategy))
            return false;

        foreach (var window in WindurstBurstWindows())
            if (CombatTimer >= window.Time - window.Before && CombatTimer <= window.Time + window.After)
                return true;

        return false;
    }

    private bool ShouldHoldForWindurstBurst(in Strategy strategy, float readyIn)
    {
        if (!ShouldUseWindurstThirdWalkProfile(strategy))
            return false;

        foreach (var window in WindurstBurstWindows())
        {
            var startsIn = window.Time - window.Before - CombatTimer;
            if (startsIn > WindurstBurstHoldLead)
                return false;

            if (startsIn > 0)
                return readyIn <= startsIn + window.Before + window.After;
        }

        return false;
    }

    private float PlannerAmplifierReadyIn(in Strategy strategy)
    {
        var readyIn = Unlocked(AID.Amplifier) ? ReadyIn(AID.Amplifier) : float.MaxValue;
        return ShouldHoldForWindurstBurst(strategy, readyIn) ? float.MaxValue : readyIn;
    }

    private bool ShouldHoldLeylinesForDyingAdds()
    {
        if (CachedDyingAddsCheckAt == World.CurrentTime)
            return CachedDyingAddsHold;

        var result = ComputeShouldHoldLeylinesForDyingAdds();
        CachedDyingAddsCheckAt = World.CurrentTime;
        CachedDyingAddsHold = result;
        return result;
    }

    private bool ComputeShouldHoldLeylinesForDyingAdds()
    {
        if (!Player.InCombat)
            return false;

        var liveCount = 0;
        var anyLikelySurvives = false;
        var anyLikelySurvivesInAOE = false;
        var useAOECenter = NumAOETargets >= AOEBreakpoint && BestAOETarget != null;
        var aoeCenter = useAOECenter ? BestAOETarget!.Actor.Position : default;

        foreach (var enemy in Hints.PriorityTargetsSpan)
        {
            var actor = enemy.Actor;
            if (actor.HPMP.CurHP <= 0 || actor.PendingDead)
                continue;

            ++liveCount;
            var survives = TargetLikelySurvivesLeyLines(actor);
            if (survives)
                anyLikelySurvives = true;

            if (useAOECenter && survives && TargetInAOECircle(actor, aoeCenter, BLMTuning.SplashRadius))
                anyLikelySurvivesInAOE = true;
        }

        if (liveCount == 0)
            return false;

        var addPack = NumAOETargets >= AOEBreakpoint || liveCount > 1;
        if (!addPack)
            return false;

        return useAOECenter ? !anyLikelySurvivesInAOE : !anyLikelySurvives;
    }

    private bool BurstPreparationInProgress()
        => WasInCombat
        && (Element != 0
            || AstralSoul > 0
            || Polyglot > 0
            || Firestarter
            || Thunderhead
            || ReadyIn(AID.Manafont) <= BLMTuning.RaidBuffWindowSeconds * 2
            || ReadyIn(AID.Amplifier) <= BLMTuning.RaidBuffWindowSeconds * 2
            || RaidBuffsLeft > 0
            || RaidBuffsIn <= BLMTuning.RaidBuffWindowSeconds * 2);

    private bool HasEvenBurstCountdown()
        => RaidBuffsIn > 0
        && !float.IsNaN(RaidBuffsIn)
        && !float.IsInfinity(RaidBuffsIn)
        && RaidBuffsIn < float.MaxValue;

    private bool EvenBurstWindowOpen()
        => RaidBuffsLeft > 0
        || HasEvenBurstCountdown() && RaidBuffsIn <= BLMTuning.EvenBurstRecoveryReleaseLeadSeconds;

    private bool ShouldHoldEvenBurstRecoveryAction(float recast)
    {
        if (!EvenBurstRecoveryActive || EvenBurstWindowOpen())
            return false;

        return !HasEvenBurstCountdown()
            || RaidBuffsIn <= recast + BLMTuning.EvenBurstRecoveryRecastSafetySeconds;
    }

    private bool EvenBurstCooldownClusterReady(in Strategy strategy)
    {
        var amplifierReady = !Unlocked(AID.Amplifier) || ReadyIn(AID.Amplifier) <= GCD;
        var manafontReady = !ManafontAutomatic(strategy) || !Unlocked(AID.Manafont) || ReadyIn(AID.Manafont) <= GCD;
        return amplifierReady && manafontReady;
    }

    private bool IsEvenBurstRecoveryTarget(Enemy? primaryTarget)
    {
        if (primaryTarget == null || primaryTarget.Actor.PendingDead || primaryTarget.Actor.HPMP.CurHP == 0)
            return false;

        return TargetLikelySurvivesLeyLines(primaryTarget.Actor);
    }

    private void ClearEvenBurstRecovery()
    {
        EvenBurstRecoveryActive = false;
        EvenBurstRecoveryTargetAvailableSince = default;
    }

    private void UpdateEvenBurstRecovery(in Strategy strategy, Enemy? primaryTarget, bool hasPriorityTarget, bool combatEndedThisFrame, bool combatStartedThisFrame, bool burstPreparationWasActive)
    {
        var targetActor = primaryTarget?.Actor;
        var hasBurstTarget = targetActor is { PendingDead: false } && targetActor.HPMP.CurHP > 0
            || hasPriorityTarget;
        var previousTargetDied = LastBurstTargetID != 0
            && (targetActor == null || targetActor.InstanceID != LastBurstTargetID)
            && (World.Actors.Find(LastBurstTargetID) is not { } lastBurstTarget || lastBurstTarget.PendingDead || lastBurstTarget.HPMP.CurHP == 0);
        var targetLost = HadBurstTarget && !hasBurstTarget;

        // Only arm when a raid buff countdown is actually known; without one the hold would block Manafont/Amplifier across trash pulls.
        if (UseAbilities(strategy)
            && burstPreparationWasActive
            && HasEvenBurstCountdown()
            && (combatEndedThisFrame || targetLost || previousTargetDied))
        {
            EvenBurstRecoveryActive = true;
            EvenBurstRecoveryTargetAvailableSince = default;
            Standard57BurstInterruptionSuppressedUntil = World.FutureTime(BLMTuning.Standard57OpenerWindow);
            ClearPlannerLock();
            ResetStandard57OpenerState();
        }
        else if (EvenBurstRecoveryActive && combatStartedThisFrame)
        {
            // A new pull realigns burst on its own; never carry a recovery hold into it.
            ClearEvenBurstRecovery();
            ClearPlannerLock();
        }

        HadBurstTarget = hasBurstTarget;
        LastBurstTargetID = hasBurstTarget ? targetActor?.InstanceID ?? 0 : 0;

        if (!EvenBurstRecoveryActive)
        {
            if (CountdownRemaining > 0 && EvenBurstCooldownClusterReady(strategy))
                Standard57BurstInterruptionSuppressedUntil = default;
            return;
        }

        if (!UseAbilities(strategy))
        {
            ClearEvenBurstRecovery();
            Standard57BurstInterruptionSuppressedUntil = default;
            return;
        }

        if (!IsEvenBurstRecoveryTarget(primaryTarget))
        {
            EvenBurstRecoveryTargetAvailableSince = default;
            return;
        }

        if (EvenBurstRecoveryTargetAvailableSince == default)
        {
            EvenBurstRecoveryTargetAvailableSince = World.CurrentTime;
            return;
        }

        var targetStableFor = (float)(World.CurrentTime - EvenBurstRecoveryTargetAvailableSince).TotalSeconds;
        if (targetStableFor < BLMTuning.EvenBurstRecoveryTargetStableSeconds)
            return;

        if (EvenBurstWindowOpen() || !HasEvenBurstCountdown() && EvenBurstCooldownClusterReady(strategy))
        {
            ClearEvenBurstRecovery();
            if (CountdownRemaining > 0 && EvenBurstCooldownClusterReady(strategy))
                Standard57BurstInterruptionSuppressedUntil = default;
            ClearPlannerLock();
        }
    }

    // FightRemaining (value-of-information experiment): the rules below read Hints.FightRemaining only when it is Known; Unknown (and the
    // harness "blind" control, whose Upper/Remaining are float.MaxValue) reads as "the fight does not end soon" = the unmodified behaviour.
    // Each rule is separately switchable (a window of 0 turns it off).
    private static class Voi
    {
        public const float DumpWindow = 10; // Polyglot dump (XenoCap priority, hold rules skipped) when the fight ends within this many seconds; 0 = off
        public const float GateLife = 21; // Thunder refresh is not started when the fight ends sooner than this (0 = off)
        public const float AmpWindow = 20; // Amplifier is pressed (opener script and near-overcap holds ignored) when the fight ends within this many seconds (0 = off)
        public const int DumpStat = 1; // which estimate the dump / Amplifier rules read: 0 = UpperBound, 1 = RemainingSeconds, 2 = LowerBound (dumping a stack early costs almost nothing, so the point estimate beats UpperBound under noise)
        public const int GateStat = 1; // the same for the Thunder gate
    }

    // Seconds until the fight ends by the chosen statistic (0 = UpperBound, 1 = RemainingSeconds, 2 = LowerBound); float.MaxValue when unknown.
    private float VoiEndIn(int stat)
    {
        var f = Hints.FightRemaining;
        if (!f.Known)
            return float.MaxValue;
        return stat == 1 ? f.RemainingSeconds : stat == 2 ? f.LowerBound : f.UpperBound;
    }

    private bool VoiDumpNow => Voi.DumpWindow > 0 && Player.InCombat && VoiEndIn(Voi.DumpStat) <= Voi.DumpWindow;

    private bool AlmostMaxMP => MP >= Player.HPMP.MaxMP * 0.96f;
    private int MinAstralFireMP => Unlocked(AID.Despair) ? 800 : FireSpellCost;
    private int FireSpellCost => Hearts > 0 ? 800 : 1600;
    private bool RecentlyEndedCombat()
        => LastCombatEndedAt != default
        && !Player.InCombat
        && (World.CurrentTime - LastCombatEndedAt).TotalSeconds < BLMTuning.PostCombatUmbralSoulDelay;

    public enum GCDPriority
    {
        None = 0,
        InstantMove = 100,
        Standard = 500, // aka F4
        InstantWeave = 600, // if we want to use manafont/transpose ASAP (TODO: or utility actions?)
        High = 650, // anything more important than F4 filler (usually paradox, or F3 before paradox)
        DotRefresh = 700, // thunder refresh
        XenoCap = 750,
        Max = 900, // flare star
        // Everything below stays under ActionQueue.Priority.ManualGCD (High + 999) even after the InstantCastPriority offsets (<= 50),
        // so a manually pressed GCD always wins over the module.
        FuturePlan = 910,
        ResourceCap = 930,
        Opener = 948,
    }

    public enum InstantCastPriority
    {
        Despair = 0,
        Firestarter = 10,
        CastEnabler = 20,
        Paradox = 30,
        TP = 40,
        Polyglot = 50
    }

    private static GCDPriority ForMove(InstantCastPriority p) => GCDPriority.InstantMove + (int)p;

    private readonly List<AID> PlannerLockedRoute = [];
    private int PlannerLockedIndex;
    private uint PlannerLockLastAdvancedSequence;
    private DateTime PlannerLockUntil;
    private bool HaveLastRotationMode;
    private RotationStrategy LastRotationMode;
    private bool HaveLastExternalHintStrategy;
    private MechanicHintStrategy LastExternalHintStrategy;
    private uint LastObservedCastSequence;
    private uint Standard57OpenerObservedSequence;
    private bool Standard57ThunderObserved;
    private bool Standard57SwiftcastObserved;
    private bool Standard57AmplifierObserved;
    private int Standard57Fire4Count;
    private bool Standard57XenoBeforeManafontSpent;
    private bool Standard57ManafontObserved;
    private DateTime Standard57ManafontWindowEnteredAt;
    private DateTime RotationModeHandoffUntil;
    private DateTime LastExecAt;
    private DateTime LastFlareStarCastAt;
    private float ElementTimerLeft;
    private float PlannerContinuousInstantCastLeft;
    private bool InstantB3TransposeReentryActive;
    private bool InstantB3TransposeReentryUsedSwiftcast;
    private DateTime LastPolyglotSpendAt;
    private DateTime NextExternalHintTestLogAt;
    private DateTime NextManafontHandoffLogAt;
    private DateTime ManualToAutoManafontHandoffUntil;
    private bool ManualToAutoManafontHandoffPending;
    private DateTime MovementKeyRequestUntil;
    private DateTime MovementCastEnablerRetryAfter;
    private bool WasInCombat;
    private DateTime LastCombatEndedAt;
    private bool HadBurstTarget;
    private ulong LastBurstTargetID; // an id, not the Actor: a despawned target is no longer in World.Actors and counts as gone
    private bool EvenBurstRecoveryActive;
    private DateTime EvenBurstRecoveryTargetAvailableSince;
    private DateTime Standard57BurstInterruptionSuppressedUntil;
    private readonly Dictionary<PlannerCacheKey, PlannerState> STPlannerCache = [];
    private readonly Dictionary<AOEPlannerCacheKey, AOEPlannerState> AOEPlannerCache = [];
    private readonly (PlannerState State, float Score)[] STPlannerBeam = new (PlannerState, float)[BLMPlannerTuning.STPlannerBeamWidth];
    private readonly (PlannerState State, float Score)[] STPlannerNextBeam = new (PlannerState, float)[BLMPlannerTuning.STPlannerBeamWidth];
    private readonly (AOEPlannerState State, float Score)[] AOEPlannerBeam = new (AOEPlannerState, float)[BLMPlannerTuning.AOEPlannerBeamWidth];
    private readonly (AOEPlannerState State, float Score)[] AOEPlannerNextBeam = new (AOEPlannerState, float)[BLMPlannerTuning.AOEPlannerBeamWidth];
    private DateTime PlannerCacheFrameAt;
    private DateTime PlannerCacheExpiresAt;

    // Plan reuse: between two executed actions the only things that change are timers, so the route found at the start of
    // a GCD stays valid until an action lands, a discrete resource changes, or the frame context (targets, movement,
    // downtime, strategy) changes. Reusing it turns ~50 searches per GCD into one or two.
    private readonly record struct PlannerReuseKey(
        int Element, int MP, int Hearts, int Polyglot, int AstralSoul, bool Paradox, bool Firestarter, bool Thunderhead,
        int InstantBudget, int ActiveInstantBudget, AID ReservedInstant, int Targets, int DotTargets, bool DowntimeSoon, bool DowntimeNow,
        bool ForcedMoveSoon, bool Movement, bool InLeyLines, bool HaveLeyLines, bool AllowManafont, bool AllowBurst, bool AllowThunder, bool AllowTriplecast,
        int Level, RotationStrategy Rotation, uint LastCastSequence, bool UrgentThunder,
        int ManafontBucket, int TransposeBucket, int AmplifierBucket, int LucidBucket);
    private PlannerReuseKey STPlanReuseKey;
    private PlannerState STPlanReused;
    private DateTime STPlanReusedAt;
    private bool STPlanReuseValid;
    private PlannerReuseKey AOEPlanReuseKey;
    private AOEPlannerState AOEPlanReused;
    private DateTime AOEPlanReusedAt;
    private bool AOEPlanReuseValid;
    private PlannerReuseKey LeyLinesDecisionKey;
    private bool LeyLinesDecision;
    private DateTime LeyLinesDecisionAt;
    private bool LeyLinesDecisionValid;

    private PlannerReuseKey BuildPlannerReuseKey(in Strategy strategy, bool allowNewTriplecast)
    {
        var timeline = CurrentTimelineContext();
        var state = CurrentPlannerState(strategy, allowNewTriplecast);
        return new(state.Element, state.MP / 100, state.Hearts, state.Polyglot, state.AstralSoul, state.Paradox, state.Firestarter, state.Thunderhead,
            state.InstantBudget, state.ActiveInstantBudget, state.ReservedInstantAction, NumAOETargets, NumAOEDotTargets, timeline.DowntimeSoon, timeline.DowntimeNow,
            timeline.ForcedMoveSoon, MovementSkillPriorityRequested(), InLeyLines, HaveLeyLines, state.AllowManafont, state.AllowBurstActions, state.AllowThunder, allowNewTriplecast,
            Player.Level, strategy.Rotation.Value, Manager.LastCast.Data?.SourceSequence ?? 0, TargetThunderLeft <= BLMTuning.ThunderRefreshWindow,
            PlannerReuseTimeBucket(state.ManafontReadyIn), PlannerReuseTimeBucket(state.TransposeReadyIn), PlannerReuseTimeBucket(state.AmplifierReadyIn), PlannerReuseTimeBucket(state.LucidReadyIn));
    }

    // Cooldown timers move without any action landing, and a plan found while Manafont / Transpose were still on recast stays
    // cached until an action lands otherwise (under Astral Fire the MP bucket does not move either). Coarse 0.5s buckets keep the
    // reuse rate high while making sure a timer crossing the point the plan was waiting for triggers a fresh search.
    private static int PlannerReuseTimeBucket(float value)
        => float.IsNaN(value) ? int.MinValue : value >= float.MaxValue || float.IsPositiveInfinity(value) ? int.MaxValue : (int)MathF.Ceiling(Math.Max(0, value) / BLMPlannerTuning.PlanReuseTimeQuantumSeconds);

    private bool PlanReusable(bool valid, in PlannerReuseKey cached, in PlannerReuseKey current, DateTime cachedAt)
        => valid && cached == current && (World.CurrentTime - cachedAt).TotalSeconds < BLMPlannerTuning.PlanReuseSeconds && World.CurrentTime >= cachedAt;

    public override void Exec(in Strategy strategy, Enemy? primaryTarget)
    {
        if (strategy.Targeting.Value is Targeting.Auto or Targeting.AutoTryPri && !IsUsableAOECastTarget(primaryTarget))
            primaryTarget = null;
        SelectPrimaryTarget(strategy, ref primaryTarget, range: BLMTuning.DefaultTargetRange);
        var useAbilities = UseAbilities(strategy);
        var burstPreparationWasActive = BurstPreparationInProgress();
        if (!useAbilities)
            ClearManualToAutoManafontHandoff("manual-assist");

        var polyglotOvercapOnly = PolyglotOvercapOnly(strategy);
        var resumedAfterManualGap = LastExecAt != default && (World.CurrentTime - LastExecAt).TotalSeconds > BLMTuning.ManualControlGapSeconds;
        LastExecAt = World.CurrentTime;

        var gauge = World.Client.GetGauge<BlackMageGauge>();

        Element = gauge.ElementStance;
        NextPolyglot = gauge.EnochianTimer * 0.001f;
        ElementTimerLeft = Element == 0 ? 0 : BLMGameConstants.ElementTimer;
        Hearts = gauge.UmbralHearts;
        Polyglot = gauge.PolyglotStacks;
        Paradox = gauge.ParadoxActive;
        AstralSoul = gauge.AstralSoulStacks;

        Triplecast = Status(SID.Triplecast);
        PlannerContinuousInstantCastLeft = Math.Max(StatusLeft(ClassShared.SID.LostChainspell), StatusLeft(PhantomSID.OccultQuick));
        Thunderhead = Player.FindStatus(SID.Thunderhead) != null;
        Firestarter = Player.FindStatus(SID.Firestarter) != null;
        InLeyLines = Player.FindStatus(SID.CircleOfPower) != null;
        HaveLeyLines = Player.FindStatus(SID.LeyLines) != null;

        var combatEndedThisFrame = WasInCombat && !Player.InCombat;
        var combatStartedThisFrame = !WasInCombat && Player.InCombat;
        if (combatEndedThisFrame)
            LastCombatEndedAt = World.CurrentTime;

        WasInCombat = Player.InCombat;

        Array.Fill(EnemyDotTimers, float.MaxValue);
        for (var i = 0; i < Hints.Enemies.Length; ++i)
        {
            var enemyActor = Hints.Enemies[i]?.Actor;
            if (enemyActor == null)
                continue;

            UpdateThunderLifeEstimate(enemyActor);
            var index = enemyActor.CharacterSpawnIndex;
            if (index >= 0 && index < EnemyDotTimers.Length)
                EnemyDotTimers[index] = CalculateDotTimer(enemyActor);
        }

        (BestAOETarget, NumAOETargets) = SelectAOECenterTarget(strategy, primaryTarget);
        var effectiveAOE = UseAOERotation();
        if (effectiveAOE && !IsUsableAOECastTarget(primaryTarget))
            primaryTarget = BestAOETarget;
        CurrentExternalHintStrategy = strategy.MechanicHints.Value;
        UpdateMechanicForecast(CurrentExternalHintStrategy);
        UpdateStandard57ScriptHidesUpcomingLoss(strategy);
        PushExternalHintTest();
        HandleExternalHintHandoff(strategy);
        HandleManualControlHandoff(strategy, resumedAfterManualGap);
        HandleRotationModeHandoff(strategy);

        var hasPriorityTarget = !Hints.PriorityTargetsSpan.IsEmpty;
        if (hasPriorityTarget)
        {
            NoPriorityTargetSince = default;
        }
        else if (NoPriorityTargetSince == default)
        {
            NoPriorityTargetSince = World.CurrentTime;
        }

        UpdateEvenBurstRecovery(strategy, primaryTarget, hasPriorityTarget, combatEndedThisFrame, combatStartedThisFrame, burstPreparationWasActive);

        if (useAbilities)
        {
            UpdateAOEToSingleBridgeState(effectiveAOE);
            ObserveExternalCastForHandoff();
            ObserveStandard57OpenerCast();
            UpdateStandard57ManafontWindowState(strategy);
        }
        else
        {
            ClearPlannerLock();
            ResetAOEToSingleBridge();
            ResetStandard57OpenerState();
            ResetInstantB3TransposeReentry();
        }

        var dotTarget = ResolveEnemy(strategy.Thunder) ?? primaryTarget;

        if (strategy.Thunder.Value is ThunderStrategy.Force or ThunderStrategy.Delay)
        {
            BestThunderTarget = dotTarget;
            TargetThunderLeft = GetTargetThunderLeft(dotTarget?.Actor);
        }
        else
        {
            (BestThunderTarget, TargetThunderLeft) = SelectDotTarget(strategy, dotTarget, GetSingleTargetThunderLeft, 2);
            EnsureThunderRefreshFallbackTarget(strategy, dotTarget);
        }

        (BestAOEThunderTarget, NumAOEDotTargets) = SelectTarget(strategy, dotTarget, BLMTuning.DefaultTargetRange, AOEThunderCheck);

        // While a status locks every action (stun, sleep, the Down for the Count that phase transitions put on the whole
        // party), the game answers any request with 579 'Cannot execute at this time'. Queuing anything only makes
        // ActionManagerEx resubmit the same rejected action each frame: A35 Shinryu Paradox's 52 s transition stun
        // produced 5,211 rejected downtime Transposes and nothing else could have run either. Queue nothing; the
        // rotation picks up from the live gauge and cooldowns on the first unlocked frame.
        if (Locks.AllLocked)
            return;

        if (CountdownRemaining > 0)
        {
            var prepullGCD = Fire == 3 && Unlocked(AID.Fire4)
                ? AID.Fire4
                : Unlocked(AID.Fire3)
                    ? AID.Fire3
                    : AID.Fire1;

            if (CountdownRemaining < GetCastTime(prepullGCD))
                PushGCD(prepullGCD, primaryTarget, GCDPriority.Standard);

            if (strategy.Leylines.Value == LeylinesStrategy.Force && !HaveLeyLines && Unlocked(AID.LeyLines))
                PushAction(AID.LeyLines, Player, strategy.Leylines.Priority(), 0);

            return;
        }

        var standard57OpenerRoute = useAbilities && ShouldUseStandard57OpenerRoute(strategy);
        if (standard57OpenerRoute)
            ClearPlannerLock();

        UpdateManualToAutoManafontHandoff(strategy);

        var handoffManafontPushed = false;
        if (useAbilities
            && ManualToAutoManafontHandoffActive
            && ShouldUseManafontOnManualToAutoHandoff(strategy, primaryTarget, hasPriorityTarget))
        {
            var manafontPriority = ShouldUseStandard57OpenerControl(strategy) && Standard57AnyManafontWindow(strategy)
                ? GCDPriority.Opener
                : GCDPriority.ResourceCap;
            PushManafont(strategy, manafontPriority, primaryTarget);
            LogManualToAutoManafontHandoff($"state=push priority={manafontPriority}");
            handoffManafontPushed = true;
        }

        // The lock only advances by looking at the last cast, and TryPushPlannerLock is short-circuited away whenever the urgent
        // Polyglot or Thunder refresh push claims the frame first. A cast that finished during those frames was never seen: a
        // Despair fired while an overcap Xenoglossy waited on the GCD left the route asking for Despair at 0 MP, nothing was
        // queued, and the rotation idled until the 20 s lock expired. Observe every frame; the sequence guard makes it idempotent.
        AdvancePlannerLock();

        var plannerLockPushed = false;
        if (!standard57OpenerRoute && useAbilities && primaryTarget != null && !UseAOERotation())
            plannerLockPushed =
                TryPushUrgentPolyglot(strategy, primaryTarget, GCDPriority.ResourceCap)
                || TryPushThunderRefresh(strategy, GCDPriority.FuturePlan)
                || TryPushPlannerLock(strategy, primaryTarget);

        if (!plannerLockPushed && !standard57OpenerRoute && useAbilities && primaryTarget != null && !UseAOERotation() && !ShouldHoldEvenBurstRecoveryAction(BLMGameConstants.AmplifierRecast) && !UseFuturePlanner(strategy) && TryStartStandardDTRLock(strategy, primaryTarget))
            plannerLockPushed = true;

        var useManafontNow = !handoffManafontPushed && ShouldUseManafontNow(strategy, primaryTarget, hasPriorityTarget);
        if (useManafontNow)
        {
            var manafontPriority = ShouldUseStandard57OpenerControl(strategy) && Standard57AnyManafontWindow(strategy)
                ? GCDPriority.Opener
                : GCDPriority.FuturePlan;
            PushManafont(strategy, manafontPriority, primaryTarget);
        }

        if (!hasPriorityTarget)
        {
            var allowDowntimeTranspose = !ShouldSuppressDowntimeTransposeForShortTargetLoss();
            if (!allowDowntimeTranspose && PlannerLockedRoute.Contains(AID.Transpose))
                ClearPlannerLock();

            // A known early return only argues against leaving Astral Fire for the gap. Going back to AF1 at full MP with
            // Firestarter is the first thing the return would do anyway, and doing it now frees the first GCD after it.
            var allowTransposeToFire = !ShortTargetLossGraceActive();
            if ((allowDowntimeTranspose || allowTransposeToFire && Ice > 0) && useAbilities && Unlocked(AID.Transpose) && ReadyIn(AID.Transpose) == 0)
            {
                // swap back to ice in downtime if we can't fit in another FS
                if (Fire > 0 && allowDowntimeTranspose)
                {
                    var canFS = AstralSoul == 6 || MP switch
                    {
                        < 800 => false,
                        < 2400 => AstralSoul >= 3, // at >= 800, guaranteed enough MP to cast flare (though we probably want to use F4 as usual)
                        _ => Unlocked(AID.FlareStar), // TODO: this cutoff depends on heart stacks, but it probably doesn't matter in practice
                    };

                    if (!canFS && Player.HPMP.CurMP < Player.HPMP.MaxMP)
                        PushOGCD(AID.Transpose, Player);
                }

                // at max MP, switch back to AF1 to use firestarter
                if (Ice > 0 && Firestarter && Unlocked(AID.Fire3) && Player.HPMP.CurMP == Player.HPMP.MaxMP)
                    PushOGCD(AID.Transpose, Player);
            }

            if (!RecentlyEndedCombat() && Unlocked(AID.UmbralSoul) && Ice > 0 && (Ice < 3 || Hearts < MaxHearts || Player.HPMP.CurMP < Player.HPMP.MaxMP))
                PushGCD(AID.UmbralSoul, Player, GCDPriority.Standard);
        }

        if (primaryTarget == null)
            return;

        GoalZoneSingle(BLMTuning.DefaultTargetRange);

        if (Player.InCombat && FindOwnLeyLines() is Actor ll)
            Hints.GoalZones.Add(p => p.InCircle(ll.Position, 3) ? 0.5f : 0);

        if (ShouldFinishAFWithDespair(strategy))
        {
            AdvancePlannerLock();
            if (PlannerLockedIndex >= PlannerLockedRoute.Count || PlannerLockedRoute[PlannerLockedIndex] != AID.Despair)
                ClearPlannerLock();

            PushGCD(AID.Despair, primaryTarget, GCDPriority.Opener);
            return;
        }

        var usePhantomActions = useAbilities && CanUsePhantomActions();
        if (usePhantomActions && strategy.Zeninage.IsEnabled() && RaidBuffsLeft > GCD && DutyActionReadyIn(PhantomID.Zeninage) <= GCD)
            PushGCD((AID)PhantomID.Zeninage, primaryTarget, GCDPriority.Max);

        if (usePhantomActions && strategy.Iainuki.IsEnabled() && (CombatTimer > BLMTuning.BurstWarmupSeconds || RaidBuffsLeft > GCD))
        {
            var ready = DutyActionReadyIn(PhantomID.Iainuki);
            if (ready <= GCD + 0.05f)
                PushGCD((AID)PhantomID.Iainuki, primaryTarget, GCDPriority.Max);

            if (ready <= GCD + 0.05f + GCDLength * 2f)
                Hints.GoalZones.Add(Hints.GoalSingleTarget(primaryTarget.Actor, Player, World.Actors, 8f));
        }

        if (usePhantomActions && strategy.AutoTimeMage.IsEnabled())
        {
            if (DutyActionReadyIn(PhantomID.OccultQuick) <= GCD && (InLeyLines || CombatTimer > BLMTuning.BurstWarmupSeconds))
                PushGCD((AID)PhantomID.OccultQuick, Player, GCDPriority.Max);

            if ((CombatTimer > BLMTuning.BurstWarmupSeconds || RaidBuffsLeft > GCD) && DutyActionReadyIn(PhantomID.OccultComet) <= GCD)
            {
                if (ActiveInstantCast)
                    PushGCD((AID)PhantomID.OccultComet, EffectiveAOETarget(primaryTarget), GCDPriority.Max);
                else if (AllowNonMovementCastEnabler(strategy))
                {
                    if (AllowSwiftcast(strategy) && Unlocked(AID.Swiftcast) && CanWeave(AID.Swiftcast))
                        PushOGCD(AID.Swiftcast, Player);
                    else if (AllowTriplecast(strategy) && CanUseTriplecastForTarget(primaryTarget) && Unlocked(AID.Triplecast) && CanWeave(AID.Triplecast))
                        PushOGCD(AID.Triplecast, Player);
                }
            }
        }

        if (useAbilities)
        {
            UseLeylines(strategy, primaryTarget);
            UseTriplecastForced(strategy, primaryTarget);
            UseSwiftcastForced(strategy);
        }

        var transposeToIceB3 = !effectiveAOE && Fire > 0 && MP < MinAstralFireMP && AstralSoul < 6;
        if (useAbilities && !handoffManafontPushed && !useManafontNow && ShouldTranspose(strategy, primaryTarget))
        {
            PushUtilityOGCD(AID.Transpose, GCDPriority.FuturePlan);
            if (transposeToIceB3)
            {
                InstantB3TransposeReentryActive = true;
                InstantB3TransposeReentryUsedSwiftcast = ActiveSwiftcast && !ActiveTriplecast;
                InstantB3TransposeReentryUsedSwiftcast |= PushInstantB3(strategy, GCDPriority.FuturePlan, primaryTarget) == AID.Swiftcast;
            }
        }

        if (!plannerLockPushed)
        {
            var thunderInstantPrio = ThunderInstantPriority(strategy);
            PushThunder(strategy, thunderInstantPrio);
            TryStandard57OpenerGCD(strategy, primaryTarget);

            if (TryPushNeutralAOEStartGCD(strategy, primaryTarget))
            {
            }
            else if (Fire > 0)
                FirePhase(strategy, primaryTarget);
            else if (Ice > 0)
                IcePhase(strategy, primaryTarget);
            else if (MP >= BLMGameConstants.FullIceMP && !UseAOERotation())
                FirePhase(strategy, primaryTarget);
            else
                IcePhase(strategy, primaryTarget);

            if (!standard57OpenerRoute && UseFuturePlanner(strategy))
                UseFuturePlanner(strategy, primaryTarget);
        }

        if (useAbilities && ShouldUseMovementCastEnabler(strategy))
            PushSwiftOrTriplecast(strategy, ForMove(InstantCastPriority.CastEnabler), primaryTarget: primaryTarget, movementCastEnabler: true);

        var spendForOvercap = ShouldSpendPolyglotForOvercap();
        if (Polyglot > 0 && (!polyglotOvercapOnly || spendForOvercap) && ShouldSpendPolyglotNow(strategy))
        {
            var spendForAmplifier = ShouldSpendPolyglotForAmplifier(strategy);
            var spendForStandard57Manafont = ShouldSpendStandard57XenoBeforeManafont(strategy);
            // FightRemaining (value-of-information experiment): a dumped stack outranks Fire IV (Xenoglossy 1130 vs 686 potency)
            var prio = spendForStandard57Manafont ? GCDPriority.Opener : spendForAmplifier || spendForOvercap || VoiDumpNow ? GCDPriority.XenoCap : ForMove(InstantCastPriority.Polyglot);

            PushPolyglotGCD(primaryTarget, prio);
        }

        if (ShouldUseAmplifier(strategy))
            PushOGCD(AID.Amplifier, Player);

        if (strategy.Scathe.Value == ScatheStrategy.Allow && Unlocked(AID.Scathe))
            PushGCD(AID.Scathe, primaryTarget, GCDPriority.InstantMove - 50);
    }

    private void FirePhase(in Strategy strategy, Enemy? primaryTarget)
    {
        if (UseAOERotation())
        {
            if (Unlocked(TraitID.UmbralHeart))
                FirePhaseAOE(strategy, primaryTarget);
            else if (Unlocked(AID.Fire2)) // loll
                FireAOELowLevel(strategy, primaryTarget);
            else
                IceAOELowLevel(strategy, primaryTarget);
        }
        else
            FirePhaseST(strategy, primaryTarget);
    }

    private void FirePhaseST(in Strategy strategy, Enemy? primaryTarget)
    {
        if (TryAOEToSingleBridge(primaryTarget))
            return;

        if (Fire < 3)
        {
            if (ShouldUseStandard57AFParadoxReentry(strategy))
            {
                PushGCD(AID.Paradox, primaryTarget, GCDPriority.High);
                return;
            }

            if (!Firestarter)
            {
                // generate firestarter via paradox, then use fs to get f3
                // this results in us not getting another free paradox until next manafont
                if (Paradox)
                {
                    PushGCD(AID.Paradox, primaryTarget, GCDPriority.High);
                    return;
                }
                else if (AllowNonMovementCastEnabler(strategy) && AllowSwiftcast(strategy) && !ActiveInstantCast && !CanFitGCD(InstantCastLeft) && Unlocked(AID.Fire3) && Unlocked(AID.Swiftcast))
                    PushUtilityOGCD(AID.Swiftcast, GCDPriority.High);
            }

            if (Unlocked(AID.Fire3))
            {
                if (ElementSwapHardcastTransitionActive)
                    TryPushTransitionElementSwapGCD(strategy, AID.Fire3, primaryTarget, GCDPriority.High);
                else
                    PushGCD(AID.Fire3, primaryTarget, GCDPriority.High);
            }
            else
                PushGCD(AID.Fire1, primaryTarget, GCDPriority.High);
            return;
        }

        if (AstralSoul == 6 && Unlocked(AID.FlareStar))
            PushGCD(AID.FlareStar, primaryTarget, GCDPriority.Max);

        if (Unlocked(AID.Fire4))
            StandardF4(strategy, primaryTarget);
        else if (Unlocked(AID.Fire3))
        {
            if (Firestarter)
                PushGCD(AID.Fire3, primaryTarget, GCDPriority.Standard);

            PushGCD(AID.Fire1, primaryTarget, GCDPriority.Standard);

            if (Unlocked(AID.FlareStar) && MP < FireSpellCost && CanInstantB3(strategy, 1, primaryTarget))
                TryInstantCast(strategy, primaryTarget, GCDPriority.InstantWeave, useFirestarter: false, useThunderhead: false, usePolyglot: false, preferTriplecast: true);

            if (ElementSwapHardcastTransitionActive)
                TryPushTransitionElementSwapGCD(strategy, AID.Blizzard3, primaryTarget, GCDPriority.Standard, mpCutoff: FireSpellCost);
            else
                PushGCD(AID.Blizzard3, primaryTarget, GCDPriority.Standard, mpCutoff: FireSpellCost);
        }
        else
        {
            PushGCD(AID.Fire1, primaryTarget, GCDPriority.Standard);
            if (MP < FireSpellCost)
            {
                TryInstantOrTranspose(strategy, primaryTarget);
                PushGCD(AID.Blizzard1, primaryTarget, GCDPriority.Standard, mpCutoff: FireSpellCost);
            }
        }
    }

    private bool TryAOEToSingleBridge(Enemy? primaryTarget)
    {
        if (!AOEToSingleBridgeActive)
            return false;

        if (UseAOERotation() || !Unlocked(AID.FlareStar) || primaryTarget == null || primaryTarget.Actor.PendingDead)
        {
            AOEToSingleBridgeActive = false;
            return false;
        }

        if (Fire <= 0)
        {
            AOEToSingleBridgeActive = false;
            return false;
        }

        if (AstralSoul == 6)
        {
            PushGCD(AID.FlareStar, primaryTarget, GCDPriority.Max);
            AOEToSingleBridgeActive = false;
            return true;
        }

        if (AstralSoul >= 3 && MP >= 800 && Unlocked(AID.Flare))
        {
            PushGCD(AID.Flare, primaryTarget, GCDPriority.High);
            return true;
        }

        AOEToSingleBridgeActive = false;
        return false;
    }

    private bool ShouldKeepAOEToSingleFireBridge()
        => Fire > 0
        && (AstralSoul == 6
            || AstralSoul >= 3 && MP >= 800 && Unlocked(AID.Flare));

    private bool TryAOEToSingleIceBridge(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!AOEToSingleTransitionActive || UseAOERotation() || Ice <= 0 || primaryTarget == null || primaryTarget.Actor.PendingDead)
            return false;

        if (Ice < 3)
        {
            if (Unlocked(AID.Blizzard3))
            {
                return TryPushTransitionElementSwapGCD(strategy, AID.Blizzard3, primaryTarget, GCDPriority.High);
            }

            return false;
        }

        if (Unlocked(AID.Blizzard4) && Hearts < MaxHearts)
        {
            PushGCD(AID.Blizzard4, primaryTarget, GCDPriority.High);
            return true;
        }

        if (AlmostMaxMP && ReadyIn(AID.Transpose) <= GCD)
            return TryInstantOrTranspose(strategy, primaryTarget, useThunderhead: false, allowIceParadoxBeforeTranspose: false);

        return false;
    }

    private bool ShouldUseDespairBeforeFire4(in Strategy strategy)
    {
        // At Astral Soul 5 the next Fire IV completes Flare Star; Despair here would strand the stacks.
        if (Fire <= 0 || !Unlocked(AID.Despair) || AstralSoul >= 5 || MP < BLMGameConstants.DespairMinMP)
            return false;

        if (ShouldUseStandard57OpenerControl(strategy) && AstralSoul == 4 && ReadyIn(AID.Manafont) <= ManafontGCDWindow)
            return false;

        var currentMP = (int)MP;
        var fireCost = FireSpellCost;
        return currentMP >= fireCost
            && currentMP - fireCost < BLMGameConstants.DespairMinMP;
    }

    private bool ShouldFinishAFWithDespair(in Strategy strategy)
        => UseAbilities(strategy)
        && !UseAOERotation()
        && Fire == 3
        && AstralSoul == 0
        && MP >= BLMGameConstants.DespairMinMP
        && MP < FireSpellCost
        && Unlocked(AID.Despair)
        && LastFlareStarCastAt != default
        && (World.CurrentTime - LastFlareStarCastAt).TotalSeconds <= BLMTuning.RecentSpellWindowSeconds;

    private bool ShouldUseDespairBeforeAFParadox()
    {
        if (Fire != 3 || !Paradox || Firestarter || !Unlocked(AID.Despair) || AstralSoul >= 6 || MP < BLMGameConstants.DespairMinMP)
            return false;

        var currentMP = (int)MP;
        return currentMP >= BLMGameConstants.ParadoxFireMP
            && currentMP - BLMGameConstants.ParadoxFireMP < BLMGameConstants.DespairMinMP;
    }

    private bool CanUseAFParadox(in Strategy strategy)
    {
        if (!Unlocked(AID.Paradox) || !Paradox || Fire <= 0)
            return false;

        if (ShouldSkipStandard57AFParadox(strategy))
            return false;

        if (Firestarter && !MovementSkillPriorityRequested())
            return false;

        if (ShouldUseDespairBeforeAFParadox())
            return false;

        return true;
    }

    private bool ShouldHoldAFForNearManafont(in Strategy strategy, Enemy? primaryTarget)
    {
        if (primaryTarget == null || primaryTarget.Actor.PendingDead || Fire <= 0 || AstralSoul == 6 || !ManafontAllowedBySetting(strategy))
            return false;

        if (ManafontAutomatic(strategy) && ShouldHoldEvenBurstRecoveryAction(CurrentManafontRecast))
            return false;

        var manafontReadyIn = ReadyIn(AID.Manafont);
        if (manafontReadyIn <= 0 || manafontReadyIn > NearManafontHoldWindow(strategy, manafontReadyIn))
            return false;

        var timeline = CurrentTimelineContext();
        if (timeline.DowntimeNow || timeline.DowntimeIn <= GCDLength * 2)
            return false;

        if (ElementTimerLeft <= Math.Max(GCDLength, manafontReadyIn) + BLMTuning.ElementTimerSafetyBuffer)
            return false;

        return MP < FireSpellCost || ShouldUseDespairIntoNearManafont();
    }

    // How far ahead of Manafont the Astral Fire phase is held with instants instead of swapping to ice. The historical
    // window was two GCDs regardless of what could fill them; with a Polyglot stack, a Thunder refresh, Firestarter or an
    // affordable AF Paradox on hand each filler buys one more GCD slot (Manafont weaves inside the last one). Transposing
    // into ice a few seconds before Manafont throws away the 10000 MP it grants (2026-09-19 combat matrix: -1700 per run).
    private float NearManafontHoldWindow(in Strategy strategy, float manafontReadyIn)
    {
        // Two stacks may go out back to back here: the repeat lockout guards against dumping Polyglot, not against
        // bridging a known refill (see TryPushAFHoldForNearManafont).
        var fillers = Polyglot >= 2 ? 2 : Polyglot == 1 && !RecentlySpentPolyglot() ? 1 : 0;
        if (Thunderhead && ThunderRefreshModeEnabled(strategy) && BestThunderTarget != null && ThunderTargetWorthDot(BestThunderTarget.Actor) && TargetThunderLeft <= BLMTuning.ThunderRefreshWindow + manafontReadyIn)
            fillers++;
        if (CanUseAFParadox(strategy))
            fillers++;
        if (Firestarter && Unlocked(AID.Fire3))
            fillers++;
        fillers = Math.Min(fillers, BLMTuning.NearManafontHoldMaxFillers);
        return Math.Max(ManafontGCDWindow * 2, GCD + fillers * GCDLength - BLMTuning.NearManafontHoldMargin);
    }

    private bool ShouldUseDespairIntoNearManafont()
        => Unlocked(AID.Despair)
        && Unlocked(TraitID.EnhancedAstralFire)
        && MP >= BLMGameConstants.DespairMinMP
        && MP < FireSpellCost
        && ReadyIn(AID.Manafont) <= ManafontGCDWindow;

    private bool TryPushAFHoldForNearManafont(in Strategy strategy, Enemy? primaryTarget, GCDPriority priority)
    {
        if (!ShouldHoldAFForNearManafont(strategy, primaryTarget))
            return false;

        if (ShouldUseDespairIntoNearManafont())
        {
            PushGCD(AID.Despair, primaryTarget, priority);
            return true;
        }

        if (Polyglot > 0 && BestPolyglotGCD() != AID.None && (!RecentlySpentPolyglot() || Polyglot >= 2 || ReadyIn(AID.Manafont) <= ManafontGCDWindow))
        {
            PushPolyglotGCD(primaryTarget, priority + (int)InstantCastPriority.Polyglot);
            return true;
        }

        if (TryPushThunderRefresh(strategy, priority))
            return true;

        if (CanUseAFParadox(strategy))
        {
            PushGCD(AID.Paradox, primaryTarget, priority + (int)InstantCastPriority.Paradox);
            return true;
        }

        if (Firestarter && Unlocked(AID.Fire3))
        {
            PushGCD(AID.Fire3, primaryTarget, priority + (int)InstantCastPriority.Firestarter);
            return true;
        }

        return false;
    }

    private void StandardF4(in Strategy strategy, Enemy? primaryTarget)
    {
        var despairBeforeParadox = ShouldUseDespairBeforeAFParadox();
        var despairBeforeFire4 = ShouldUseDespairBeforeFire4(strategy);
        if (despairBeforeFire4)
            PushManafontFinisherCastEnabler(strategy, primaryTarget, AID.Despair);

        if (despairBeforeParadox && despairBeforeFire4)
            PushGCD(AID.Despair, primaryTarget, GCDPriority.High);

        if (CanUseAFParadox(strategy))
        {
            var paraPrio = ForMove(InstantCastPriority.Paradox);

            if (MP < FireSpellCost * 2 && AstralSoul < 5)
                paraPrio = GCDPriority.High;

            PushGCD(AID.Paradox, primaryTarget, paraPrio);
        }

        if (TryPushAFHoldForNearManafont(strategy, primaryTarget, GCDPriority.FuturePlan))
            return;

        // force instant cast if we want to accelerate B3 swap; Despair is instant and can already weave Manafont
        if (MP < MinAstralFireMP && ManafontAllowedBySetting(strategy) && CanWeave(AID.Manafont, 1))
            TryInstantCast(strategy, primaryTarget, GCDPriority.InstantWeave, preferTriplecast: true, useCastEnabler: false);
        else if (Unlocked(AID.FlareStar) && MP < MinAstralFireMP && CanInstantB3(strategy, 1, primaryTarget))
            TryInstantCast(strategy, primaryTarget, GCDPriority.InstantWeave, preferTriplecast: true);

        if (Unlocked(AID.Despair) && MP < 2400 && !Unlocked(AID.FlareStar) && despairBeforeFire4)
            PushGCD(AID.Despair, primaryTarget, GCDPriority.Standard);

        if (!despairBeforeParadox && despairBeforeFire4)
            PushGCD(AID.Despair, primaryTarget, GCDPriority.High);

        // TODO: BLM doesn't really fit the priority system that well because of the MP cutoff stuff
        PushGCD(AID.Fire4, primaryTarget, GCDPriority.Standard);
        if (Unlocked(AID.Despair))
            PushGCD(AID.Despair, primaryTarget, GCDPriority.Standard, mpCutoff: FireSpellCost);
        if (ElementSwapHardcastTransitionActive)
            TryPushTransitionElementSwapGCD(strategy, AID.Blizzard3, primaryTarget, GCDPriority.Standard, mpCutoff: MinAstralFireMP);
        else
            PushGCD(AID.Blizzard3, primaryTarget, GCDPriority.Standard, mpCutoff: MinAstralFireMP);

        // use as instant cast, otherwise save for UI switch
        if (Firestarter && Unlocked(AID.Fire3))
            PushGCD(AID.Fire3, primaryTarget, ForMove(InstantCastPriority.Firestarter));

    }

    private void FirePhaseAOE(in Strategy strategy, Enemy? primaryTarget)
    {
        var aoeTarget = EffectiveAOETarget(primaryTarget);
        if (aoeTarget == null)
            return;

        if (AstralSoul == 6 && Unlocked(AID.FlareStar))
            PushGCD(AID.FlareStar, aoeTarget, GCDPriority.Max);

        if (Unlocked(AID.Flare) && MP >= 800 && Fire > 0)
        {
            if (Hearts == 0 && AstralSoul < 6)
                PushManafontFinisherCastEnabler(strategy, aoeTarget, AID.Flare);

            PushGCD(AID.Flare, aoeTarget, GCDPriority.Standard);
        }

        if (AstralSoul < 6 && Fire > 0 && MP < 800)
        {
            var fillerPriority = CanWeave(AID.Transpose) ? GCDPriority.InstantMove : GCDPriority.FuturePlan;
            if (TryAOETransposeFiller(strategy, aoeTarget, fillerPriority))
                return;

            if (!TryInstantOrTranspose(strategy, aoeTarget, useThunderhead: false, allowIceParadoxBeforeTranspose: false))
            {
                // At level 100, keep the Flare / Flare Star loop on Transpose even while its recast is pending.
                if (Unlocked(AID.FlareStar))
                    PushUtilityOGCD(AID.Transpose, GCDPriority.Standard);
                else
                    PushGCD(AOEBlizzard2Worthwhile(NumAOETargets) ? BestAOEBlizzard2() : AID.Blizzard3, aoeTarget, GCDPriority.Standard);
            }
            return;
        }

        TryInstantCast(strategy, aoeTarget, GCDPriority.InstantMove, usePolyglot: !PolyglotOvercapOnly(strategy), useCastEnabler: false);
    }

    private void FireAOELowLevel(in Strategy strategy, Enemy? primaryTarget)
    {
        if (Unlocked(AID.Flare) && MP < 3000 && MP >= 800)
        {
            PushGCD(AID.Flare, EffectiveAOETarget(primaryTarget), GCDPriority.High);
            return;
        }

        if (MP < GetManaCost(Unlocked(AID.Fire2) ? AID.Fire2 : AID.Fire1))
        {
            TryInstantOrTranspose(strategy, primaryTarget);

            PushGCD(Unlocked(AID.Blizzard3) ? AID.Blizzard3 : AID.Blizzard1, primaryTarget, GCDPriority.Standard);
            return;
        }

        if (Unlocked(AID.Fire2))
            PushGCD(AID.Fire2, EffectiveAOETarget(primaryTarget), GCDPriority.Standard);
        else
            PushGCD(AID.Fire1, primaryTarget, GCDPriority.Standard);
    }

    private void IcePhase(in Strategy strategy, Enemy? primaryTarget)
    {
        if (UseAOERotation())
        {
            if (Unlocked(TraitID.UmbralHeart))
                IcePhaseAOE(strategy, primaryTarget);
            else
                IceAOELowLevel(strategy, primaryTarget);
        }
        else
            IcePhaseST(strategy, primaryTarget);
    }

    private void IcePhaseST(in Strategy strategy, Enemy? primaryTarget)
    {
        if (ShouldUseIceParadoxBeforeTranspose())
        {
            PushGCD(AID.Paradox, primaryTarget, GCDPriority.FuturePlan);
            return;
        }

        if (ShouldUseStandard57IceReentry(strategy) && RecentObservedSpell(AID.Blizzard4) && Ice < 3)
        {
            if (ShouldUseIceParadoxBeforeTranspose())
                PushGCD(AID.Paradox, primaryTarget, GCDPriority.FuturePlan);
            return;
        }

        if (TryAOEToSingleIceBridge(strategy, primaryTarget))
            return;

        if (Ice < 3 && Unlocked(AID.Blizzard3))
        {
            if (ElementSwapHardcastTransitionActive)
            {
                TryPushTransitionElementSwapGCD(strategy, AID.Blizzard3, primaryTarget, GCDPriority.High);
                return;
            }

            if (PushInstantB3(strategy, GCDPriority.High, primaryTarget) != AID.None)
                return;

            PushGCD(AID.Blizzard3, primaryTarget, GCDPriority.High);
            return;
        }

        if (Unlocked(AID.Blizzard4) && Hearts < MaxHearts)
        {
            PushGCD(AID.Blizzard4, primaryTarget, GCDPriority.High);
            return;
        }

        if (ShouldForceSwiftInstantB3ReentryParadox())
        {
            PushGCD(AID.Paradox, primaryTarget, GCDPriority.FuturePlan);
            return;
        }

        if (ShouldCompleteInstantB3TransposeReentry())
        {
            // Ice Paradox is free and instant: spend it regardless of MP, otherwise the reentry stalls (Paradox blocks Transpose, MP < 96% blocks Paradox).
            if (Paradox)
            {
                PushGCD(AID.Paradox, primaryTarget, GCDPriority.FuturePlan);
                return;
            }

            if (Firestarter && ReadyIn(AID.Transpose) <= GCD)
                return;
        }

        if (ShouldUseStandard57IceReentry(strategy))
        {
            if (ShouldUseIceParadoxBeforeTranspose())
            {
                PushGCD(AID.Paradox, primaryTarget, GCDPriority.FuturePlan);
                return;
            }

            if (ShouldHoldStandard57IceForTranspose(strategy))
                return;
        }

        if (AlmostMaxMP)
        {
            // Spend ice paradox before the UB3 -> AF1 transpose return, even if Firestarter is already reserved.
            if (ShouldUseIceParadoxBeforeTranspose())
            {
                PushGCD(AID.Paradox, primaryTarget, GCDPriority.FuturePlan);
                return;
            }

            if (Paradox)
            {
                // Hearts are full here: the Blizzard IV branch above already returned otherwise.
                PushGCD(AID.Paradox, primaryTarget, GCDPriority.FuturePlan);
                return;
            }

            if (UseAbilities(strategy) && Firestarter && CanWeave(AID.Transpose, 1) && !ActiveInstantCast && !CanFitGCD(InstantCastLeft))
                TryInstantCast(strategy, primaryTarget, GCDPriority.InstantWeave, useFirestarter: false);

            if (Unlocked(AID.Fire3))
            {
                if (ElementSwapHardcastTransitionActive)
                {
                    TryPushTransitionElementSwapGCD(strategy, AID.Fire3, primaryTarget, GCDPriority.InstantWeave);
                    return;
                }

                PushGCD(AID.Fire3, primaryTarget, GCDPriority.InstantWeave);
            }
            else
                PushGCD(AID.Fire1, primaryTarget, GCDPriority.Standard);
        }

        if (Unlocked(AID.Blizzard4))
            PushGCD(AID.Blizzard4, primaryTarget, GCDPriority.Standard);
        else
            PushGCD(AID.Blizzard1, primaryTarget, GCDPriority.Standard);
    }

    private void IcePhaseAOE(in Strategy strategy, Enemy? primaryTarget)
    {
        if (Ice == 0)
        {
            var aoeTarget = BestAOETarget ?? primaryTarget;
            if (aoeTarget == null)
                return;

            PushGCD(AOEBlizzard2Worthwhile(NumAOETargets) ? BestAOEBlizzard2() : Unlocked(AID.Blizzard3) ? AID.Blizzard3 : AID.Blizzard1, aoeTarget, GCDPriority.Standard);
        }
        else if (Hearts < MaxHearts || MP < 2400)
        {
            PushAOEIceHeartGCD(primaryTarget, GCDPriority.Standard);
        }
        else if (NumAOETargets >= FreezeBreakpoint && Unlocked(AID.Freeze) && ReadyIn(AID.Transpose) > GCD)
        {
            PushGCD(AID.Freeze, EffectiveAOETarget(primaryTarget), GCDPriority.InstantMove);
        }

        if (Ice > 0 && Hearts == MaxHearts && MP >= 2400)
        {
            var fillerPriority = CanWeave(AID.Transpose) ? GCDPriority.InstantMove : GCDPriority.FuturePlan;
            if (TryAOETransposeFiller(strategy, primaryTarget, fillerPriority))
                return;

            if (!TryInstantOrTranspose(strategy, primaryTarget, useThunderhead: false, allowIceParadoxBeforeTranspose: false))
            {
                if (Unlocked(AID.FlareStar))
                {
                    PushUtilityOGCD(AID.Transpose, GCDPriority.Standard);
                    PushAOEIceHeartGCD(primaryTarget, GCDPriority.InstantMove);
                }
                else
                    PushGCD(AID.Fire2, EffectiveAOETarget(primaryTarget), GCDPriority.Standard);
            }
            return;
        }

        TryInstantCast(strategy, primaryTarget, GCDPriority.InstantMove, usePolyglot: !PolyglotOvercapOnly(strategy), useCastEnabler: false);
    }

    private void PushAOEIceHeartGCD(Enemy? primaryTarget, GCDPriority priority)
    {
        if (NumAOETargets >= FreezeBreakpoint && Unlocked(AID.Freeze))
            PushGCD(AID.Freeze, EffectiveAOETarget(primaryTarget), priority);
        else if (Unlocked(AID.Blizzard4))
            PushGCD(AID.Blizzard4, primaryTarget, priority);
        else
            PushGCD(AID.Blizzard1, primaryTarget, priority);
    }

    private void IceAOELowLevel(in Strategy strategy, Enemy? primaryTarget)
    {
        if (AlmostMaxMP)
        {
            if (!Unlocked(TraitID.AspectMastery3))
                TryInstantOrTranspose(strategy, primaryTarget);

            if (Unlocked(AID.Fire2))
                PushGCD(AID.Fire2, EffectiveAOETarget(primaryTarget), GCDPriority.Standard);
            else
                PushGCD(AID.Fire1, primaryTarget, GCDPriority.Standard);

            return;
        }

        if (Ice == 3 && NumAOETargets >= FreezeBreakpoint && Unlocked(AID.Freeze))
        {
            PushGCD(AID.Freeze, EffectiveAOETarget(primaryTarget), GCDPriority.Standard);
            return;
        }

        PushGCD(Ice < 3 && Unlocked(AID.Blizzard3) ? AID.Blizzard3 : AID.Blizzard1, primaryTarget, GCDPriority.Standard);
    }

    private bool TryAOETransposeFiller(in Strategy strategy, Enemy? primaryTarget, GCDPriority prio)
    {
        if (Polyglot > 0 && !PolyglotOvercapOnly(strategy) && ShouldSpendPolyglotNow(strategy))
        {
            PushPolyglotGCD(primaryTarget, prio + (int)InstantCastPriority.Polyglot);
            return true;
        }

        if (Thunderhead && UseAOEThunder() && strategy.Thunder.Value != ThunderStrategy.Delay)
        {
            PushThunder(strategy, prio + (int)InstantCastPriority.TP);
            return true;
        }

        if (Paradox && Ice > 0 && Unlocked(AID.Paradox))
        {
            PushGCD(AID.Paradox, primaryTarget, prio + (int)InstantCastPriority.Paradox);
            return true;
        }

        return false;
    }

    private static GCDPriority Priomax(GCDPriority g1, GCDPriority g2) => g1 > g2 ? g1 : g2;

    private bool HasAnyAOEGCD()
        => Unlocked(AID.Fire2)
        || Unlocked(AID.Freeze)
        || Unlocked(AID.Flare)
        // Before Fire II the only AoE spell is Blizzard II, which only beats the single-target loop from 4 targets; with fewer the AoE
        // rotation would just alternate Fire / Blizzard on one target, so it stays off (measured: -0.7% on the level 15 DMU add phases).
        || AOEBlizzard2Worthwhile(NumAOETargets);

    // Blizzard II / High Blizzard II enter Umbral Ice III with a full MP bar exactly like Blizzard III, but hit every target for 80 / 100
    // instead of one for 290: they pay off from 3 targets once upgraded (4 before), and only as the neutral-element AoE entry or the
    // pre-Flare Star fire-to-ice fallback; at level 100 the Flare / Flare Star loop keeps its Transpose entry.
    private AID BestAOEBlizzard2()
        => Unlocked(AID.HighBlizzard2) ? AID.HighBlizzard2 : Unlocked(AID.Blizzard2) ? AID.Blizzard2 : AID.None;

    private bool AOEBlizzard2Worthwhile(int targets)
        => Unlocked(AID.HighBlizzard2) ? targets >= 3 : Unlocked(AID.Blizzard2) && targets >= 4;

    private bool UseAOERotation()
        => NumAOETargets >= AOEBreakpoint && HasAnyAOEGCD();

    private Enemy? EffectiveAOETarget(Enemy? primaryTarget)
        => BestAOETarget ?? primaryTarget;

    private bool IsUsableAOECastTarget(Enemy? target)
        => target != null
        && target.Priority is not Enemy.PriorityInvincible and not Enemy.PriorityForbidden
        && target.Actor.IsTargetable
        && !target.Actor.PendingDead
        && target.Actor.HPMP.CurHP > 0
        && Player.DistanceToHitbox(target.Actor) <= BLMTuning.DefaultTargetRange;

    private (Enemy? Target, int Count) SelectAOECenterTarget(in Strategy strategy, Enemy? primaryTarget)
    {
        var selected = SelectTargetByHP(strategy, primaryTarget, BLMTuning.DefaultTargetRange, IsSplashTarget);

        // the splash scan is a second pass over every pair of targets, so it only runs when its result can replace the selection
        if (!IsUsableAOECastTarget(selected.Best))
            return strategy.Targeting == Targeting.Manual || strategy.AOE == AOEStrategy.ForceST ? (null, 0) : SelectBestAOECenterTarget(strategy, primaryTarget);

        if (strategy.Targeting != Targeting.Manual && selected.Targets < AOEBreakpoint)
        {
            var bestSplash = SelectBestAOECenterTarget(strategy, primaryTarget);
            if (bestSplash.Count >= AOEBreakpoint)
            {
                return bestSplash;
            }
        }

        return (selected.Best, selected.Targets);
    }

    private (Enemy? Target, int Count) SelectBestAOECenterTarget(in Strategy strategy, Enemy? primaryTarget)
    {
        Enemy? best = null;
        var bestCount = 0;
        ulong bestHP = 0;

        foreach (var enemy in Hints.PriorityTargetsSpan)
        {
            var actor = enemy.Actor;
            if (!IsUsableAOECastTarget(enemy))
                continue;

            var count = AdjustNumTargets(strategy.AOE, CountSplashTargets(enemy));
            var hp = (ulong)actor.HPMP.CurHP;
            if (count > bestCount || count == bestCount && hp > bestHP)
            {
                best = enemy;
                bestCount = count;
                bestHP = hp;
            }
        }

        if (best == null && IsUsableAOECastTarget(primaryTarget))
            return (primaryTarget, AdjustNumTargets(strategy.AOE, CountSplashTargets(primaryTarget!)));

        return (best, bestCount);
    }

    private int CountSplashTargets(Enemy center)
    {
        foreach (var forbidden in Hints.ForbiddenTargetsSpan)
        {
            if (IsSplashTarget(center.Actor, forbidden.Actor))
                return 0;
        }

        var count = 0;
        foreach (var enemy in Hints.PriorityTargetsSpan)
        {
            var actor = enemy.Actor;
            if (actor.PendingDead || actor.HPMP.CurHP <= 0)
                continue;

            if (IsSplashTarget(center.Actor, actor))
                ++count;
        }

        return count;
    }

    private bool TryPushNeutralAOEStartGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!UseAOERotation() || Fire != 0 || Ice != 0)
            return false;

        var aoeTarget = BestAOETarget ?? primaryTarget;
        if (aoeTarget == null)
            return false;

        if (AOEBlizzard2Worthwhile(NumAOETargets))
        {
            PushGCD(BestAOEBlizzard2(), aoeTarget, GCDPriority.Standard);
            return true;
        }

        // Otherwise enter ice with Blizzard III (it also works from Astral Fire).
        if (Unlocked(AID.Blizzard3))
        {
            PushGCD(AID.Blizzard3, aoeTarget, GCDPriority.Standard);
            return true;
        }

        if (Unlocked(AID.Blizzard1))
        {
            PushGCD(AID.Blizzard1, aoeTarget, GCDPriority.Standard);
            return true;
        }

        if (Unlocked(AID.Fire1))
        {
            PushGCD(AID.Fire1, primaryTarget, GCDPriority.Standard);
            return true;
        }

        return false;
    }

    private bool HasAOEThunder()
        => Unlocked(AID.Thunder2)
        || Unlocked(AID.Thunder4)
        || Unlocked(AID.HighThunder2);

    private bool UseAOEThunder()
        => HasAOEThunder() && NumAOEDotTargets >= AOEBreakpoint;

    private static bool IsSingleThunderAction(AID aid)
        => aid is AID.Thunder1 or AID.Thunder3 or AID.HighThunder;

    private static bool IsAOEThunderAction(AID aid)
        => aid is AID.Thunder2 or AID.Thunder4 or AID.HighThunder2;

    private static bool IsThunderAction(AID aid)
        => IsSingleThunderAction(aid) || IsAOEThunderAction(aid);

    private static bool IsPolyglotAction(AID aid)
        => aid is AID.Xenoglossy or AID.Foul;

    private bool ThunderRefreshModeEnabled(in Strategy strategy)
        => strategy.Thunder.Value is not ThunderStrategy.Delay and not ThunderStrategy.InstantOnly;

    private bool SingleThunderRefreshReady(in Strategy strategy)
        => Thunderhead
        && ThunderRefreshModeEnabled(strategy)
        && BestThunderTarget != null
        && ThunderTargetWorthDot(BestThunderTarget.Actor)
        && DotExpiring(TargetThunderLeft);

    private bool AOEThunderRefreshReady(in Strategy strategy)
        => Thunderhead
        && ThunderRefreshModeEnabled(strategy)
        && UseAOEThunder()
        && BestAOEThunderTarget != null
        && ThunderTargetWorthDot(BestAOEThunderTarget.Actor, BLMTuning.AOEThunderMinTargetLife)
        && NumAOEDotTargets >= AOEBreakpoint;

    private bool TryPushThunderRefresh(in Strategy strategy, GCDPriority priority)
    {
        if (AOEThunderRefreshReady(strategy))
        {
            PushGCD(BestAOEThunder(), BestAOEThunderTarget, priority);
            return true;
        }

        if (SingleThunderRefreshReady(strategy))
        {
            PushGCD(BestSingleThunder(), BestThunderTarget, priority);
            return true;
        }

        return false;
    }

    private void EnsureThunderRefreshFallbackTarget(in Strategy strategy, Enemy? dotTarget)
    {
        if (BestThunderTarget != null)
            return;

        // SelectDotTarget intentionally refuses broad multi-dotting above its target cap.
        // Keep that behavior, but retain one refresh target so Thunderhead does not get stranded
        // when AoE Thunder is not worth using yet.
        (var fallbackTarget, var fallbackLeft) = SelectDotTarget(strategy, dotTarget, GetSingleTargetThunderLeft, int.MaxValue);
        if (fallbackTarget != null && ThunderTargetWorthDot(fallbackTarget.Actor))
        {
            BestThunderTarget = fallbackTarget;
            TargetThunderLeft = fallbackLeft;
        }
    }

    private bool CanUseHighLevelPlanner()
        => Unlocked(AID.Fire4) && Unlocked(AID.Blizzard3) && Unlocked(AID.Despair) && Unlocked(AID.Xenoglossy);

    private bool CanUsePhantomActions()
        => Unlocked(TraitID.EnhancedPolyglotII);

    private AID BestSingleThunder()
    {
        if (Unlocked(AID.HighThunder))
            return AID.HighThunder;
        if (Unlocked(AID.Thunder3))
            return AID.Thunder3;
        return AID.Thunder1;
    }

    private AID BestAOEThunder()
    {
        if (Unlocked(AID.HighThunder2))
            return AID.HighThunder2;
        if (Unlocked(AID.Thunder4))
            return AID.Thunder4;
        if (Unlocked(AID.Thunder2))
            return AID.Thunder2;
        return BestSingleThunder();
    }

    private void PushThunder(in Strategy strategy, GCDPriority prioForInstant = GCDPriority.None)
    {
        if (UseAOEThunder())
            T2(strategy, prioForInstant);
        else
            T1(strategy, prioForInstant);
    }

    private GCDPriority ThunderInstantPriority(in Strategy strategy)
    {
        if (!ShouldSpendThunderheadForMovement(strategy))
            return GCDPriority.None;

        return MovementSkillPriorityRequested()
            ? GCDPriority.InstantMove
            : GCDPriority.None;
    }

    private bool ShouldSpendThunderheadForMovement(in Strategy strategy)
    {
        if (strategy.Thunder.Value == ThunderStrategy.InstantOnly)
            return true;

        if (strategy.Thunder.Value is ThunderStrategy.Delay or ThunderStrategy.ForbidInstant)
            return false;

        return UseAOEThunder()
            ? NumAOEDotTargets >= AOEBreakpoint
            : TargetThunderLeft <= BLMTuning.PlannerMovementThunderRefreshWindow;
    }

    private void T1(in Strategy strategy, GCDPriority prioForInstant = GCDPriority.None)
    {
        if (!Thunderhead)
            return;

        if (strategy.Thunder.Value != ThunderStrategy.Force && !ThunderTargetWorthDot(BestThunderTarget?.Actor))
            return;

        var prioStandard = DotExpiring(TargetThunderLeft) ? GCDPriority.DotRefresh : GCDPriority.None;
        var prioInstant = prioForInstant != GCDPriority.None && ShouldSpendThunderheadForMovement(strategy)
            ? prioForInstant + (int)InstantCastPriority.TP
            : GCDPriority.None;

        var prio = strategy.Thunder.Value switch
        {
            // use to refresh normally, or use as utility cast
            ThunderStrategy.Automatic => MovementCastEnablerRequested() && prioInstant != GCDPriority.None ? prioInstant : Priomax(prioStandard, prioInstant),
            // only use to refresh normally
            ThunderStrategy.ForbidInstant => prioStandard,
            // ignore timer, refresh asap
            ThunderStrategy.Force => GCDPriority.Max,
            // only use for utility, ignore timer
            ThunderStrategy.InstantOnly => prioInstant,
            _ => GCDPriority.None
        };

        if (prio == GCDPriority.None)
            return;

        PushGCD(BestSingleThunder(), BestThunderTarget, prio);
    }

    private void T2(in Strategy strategy, GCDPriority prioForInstant = GCDPriority.None)
    {
        if (!Thunderhead)
            return;

        if (strategy.Thunder.Value != ThunderStrategy.Force && !ThunderTargetWorthDot(BestAOEThunderTarget?.Actor, BLMTuning.AOEThunderMinTargetLife))
            return;

        var prioStandard = NumAOEDotTargets >= AOEBreakpoint ? GCDPriority.DotRefresh : GCDPriority.None;
        var prioInstant = prioForInstant != GCDPriority.None && UseAOEThunder() && ShouldSpendThunderheadForMovement(strategy)
            ? prioForInstant + (int)InstantCastPriority.TP
            : GCDPriority.None;

        var prio = strategy.Thunder.Value switch
        {
            // use to refresh normally, or use as utility cast
            ThunderStrategy.Automatic => MovementCastEnablerRequested() && prioInstant != GCDPriority.None ? prioInstant : Priomax(prioStandard, prioInstant),
            // only use to refresh normally
            ThunderStrategy.ForbidInstant => prioStandard,
            // ignore timer, just check if we have enough AOE targets
            ThunderStrategy.Force => UseAOEThunder() ? GCDPriority.Max : GCDPriority.None,
            // only use for utility, ignore timer
            ThunderStrategy.InstantOnly => prioInstant,
            _ => GCDPriority.None
        };

        if (prio == GCDPriority.None)
            return;

        PushGCD(BestAOEThunder(), BestAOEThunderTarget, prio);
    }

    private AID BestPolyglotGCD()
    {
        if (UseAOERotation() && Unlocked(AID.Foul))
            return AID.Foul;

        if (Unlocked(AID.Xenoglossy))
            return AID.Xenoglossy;

        if (Unlocked(AID.Foul))
            return AID.Foul;

        return AID.None;
    }

    private Enemy? BestPolyglotTarget(Enemy? primaryTarget, AID aid)
        => aid == AID.Foul && UseAOERotation()
            ? BestAOETarget ?? primaryTarget
            : primaryTarget;

    private void PushPolyglotGCD(Enemy? primaryTarget, GCDPriority priority)
    {
        var aid = BestPolyglotGCD();
        if (aid == AID.None || Polyglot <= 0)
            return;

        PushGCD(aid, BestPolyglotTarget(primaryTarget, aid), priority);
    }

    private bool ShouldUrgentSpendPolyglot(in Strategy strategy)
    {
        if (Polyglot <= 0)
            return false;

        if (PolyglotOvercapOnly(strategy))
            return ShouldSpendPolyglotForOvercap();

        // FightRemaining (value-of-information experiment): the fight ends within the dump window, a held stack is worth nothing
        return ShouldSpendPolyglotForOvercap()
            || ShouldSpendPolyglotForAmplifier(strategy)
            || VoiDumpNow;
    }

    private bool TryPushUrgentPolyglot(in Strategy strategy, Enemy? primaryTarget, GCDPriority priority)
    {
        if (!ShouldUrgentSpendPolyglot(strategy))
            return false;

        if (BestPolyglotGCD() == AID.None)
            return false;

        PushPolyglotGCD(primaryTarget, priority);
        return true;
    }

    private float TimeToNextWeaveWindow()
    {
        if ((uint)(object)NextGCD == 0)
            return 0;

        var def = ActionDefinitions.Instance.Spell(NextGCD);
        if (def == null)
            return 0;

        var castTime = GetCastTime(NextGCD);
        return Math.Max(castTime, def.InstantAnimLock) + AnimationLockDelay;
    }

    private bool ShouldSpendPolyglotForOvercap() => Polyglot >= MaxPolyglot && !CanFitGCD(NextPolyglot, 1);

    private bool WouldAmplifierCauseNearOvercap(int polyglot, float nextPolyglot)
        => polyglot + 1 >= MaxPolyglot && nextPolyglot <= GCDLength;

    private bool ShouldSpendPolyglotForAmplifier(in Strategy strategy)
    {
        if (!UseAbilities(strategy)
            || !Player.InCombat
            || ShouldHoldEvenBurstRecoveryAction(BLMGameConstants.AmplifierRecast)
            || ShouldHoldForWindurstBurst(strategy, ReadyIn(AID.Amplifier))
            || !Unlocked(AID.Amplifier)
            || Polyglot <= 0
            || !AmplifierReadySoonForSpend())
            return false;

        if (Polyglot >= MaxPolyglot)
            return !RecentlySpentPolyglot();

        if (Polyglot >= MaxPolyglot - 1 && WouldAmplifierCauseNearOvercap(Polyglot, NextPolyglot))
            return !RecentlySpentPolyglot();

        return false;
    }

    private int DesiredPolyglotReserve(in Strategy strategy)
    {
        if (EvenBurstRecoveryActive)
            return MaxPolyglot;

        if (RaidBuffsIn <= 15 || RaidBuffsLeft > 0)
            return MaxPolyglot;

        return Math.Max(0, MaxPolyglot - 1);
    }

    private bool ShouldSpendPolyglotNow(in Strategy strategy)
    {
        if (ShouldSpendPolyglotForOvercap()
            || ShouldSpendPolyglotForAmplifier(strategy)
            || ShouldSpendStandard57XenoBeforeManafont(strategy))
            return true;

        var timeline = CurrentTimelineContext();
        if (timeline.DowntimeIn <= GCDLength * 2)
            return true;

        // FightRemaining (value-of-information experiment): dump every stack before the fight ends
        if (VoiDumpNow)
            return true;

        if (ShouldSpendPolyglotForMovement(strategy, timeline))
            return true;

        // Under raid buffs dump stacks back to back; the lockout only exists to pace spending outside buffs.
        if (RaidBuffsLeft > 0)
            return true;

        return Polyglot > DesiredPolyglotReserve(strategy) && !RecentlySpentPolyglot();
    }

    private bool ShouldSpendPolyglotForMovement(in Strategy strategy, TimelineContext timeline)
        => !PolyglotOvercapOnly(strategy)
        && MovementSkillPriorityRequested()
        && CanUsePolyglotForMovement();

    private bool CanUsePolyglotForMovement()
    {
        var aid = BestPolyglotGCD();
        return Polyglot > 0 && aid != AID.None && GetCastTime(aid) <= 0;
    }

    private bool CanUseThunderheadForMovement(in Strategy strategy)
    {
        if (!Thunderhead || ThunderInstantPriority(strategy) == GCDPriority.None)
            return false;

        return UseAOEThunder()
            ? BestAOEThunderTarget != null && ThunderTargetWorthDot(BestAOEThunderTarget.Actor, BLMTuning.AOEThunderMinTargetLife)
            : BestThunderTarget != null && ThunderTargetWorthDot(BestThunderTarget.Actor);
    }

    private bool CanUseParadoxForMovement(in Strategy strategy)
    {
        if (!Unlocked(AID.Paradox) || !Paradox || ShouldSkipStandard57AFParadox(strategy))
            return false;

        return Fire > 0 ? CanUseAFParadox(strategy) : true;
    }

    private bool CanUseFirestarterForMovement()
        => Firestarter && Unlocked(AID.Fire3);

    private bool QueuedGCD(AID aid)
    {
        var action = ActionID.MakeSpell(aid);
        foreach (var e in Hints.ActionsToExecute.Entries)
            if (e.Action == action)
                return true;

        return false;
    }

    private bool HasPreferredMovementInstantGCD(in Strategy strategy)
    {
        if (CanUseThunderheadForMovement(strategy))
            return true;

        if (CanUseParadoxForMovement(strategy))
            return true;

        return CanUseFirestarterForMovement();
    }

    private bool HasHigherPriorityMovementInstantBeforeCastEnabler(in Strategy strategy)
    {
        if (!MovementSkillPriorityRequested())
            return false;

        var timeline = CurrentTimelineContext();
        if (CanUsePolyglotForMovement()
            && (ShouldSpendPolyglotForMovement(strategy, timeline)
                || !RecentlySpentPolyglot()
                    && (ShouldSpendPolyglotForOvercap()
                        || ShouldSpendPolyglotForAmplifier(strategy))))
            return true;

        return CanUseThunderheadForMovement(strategy)
            || CanUseParadoxForMovement(strategy) && QueuedGCD(AID.Paradox)
            || CanUseFirestarterForMovement() && QueuedGCD(AID.Fire3);
    }

    private bool RecentlySpentPolyglot(float lockout = BLMTuning.RecentlySpentPolyglotLockoutSeconds)
        => LastPolyglotSpendAt != default && (World.CurrentTime - LastPolyglotSpendAt).TotalSeconds <= lockout
        || RecentObservedSpell(AID.Xenoglossy, lockout)
        || RecentObservedSpell(AID.Foul, lockout);

    private bool AmplifierReadySoonForSpend()
        => Unlocked(AID.Amplifier) && (CanWeave(AID.Amplifier) || ReadyIn(AID.Amplifier) <= TimeToNextWeaveWindow());

    private bool ShouldUseAmplifier(in Strategy strategy)
    {
        if (!UseAbilities(strategy)
            || !Player.InCombat
            || ShouldHoldEvenBurstRecoveryAction(BLMGameConstants.AmplifierRecast)
            || ShouldHoldForWindurstBurst(strategy, ReadyIn(AID.Amplifier))
            || !Unlocked(AID.Amplifier)
            || !CanWeave(AID.Amplifier))
            return false;

        // FightRemaining (value-of-information experiment): a stack that is generated now can still be spent before the fight ends
        if (Voi.AmpWindow > 0 && VoiEndIn(Voi.DumpStat) <= Voi.AmpWindow && Polyglot < MaxPolyglot)
            return true;

        if (ShouldUseStandard57OpenerControl(strategy))
            return false;

        if (ShouldSpendPolyglotForAmplifier(strategy))
            return false;

        if (Polyglot >= MaxPolyglot)
            return false;

        return !WouldAmplifierCauseNearOvercap(Polyglot, NextPolyglot);
    }

    private enum PlannerStep
    {
        None,
        ReserveInstant,
        Amplifier,
        LucidDreaming,
        Manafont,
        Transpose,
        Blizzard3,
        Blizzard4,
        Fire3,
        Fire4,
        Despair,
        FlareStar,
        Paradox,
        Xenoglossy,
        Thunder
    }

    private enum PlannerRouteKind
    {
        Standard,
        DTR,
        NearManafont,
        Amplifier,
        Paradox,
        ParadoxSkip,
        Polyglot,
        Thunder
    }

    private struct PlannerState
    {
        public int Element;
        public int MP;
        public int Hearts;
        public int Polyglot;
        public int AstralSoul;
        public bool Paradox;
        public bool Firestarter;
        public bool Thunderhead;
        public bool AllowThunder;
        public float ThunderLeft;
        public float NextPolyglot;
        public float ElementTimer;
        public int InstantBudget;
        public int ActiveInstantBudget;
        public float Time;
        public float Score;
        public float LeyLinesLeft;
        public float TransposeReadyIn;
        public float ManafontReadyIn;
        public float LucidReadyIn;
        public float LucidLeft;
        public float LucidTickIn;
        public float DowntimeIn;
        public float? UptimeIn;
        public float AmplifierReadyIn;
        public float RaidBuffsLeft;
        public float RaidBuffsIn;
        public bool ForcedMoveSoon;
        public bool PlannedInstantWeave;
        public bool UsedPolyglotRecently;
        public bool DTRStarted;
        public bool DTRComplete;
        public bool DTRFlareStarFirst;
        public bool AllowManafont;
        public bool AllowBurstActions;
        public bool ElementSwapHardcastTransitionActive;
        public bool StableElementSwapState;
        public bool ElementSwapHardcastSuppressionExcludedState;
        public PlannerRouteKind FirstKind;
        public PlannerStep FirstStep;
        public AID ReservedInstantAction;
        public AID UsedReservedInstantAction;
        public AID[] LockedRoute;
        public float Horizon; // common evaluation horizon (seconds from the search start) shared by every candidate
        // Set when the route reached the horizon, or ran out of legal successors before it (typically because the next
        // GCD no longer fits before the downtime the horizon is clamped to). Such a state is carried to the next beam
        // once, at its own evaluation, and is never expanded again - see FindBestPlannerState.
        public bool Finished;
        [System.Text.Json.Serialization.JsonIgnore]
        public PlannerRouteBuffer Route; // first steps of the route that produced this state, kept for the BLM_PLANNER_DEBUG dumps only
        public int RouteLength;
        public float WeaveSlack; // time left inside the current GCD for oGCDs; a weave that fits here costs no time

        public void RecordStep(PlannerStep step)
        {
            if (RouteLength < PlannerRouteCapacity)
                Route[RouteLength++] = step;
        }

        // Two candidates that reached the same resources at the same time are interchangeable for the rest of the
        // search; keeping both only spends beam slots on duplicates.
        public readonly bool SameState(in PlannerState other)
            => Element == other.Element && MP == other.MP && Hearts == other.Hearts && Polyglot == other.Polyglot && AstralSoul == other.AstralSoul
            && Paradox == other.Paradox && Firestarter == other.Firestarter && Thunderhead == other.Thunderhead
            && InstantBudget == other.InstantBudget && ActiveInstantBudget == other.ActiveInstantBudget
            && ReservedInstantAction == other.ReservedInstantAction && UsedReservedInstantAction == other.UsedReservedInstantAction
            && DTRStarted == other.DTRStarted && DTRComplete == other.DTRComplete && PlannedInstantWeave == other.PlannedInstantWeave
            && MathF.Abs(Time - other.Time) < 0.05f
            && MathF.Abs(ThunderLeft - other.ThunderLeft) < 0.5f
            && MathF.Abs(LeyLinesLeft - other.LeyLinesLeft) < 0.5f
            && MathF.Abs(TransposeReadyIn - other.TransposeReadyIn) < 0.5f
            && MathF.Abs(ManafontReadyIn - other.ManafontReadyIn) < 0.5f
            && MathF.Abs(LucidLeft - other.LucidLeft) < 0.5f
            && MathF.Abs(AmplifierReadyIn - other.AmplifierReadyIn) < 0.5f;
    }

    private readonly record struct PlannerCacheKey(
        int Element,
        int MP,
        int Hearts,
        int Polyglot,
        int AstralSoul,
        bool Paradox,
        bool Firestarter,
        bool Thunderhead,
        bool AllowThunder,
        int ThunderLeft,
        int NextPolyglot,
        int ElementTimer,
        int InstantBudget,
        int ActiveInstantBudget,
        int LeyLinesLeft,
        int TransposeReadyIn,
        int ManafontReadyIn,
        int LucidReadyIn,
        int LucidLeft,
        int LucidTickIn,
        int DowntimeIn,
        int UptimeIn,
        int AmplifierReadyIn,
        int RaidBuffsLeft,
        int RaidBuffsIn,
        bool ForcedMoveSoon,
        bool PlannedInstantWeave,
        bool UsedPolyglotRecently,
        bool DTRStarted,
        bool DTRComplete,
        bool DTRFlareStarFirst,
        bool AllowManafont,
        bool AllowBurstActions,
        bool ElementSwapHardcastTransitionActive,
        bool StableElementSwapState,
        bool ElementSwapHardcastSuppressionExcludedState,
        AID ReservedInstantAction,
        AID UsedReservedInstantAction,
        int PlayerLevel,
        int MaxHearts,
        int MaxPolyglot,
        int GCD,
        int GCDLength,
        bool CanWeaveAmplifier,
        bool MovementPriorityRequested,
        uint LastCastSequence,
        RotationStrategy Rotation);

    private const int PlannerRouteCapacity = 16;

    [System.Runtime.CompilerServices.InlineArray(PlannerRouteCapacity)]
    private struct PlannerRouteBuffer
    {
        private PlannerStep _element0;
    }

    private static readonly bool PlannerDebug = Environment.GetEnvironmentVariable("BLM_PLANNER_DEBUG") is { Length: > 0 };
    private static readonly (float From, float To) PlannerDebugWindow = ParsePlannerDebugWindow(Environment.GetEnvironmentVariable("BLM_PLANNER_DEBUG"));

    // Runs inside a static initializer in the game process, so a malformed BLM_PLANNER_DEBUG must not throw: anything that
    // is not "<from>-<to>" just means "no window".
    private static (float, float) ParsePlannerDebugWindow(string? value)
    {
        if (value == null || !value.Contains('-'))
            return (0, float.MaxValue);
        var parts = value.Split('-');
        return parts.Length == 2
            && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var from)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var to)
            ? (from, to)
            : (0, float.MaxValue);
    }

    private string DescribePlannerRoute(in PlannerState state)
    {
        var steps = new List<string>();
        for (var i = 0; i < state.RouteLength; ++i)
            steps.Add(state.Route[i].ToString());
        return string.Join(">", steps);
    }

    private readonly record struct PlannerFragment(PlannerRouteKind Kind, PlannerStep[] Steps);
    private static readonly PlannerStep[] AmplifierFragmentSteps = [PlannerStep.Amplifier];
    private static readonly PlannerStep[] XenoglossyFragmentSteps = [PlannerStep.Xenoglossy];
    private static readonly PlannerStep[] ThunderFragmentSteps = [PlannerStep.Thunder];
    private static readonly PlannerStep[] ParadoxFragmentSteps = [PlannerStep.Paradox];
    private static readonly PlannerStep[] Fire3FragmentSteps = [PlannerStep.Fire3];
    private static readonly PlannerStep[] Fire4FragmentSteps = [PlannerStep.Fire4];
    private static readonly PlannerStep[] FlareStarFragmentSteps = [PlannerStep.FlareStar];
    private static readonly PlannerStep[] DespairFragmentSteps = [PlannerStep.Despair];
    private static readonly PlannerStep[] Blizzard3FragmentSteps = [PlannerStep.Blizzard3];
    private static readonly PlannerStep[] Blizzard4FragmentSteps = [PlannerStep.Blizzard4];
    private readonly record struct TimelineContext(
        float DowntimeIn,
        float? UptimeIn,
        bool DowntimeSoon,
        bool DowntimeNow,
        bool ForcedMoveSoon,
        bool LeyLinesUnsafeSoon,
        float ForcedMoveIn,
        float LeyLinesUnsafeIn,
        float PlannerDowntimeIn);

    private bool FuturePlannerSelected(in Strategy strategy)
        => strategy.Rotation == RotationStrategy.FuturePlanner
        || ShouldUseWindurstThirdWalkProfile(strategy);

    private bool UseFuturePlanner(in Strategy strategy) => FuturePlannerSelected(strategy) && UseAbilities(strategy) && CanUseHighLevelPlanner() && !ShouldUseStandard57OpenerRoute(strategy);

    private void UseFuturePlanner(in Strategy strategy, Enemy primaryTarget)
    {
        if (!Player.InCombat || !CanUseHighLevelPlanner())
        {
            ClearPlannerLock();
            return;
        }

        if (UseAOERotation())
        {
            ClearPlannerLock();
            UseAOEFuturePlanner(strategy, primaryTarget);
            return;
        }

        if (TryPushPlannerLock(strategy, primaryTarget))
            return;

        if (TryPushUrgentPolyglot(strategy, primaryTarget, GCDPriority.ResourceCap))
            return;

        if (TryPushThunderRefresh(strategy, GCDPriority.FuturePlan))
            return;

        if (TryPushAFHoldForNearManafont(strategy, primaryTarget, GCDPriority.FuturePlan))
            return;

        var allowNewTriplecast = CanUseTriplecastForTarget(primaryTarget);
        var reuseKey = BuildPlannerReuseKey(strategy, allowNewTriplecast);
        PlannerState best;
        if (PlanReusable(STPlanReuseValid, STPlanReuseKey, reuseKey, STPlanReusedAt))
            best = STPlanReused;
        else
        {
            best = FindBestPlannerState(strategy, allowNewTriplecast: allowNewTriplecast);
            STPlanReused = best;
            STPlanReuseKey = reuseKey;
            STPlanReusedAt = World.CurrentTime;
            STPlanReuseValid = true;
        }
        if (best.FirstStep == PlannerStep.None)
            return;

        if ((best.FirstKind is PlannerRouteKind.DTR or PlannerRouteKind.NearManafont) && best.LockedRoute.Length > 1)
            SetPlannerLock(best.LockedRoute);

        PushPlannerStep(strategy, best.FirstStep, primaryTarget, best.UsedReservedInstantAction != AID.None ? best.UsedReservedInstantAction : best.ReservedInstantAction);
    }

    private PlannerState FindBestPlannerState(in Strategy strategy, float? leylinesLeftOverride = null, bool allowNewTriplecast = true)
    {
        var initial = CurrentPlannerState(strategy, allowNewTriplecast);
        if (leylinesLeftOverride != null)
            initial.LeyLinesLeft = leylinesLeftOverride.Value;
        PreparePlannerCache();
        var cacheKey = BuildPlannerCacheKey(initial, strategy);
        if (STPlannerCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var beam = STPlannerBeam;
        var next = STPlannerNextBeam;
        beam[0] = (initial, 0);
        var beamCount = 1;

        // Expand until every surviving candidate has reached the common horizon (or nothing can expand any more), so
        // the comparison is between full-length routes rather than between whatever the depth cap happened to allow.
        for (var depth = 0; depth < BLMPlannerTuning.STPlannerMaxDepth; ++depth)
        {
            var nextCount = 0;
            var expanded = false;
            for (var index = 0; index < beamCount; ++index)
            {
                var state = beam[index].State;
                if (state.Finished || state.Time >= state.Horizon)
                {
                    InsertPlannerState(next, ref nextCount, state, beam[index].Score);
                    continue;
                }

                var anySuccessor = false;
                foreach (var fragment in PlannerFragments(state))
                {
                    var candidate = state;
                    var valid = true;
                    foreach (var step in fragment.Steps)
                    {
                        if (!ApplyPlannerStep(ref candidate, step, fragment.Kind))
                        {
                            valid = false;
                            break;
                        }

                        if (candidate.FirstStep == PlannerStep.None)
                        {
                            candidate.FirstStep = step;
                            candidate.FirstKind = fragment.Kind;
                        }
                        candidate.RecordStep(step);
                    }

                    if (valid)
                    {
                        expanded = true;
                        anySuccessor = true;
                        if (fragment.Kind is PlannerRouteKind.DTR or PlannerRouteKind.NearManafont)
                            candidate.LockedRoute = PlannerStepsToActions(fragment.Steps, state.ReservedInstantAction);
                        InsertPlannerState(next, ref nextCount, candidate, PlannerEvaluation(candidate));
                    }
                }

                if (!anySuccessor)
                {
                    // A route with no legal move left before the horizon is finished, not refuted: the usual reason is
                    // that the next GCD no longer fits before the downtime the horizon is clamped to. Dropping it handed
                    // the plan to whatever still had a legal move, however much worse it evaluated - at Lv90 that deleted
                    // the leading Paradox route for the crime of having saved MP, and at Lv100 it deletes the beam head
                    // 172 times over the DMU timeline. PlannerEvaluation already prices "nothing more happens" correctly
                    // (remaining time to the horizon is credited at the filler rate), so carry it and let it compete.
                    // Terminates: the flagged copy is skipped by the guard above, so it yields no candidates, never sets
                    // `expanded`, and is re-inserted at most once per depth - one beam slot, no clones. When every
                    // survivor is finished, `expanded` stays false and the loop exits.
                    var finished = state;
                    finished.Finished = true;
                    InsertPlannerState(next, ref nextCount, finished, beam[index].Score);
                }
            }

            if (nextCount == 0)
                break;

            (beam, next) = (next, beam);
            beamCount = nextCount;
            if (PlannerDebug && depth < 16 && CombatTimer >= PlannerDebugWindow.From && CombatTimer <= PlannerDebugWindow.To)
                for (var index = 0; index < beamCount; ++index)
                    if (index == 0 || depth <= 1 || depth is >= 7 and <= 9 || beam[index].State.Route[0] == PlannerStep.Paradox && (index == 0 || beam[index - 1].State.Route[0] != PlannerStep.Paradox))
                    Console.WriteLine(FormattableString.Invariant($"    d{depth} #{index} eval={beam[index].Score:f1} time={beam[index].State.Time:f1} score={beam[index].State.Score:f0} terminal={TerminalStateValue(beam[index].State):f0} mp={beam[index].State.MP} fs={beam[index].State.Firestarter} route={DescribePlannerRoute(beam[index].State)}"));
            if (!expanded)
                break;
        }

        var best = beam[0].State;
        for (var index = 0; index < beamCount; ++index)
        {
            var candidate = beam[index].State;
            if (candidate.FirstStep != PlannerStep.None && candidate.Time >= BLMPlannerTuning.STPlannerMinUsefulTimeSeconds)
            {
                best = candidate;
                break;
            }
        }

        if (PlannerDebug && CombatTimer >= PlannerDebugWindow.From && CombatTimer <= PlannerDebugWindow.To)
        {
            Console.WriteLine(FormattableString.Invariant($"[blm-planner t={CombatTimer:f2}] el={initial.Element} mp={initial.MP} hearts={initial.Hearts} soul={initial.AstralSoul} poly={initial.Polyglot} para={initial.Paradox} fs={initial.Firestarter} th={initial.Thunderhead} thl={initial.ThunderLeft:f1} tp={initial.TransposeReadyIn:f1} mf={initial.ManafontReadyIn:f1} inst={initial.InstantBudget}/{initial.ActiveInstantBudget}/{initial.ReservedInstantAction} horizon={initial.Horizon:f1}"));
            Console.WriteLine("    offered=" + string.Join(",", PlannerFragments(initial).Select(f => f.Kind + ":" + string.Join(">", f.Steps))));
            for (var index = 0; index < beamCount; ++index)
            {
                var candidate = beam[index].State;
                Console.WriteLine(FormattableString.Invariant($"    #{index} eval={beam[index].Score:f1} time={candidate.Time:f1} score={candidate.Score:f0} terminal={TerminalStateValue(candidate):f0} el={candidate.Element} mp={candidate.MP} soul={candidate.AstralSoul} route={DescribePlannerRoute(candidate)}"));
            }
        }
        STPlannerCache[cacheKey] = best;
        return best;
    }

    private PlannerState CurrentPlannerState(in Strategy strategy, bool allowNewTriplecast = true)
    {
        var timeline = CurrentTimelineContext();
        return new()
        {
            Element = Element,
            MP = (int)MP,
            Hearts = Hearts,
            Polyglot = Polyglot,
            AstralSoul = AstralSoul,
            Paradox = Paradox,
            Firestarter = Firestarter,
            Thunderhead = Thunderhead,
            AllowThunder = PlannerAllowsThunder(strategy),
            ThunderLeft = TargetThunderLeft == float.MaxValue ? ThunderDurationFor(BestPlannerThunder()) : TargetThunderLeft,
            NextPolyglot = Math.Max(0, NextPolyglot),
            ElementTimer = Element == 0 ? 0 : ElementTimerLeft,
            InstantBudget = AllowAnyCastEnabler(strategy) ? PlannerInstantBudget(true, strategy, allowNewTriplecast) : PlannerActiveInstantBudget(true),
            ActiveInstantBudget = PlannerActiveInstantBudget(true),
            ReservedInstantAction = AllowAnyCastEnabler(strategy) ? PlannerPreferredInstantAction(true, strategy, allowNewTriplecast) : AID.None,
            LeyLinesLeft = InLeyLines ? Math.Max(0, StatusLeft(SID.CircleOfPower)) : 0,
            TransposeReadyIn = Unlocked(AID.Transpose) ? ReadyIn(AID.Transpose) : float.MaxValue,
            ManafontReadyIn = ReadyIn(AID.Manafont),
            LucidReadyIn = ReadyIn(AID.LucidDreaming),
            DowntimeIn = timeline.PlannerDowntimeIn,
            UptimeIn = timeline.UptimeIn,
            AmplifierReadyIn = PlannerAmplifierReadyIn(strategy),
            RaidBuffsLeft = Math.Max(0, RaidBuffsLeft),
            RaidBuffsIn = Math.Max(0, RaidBuffsIn),
            ForcedMoveSoon = timeline.ForcedMoveSoon,
            UsedPolyglotRecently = RecentlySpentPolyglot(),
            AllowManafont = ManafontForced(strategy) || ManafontAutomatic(strategy) && !ShouldHoldEvenBurstRecoveryAction(CurrentManafontRecast),
            AllowBurstActions = !ShouldHoldEvenBurstRecoveryAction(BLMGameConstants.AmplifierRecast),
            ElementSwapHardcastTransitionActive = ElementSwapHardcastTransitionActive,
            StableElementSwapState = StableElementSwapState,
            ElementSwapHardcastSuppressionExcludedState = ElementSwapHardcastSuppressionExcludedState,
            LockedRoute = [],
            Horizon = Math.Min(BLMPlannerTuning.STPlannerHorizonSeconds, timeline.PlannerDowntimeIn),
            WeaveSlack = Math.Max(0, GCD),
        };
    }

    private void PreparePlannerCache()
    {
        var now = World.CurrentTime;
        if (PlannerCacheFrameAt == now)
            return;

        var timeWentBackwards = PlannerCacheFrameAt != default && now < PlannerCacheFrameAt;
        PlannerCacheFrameAt = now;
        var preciseGCDWindow = GCD <= BLMPlannerTuning.PlannerCachePreciseGCDWindowSeconds;
        if (!timeWentBackwards && now < PlannerCacheExpiresAt)
            return;

        STPlannerCache.Clear();
        AOEPlannerCache.Clear();
        // Inside the precise window keep a short lifetime instead of re-running the beam search every frame.
        PlannerCacheExpiresAt = now.AddSeconds(preciseGCDWindow ? BLMPlannerTuning.PlannerCachePreciseLifetimeSeconds : BLMPlannerTuning.PlannerCacheLifetimeSeconds);
    }

    private static int PlannerCacheTimeBucket(float value)
    {
        if (float.IsNaN(value))
            return int.MinValue;
        if (value == float.MaxValue || float.IsPositiveInfinity(value))
            return int.MaxValue;
        if (float.IsNegativeInfinity(value))
            return int.MinValue + 1;

        var scaled = value / BLMPlannerTuning.PlannerCacheTimeQuantumSeconds;
        var bucket = value > 0 ? MathF.Ceiling(scaled) : MathF.Floor(scaled);
        return bucket >= int.MaxValue ? int.MaxValue - 1 : bucket <= int.MinValue + 2 ? int.MinValue + 2 : (int)bucket;
    }

    private static int PlannerCacheTimeBucket(float? value)
        => value is float present ? PlannerCacheTimeBucket(present) : int.MinValue + 3;

    private PlannerCacheKey BuildPlannerCacheKey(PlannerState state, in Strategy strategy)
        => new(
            state.Element,
            state.MP,
            state.Hearts,
            state.Polyglot,
            state.AstralSoul,
            state.Paradox,
            state.Firestarter,
            state.Thunderhead,
            state.AllowThunder,
            PlannerCacheTimeBucket(state.ThunderLeft),
            PlannerCacheTimeBucket(state.NextPolyglot),
            PlannerCacheTimeBucket(state.ElementTimer),
            state.InstantBudget,
            state.ActiveInstantBudget,
            PlannerCacheTimeBucket(state.LeyLinesLeft),
            PlannerCacheTimeBucket(state.TransposeReadyIn),
            PlannerCacheTimeBucket(state.ManafontReadyIn),
            PlannerCacheTimeBucket(state.LucidReadyIn),
            PlannerCacheTimeBucket(state.LucidLeft),
            PlannerCacheTimeBucket(state.LucidTickIn),
            PlannerCacheTimeBucket(state.DowntimeIn),
            PlannerCacheTimeBucket(state.UptimeIn),
            PlannerCacheTimeBucket(state.AmplifierReadyIn),
            PlannerCacheTimeBucket(state.RaidBuffsLeft),
            PlannerCacheTimeBucket(state.RaidBuffsIn),
            state.ForcedMoveSoon,
            state.PlannedInstantWeave,
            state.UsedPolyglotRecently,
            state.DTRStarted,
            state.DTRComplete,
            state.DTRFlareStarFirst,
            state.AllowManafont,
            state.AllowBurstActions,
            state.ElementSwapHardcastTransitionActive,
            state.StableElementSwapState,
            state.ElementSwapHardcastSuppressionExcludedState,
            state.ReservedInstantAction,
            state.UsedReservedInstantAction,
            Player.Level,
            MaxHearts,
            MaxPolyglot,
            PlannerCacheTimeBucket(GCD),
            PlannerCacheTimeBucket(GCDLength),
            Unlocked(AID.Amplifier) && CanWeave(AID.Amplifier),
            MovementSkillPriorityRequested(),
            LastObservedCastSequence,
            strategy.Rotation.Value);

    private static void InsertPlannerState((PlannerState State, float Score)[] selected, ref int count, PlannerState candidate, float score)
    {
        for (var index = 0; index < count; ++index)
        {
            if (!selected[index].State.SameState(candidate))
                continue;
            if (Comparer<float>.Default.Compare(selected[index].Score, score) >= 0)
                return;
            Array.Copy(selected, index + 1, selected, index, count - index - 1);
            --count;
            break;
        }
        InsertSortedPlannerState(selected, ref count, candidate, score);
    }

    private static void InsertPlannerState((AOEPlannerState State, float Score)[] selected, ref int count, AOEPlannerState candidate, float score)
    {
        for (var index = 0; index < count; ++index)
        {
            if (!selected[index].State.SameState(candidate))
                continue;
            if (Comparer<float>.Default.Compare(selected[index].Score, score) >= 0)
                return;
            Array.Copy(selected, index + 1, selected, index, count - index - 1);
            --count;
            break;
        }
        InsertSortedPlannerState(selected, ref count, candidate, score);
    }

    private static void InsertSortedPlannerState<T>((T State, float Score)[] selected, ref int count, T candidate, float score)
    {
        var insertAt = 0;
        // Keep the existing insertion order when scores tie, including NaN comparisons.
        while (insertAt < count && Comparer<float>.Default.Compare(selected[insertAt].Score, score) >= 0)
            ++insertAt;
        if (insertAt >= selected.Length)
            return;

        Array.Copy(selected, insertAt, selected, insertAt + 1, Math.Min(count, selected.Length - 1) - insertAt);
        selected[insertAt] = (candidate, score);
        if (count < selected.Length)
            ++count;
    }

    private static List<T> SelectBestPlannerStates<T>(List<T> candidates, int width, Func<T, float> evaluation)
    {
        var selected = new List<(T State, float Score)>(width);
        foreach (var candidate in candidates)
        {
            var score = evaluation(candidate);
            var insertAt = 0;
            while (insertAt < selected.Count && Comparer<float>.Default.Compare(selected[insertAt].Score, score) >= 0)
                ++insertAt;

            if (insertAt >= width)
                continue;

            selected.Insert(insertAt, (candidate, score));
            if (selected.Count > width)
                selected.RemoveAt(width);
        }

        var result = new List<T>(selected.Count);
        foreach (var candidate in selected)
            result.Add(candidate.State);
        return result;
    }

    private TimelineContext CurrentTimelineContext()
    {
        if (CachedTimelineContextAt == World.CurrentTime && CachedTimelineContextStrategy == CurrentExternalHintStrategy && CachedTimelineContextOpenerScript == Standard57ScriptHidesUpcomingLoss)
            return CachedTimelineContext;

        CachedTimelineContext = BuildTimelineContext();
        CachedTimelineContextAt = World.CurrentTime;
        CachedTimelineContextStrategy = CurrentExternalHintStrategy;
        CachedTimelineContextOpenerScript = Standard57ScriptHidesUpcomingLoss;
        return CachedTimelineContext;
    }

    private TimelineContext BuildTimelineContext()
    {
        var downtimeIn = float.IsNaN(DowntimeIn) || DowntimeIn < 0 ? float.MaxValue : DowntimeIn;
        float? uptimeIn = UptimeIn is float up && !float.IsNaN(up) && up >= 0 ? up : null;
        var forcedMoveIn = float.MaxValue;
        var leyLinesUnsafeIn = float.MaxValue;
        var plannerDowntimeIn = downtimeIn;
        if (UseExternalMechanicHints())
        {
            var snap = Mechanic.Snapshot;
            var extLoss = Mechanic.HasSnapshot ? snap.TargetLossIn : float.MaxValue;
            var extReturn = Mechanic.HasSnapshot ? snap.TargetReturnIn : float.MaxValue;
            var extMove = Mechanic.HasSnapshot ? snap.ForcedMoveIn : float.MaxValue;
            var extLeyLines = Mechanic.HasSnapshot ? snap.LeyLinesUnsafeIn : float.MaxValue;
            // the boss module's own timeline, merged the way the removed PushBossModuleTimelineHints published it: in combat only,
            // loss/return within 30 s, forced move within 10 s, Ley Lines unsafe within 15 s (SplatoonHintSource push windows)
            var inCombat = Player.InCombat;
            var stateLoss = inCombat && Mechanic.StateLossIn <= 30 ? Mechanic.StateLossIn : float.MaxValue;
            var stateReturn = inCombat && Mechanic.StateReturnIn <= 30 ? Mechanic.StateReturnIn : float.MaxValue;
            var stateMove = inCombat && Mechanic.StateMoveIn <= 10 ? Mechanic.StateMoveIn : float.MaxValue;
            var statePositioning = inCombat && Mechanic.StatePositioningIn <= 15 ? Mechanic.StatePositioningIn : float.MaxValue;
            // The planners treat a loss as a point no cast may cross, which holds for a loss of any length (a cast running into it
            // is interrupted), so they keep every one: hiding the short ones from FuturePlanner cost it 0.04% on the combat
            // matrix. The rules read DowntimeIn as "the fight pauses" and only see what DropUpcomingLoss leaves.
            var anyLoss = Math.Min(extLoss, stateLoss);
            if (ValidHintTime(anyLoss))
                plannerDowntimeIn = Math.Min(plannerDowntimeIn, anyLoss);
            var hideStartingWithin = Standard57ScriptHidesUpcomingLoss ? BLMTuning.Standard57OpenerWindow - CombatTimer : 0;
            // The planner's DowntimeIn is the same state-machine loss, unfiltered: when the state machine says the coming loss is short (or
            // hidden by the opener script), the rules must not read it as the fight pausing either, or a jump stalls the rotation on return.
            if (downtimeIn > 0 && ValidHintTime(downtimeIn) && ValidHintTime(Mechanic.StateLossIn) && Math.Abs(Mechanic.StateLossIn - downtimeIn) < 0.1f)
            {
                var plannerLoss = Mechanic.StateLossIn;
                var plannerReturn = Mechanic.StateReturnIn;
                DropUpcomingLoss(ref plannerLoss, ref plannerReturn, hideStartingWithin);
                if (!ValidHintTime(plannerLoss))
                    downtimeIn = float.MaxValue;
            }
            DropUpcomingLoss(ref extLoss, ref extReturn, hideStartingWithin);
            DropUpcomingLoss(ref stateLoss, ref stateReturn, hideStartingWithin);
            var loss = Math.Min(extLoss, stateLoss);
            var ret = Math.Min(extReturn, stateReturn);
            if (ValidHintTime(loss))
                downtimeIn = Math.Min(downtimeIn, loss);
            if (ValidHintTime(ret))
                uptimeIn = uptimeIn == null ? ret : Math.Min(uptimeIn.Value, ret);
            var move = Math.Min(extMove, stateMove);
            if (ValidHintTime(move))
                forcedMoveIn = move;
            var leyLines = Math.Min(extLeyLines, statePositioning);
            if (ValidHintTime(leyLines))
                leyLinesUnsafeIn = leyLines;
        }

        var forcedMoveSoon = forcedMoveIn <= BLMTuning.ForcedMoveSoonSeconds;
        var leyLinesUnsafeSoon = leyLinesUnsafeIn <= BLMTuning.LeyLinesUnsafeSoonSeconds;
        return new(
            downtimeIn,
            uptimeIn,
            downtimeIn <= BLMTuning.DowntimeSoonSeconds,
            downtimeIn <= 0 && uptimeIn > 0,
            forcedMoveSoon,
            leyLinesUnsafeSoon,
            forcedMoveIn,
            leyLinesUnsafeIn,
            plannerDowntimeIn);
    }

    private static bool ShouldHoldNewLeyLinesForMovement(TimelineContext timeline)
    {
        if (timeline.DowntimeNow || timeline.DowntimeIn <= BLMTuning.LeyLinesMinUsefulUptime)
            return true;

        if (timeline.LeyLinesUnsafeIn <= BLMTuning.LeyLinesMinUsefulUptime)
            return true;

        if (timeline.ForcedMoveIn <= BLMTuning.LeyLinesForcedMoveHoldWindow)
            return true;

        return false;
    }

    // Per-category action locks (ActionLocks, shared with the other modules): Pacification (6) refuses weaponskills, Silence (7)
    // spells, Amnesia (1092) abilities. The game refuses the locked category after the queue has chosen it, so a locked Transpose /
    // Triplecast / Manafont at the top of the queue is resubmitted every frame while the Blizzard III and Xenoglossy behind it never
    // run (irregular harness, Amnesia: 124 refused Transposes, 5 s of GCD idle). Every push goes through PushAction -> CanUse, so
    // filtering there keeps the queue to what the client will accept and the rest of the rotation falls back on its own.
    protected override bool CanUse(AID action) => !IsActionLocked(action);

    private bool ShouldSuppressDowntimeTransposeForShortTargetLoss()
    {
        if (NoPriorityTargetSince == default)
            return false;

        if (ShortTargetLossGraceActive())
            return true;

        var timeline = CurrentTimelineContext();
        return timeline.UptimeIn is float uptimeIn && uptimeIn <= BLMTuning.ShortTargetReturnTransposeSuppress;
    }

    // The first moments without a target may be a target swap rather than a loss.
    private bool ShortTargetLossGraceActive()
        => NoPriorityTargetSince != default
        && (float)(World.CurrentTime - NoPriorityTargetSince).TotalSeconds < BLMTuning.ShortTargetLossTransposeGrace;

    // Pushes synthetic "forced move in 3s / Ley Lines unsafe in 8s" hints every second for testing the hint plumbing. They go
    // into the process-wide provider that every job's rotation reads, so this is a developer switch (BLM_EXTERNAL_HINT_TEST=1)
    // rather than a preset track one click away from degrading every rotation.
    private static readonly bool ExternalHintTest = Environment.GetEnvironmentVariable("BLM_EXTERNAL_HINT_TEST") == "1";

    // the lambda only captures this, so one instance serves every frame (it was allocated anew each frame)
    private PositionCheck? _aoeThunderCheck;
    private PositionCheck AOEThunderCheck => _aoeThunderCheck ??= (primary, other) => DotExpiring(GetAOETargetThunderLeft(other)) && TargetInAOECircle(other, primary.Position, BLMTuning.SplashRadius);

    // every frame in combat: a plain loop over the actor table (LINQ with a capturing lambda allocated twice per frame)
    private Actor? FindOwnLeyLines()
    {
        foreach (var actor in World.Actors.Actors.Values)
            if (actor.OID == BLMGameConstants.LeyLinesGroundOID && actor.OwnerID == Player.InstanceID)
                return actor;
        return null;
    }

    private void PushExternalHintTest()
    {
        if (!ExternalHintTest)
            return;

        if (World.CurrentTime < NextExternalHintTestPushAt)
            return;

        NextExternalHintTestPushAt = World.FutureTime(BLMTuning.ExternalHintTestPushInterval);
        var pushed = SplatoonHintBridge.PushCombatTimeHints(
            territoryId: World.CurrentZone,
            contentId: World.CurrentCFCID,
            now: World.CurrentTime,
            combatTimer: CombatTimer,
            timelineStable: false,
            moveCombatTime: CombatTimer + 3f,
            leyLinesUnsafeCombatTime: CombatTimer + 8f);

        if (World.CurrentTime < NextExternalHintTestLogAt)
            return;

        NextExternalHintTestLogAt = World.CurrentTime.AddSeconds(1);
        var timeline = CurrentTimelineContext();
        Service.Log($"[BLM][ExternalHintTest] pushed={pushed} external={CurrentExternalHintStrategy} forcedMoveSoon={timeline.ForcedMoveSoon} leyLinesUnsafeSoon={timeline.LeyLinesUnsafeSoon}");
    }

    private bool UseExternalMechanicHints() => Mechanic.Enabled;

    private static bool ValidHintTime(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value != float.MaxValue;

    // Only a loss of MechanicForecast.LongLossSeconds or more is a downtime, the same cut the forecast and the timeline and
    // disengage publishers apply. A shorter one (a jump, a two-second untargetable dodge) leaves Astral Fire, MP and Polyglot
    // as they were, but read as a downtime it holds Manafont, dumps Polyglot and ends the opener; back-to-back blips
    // (occult_crescent_south_horn: 1.9 s every 5.2 s) keep DowntimeIn under two GCDs until Astral Fire runs dry and the
    // rotation drops to ice. Splatoon and IPC snapshots pass every loss through, so they are filtered here. Losses starting
    // within hideStartingWithin are dropped whatever their length (the Standard 5+7 opener, see
    // UpdateStandard57ScriptHidesUpcomingLoss). A loss already running (loss 0) always keeps its return time: its length is
    // unknown and the downtime branch needs it.
    private static void DropUpcomingLoss(ref float loss, ref float ret, float hideStartingWithin)
    {
        if (loss <= 0 || !ValidHintTime(loss))
            return;
        if (loss < hideStartingWithin || ValidHintTime(ret) && ret - loss < MechanicForecast.LongLossSeconds)
            loss = ret = float.MaxValue;
    }

    private static readonly PlannerStep[] ReserveFire3FragmentSteps = [PlannerStep.ReserveInstant, PlannerStep.Fire3];
    private static readonly PlannerStep[] ReserveBlizzard3FragmentSteps = [PlannerStep.ReserveInstant, PlannerStep.Blizzard3];

    private static readonly PlannerStep[] TransposeFragmentSteps = [PlannerStep.Transpose];
    private static readonly PlannerStep[] ManafontFragmentSteps = [PlannerStep.Manafont];
    private static readonly PlannerStep[] LucidDreamingFragmentSteps = [PlannerStep.LucidDreaming];

    // Candidate generation keeps the rotation's rule gates (which action families are eligible in which phase state);
    // the horizon evaluation decides between the eligible candidates. An unfenced legal-action generator was tried on
    // 2026-09-19 and lost 0.5-1.4% on the combat matrix (Transpose loops, early Despair, wasted instants), so the gates stay.
    private IEnumerable<PlannerFragment> PlannerFragments(PlannerState state)
    {
        var env = Env;
        var canPlanFire3 = env.HasFire3 && !PlannerWouldHardcastElementSwap(AID.Fire3, state);
        var canPlanBlizzard3 = env.HasBlizzard3 && !PlannerWouldHardcastElementSwap(AID.Blizzard3, state);
        var canReserveInstant = state.InstantBudget > 0 && state.ReservedInstantAction != AID.None;

        if (ShouldPlanAmplifier(state))
            yield return new(PlannerRouteKind.Amplifier, AmplifierFragmentSteps);

        if (CanPlanNearManafontBridge(state))
            yield return new(PlannerRouteKind.NearManafont, NearManafontBridgeSteps(state));

        if (CanPlanDTR(state))
        {
            if (state.ReservedInstantAction != AID.None)
                yield return new(PlannerRouteKind.DTR, DTRRouteSteps(state, reserveInstant: true));

            if (state.ActiveInstantBudget >= 2)
                yield return new(PlannerRouteKind.DTR, DTRRouteSteps(state, reserveInstant: false));
        }

        if (env.HasXenoglossy && state.Polyglot > 0 && ShouldPlanXenoglossy(state))
            yield return new(PlannerRouteKind.Polyglot, XenoglossyFragmentSteps);

        if (state.Thunderhead && ShouldPlanThunder(state))
            yield return new(PlannerRouteKind.Thunder, ThunderFragmentSteps);

        if (env.HasParadox && state.Paradox && ShouldPlanParadox(state))
            yield return new(PlannerRouteKind.Paradox, ParadoxFragmentSteps);

        if (state.Element > 0)
        {
            if (state.Element < 3)
            {
                if ((state.Firestarter || state.InstantBudget > 0 || state.MP >= PlannerManaCost(AID.Fire3, state)) && canPlanFire3)
                    yield return new(PlannerRouteKind.Standard, Fire3FragmentSteps);
                else if (env.HasFire3 && canReserveInstant)
                    yield return new(PlannerRouteKind.Standard, ReserveFire3FragmentSteps);
            }
            else
            {
                if (env.HasFlareStar && state.AstralSoul == 6)
                    yield return new(PlannerRouteKind.Standard, FlareStarFragmentSteps);

                var fireCost = FireSpellCostFor(state);
                var despairBeforeFire4 = ShouldPlanDespairBeforeFire4(state);

                if (state.AstralSoul < 6 && CanPlanDespairFinisher(state) && (state.MP < fireCost || despairBeforeFire4))
                    yield return new(PlannerRouteKind.Standard, DespairFragmentSteps);

                if (env.HasFire4 && state.MP >= fireCost && state.AstralSoul < 6 && !despairBeforeFire4)
                    yield return new(state.Paradox ? PlannerRouteKind.ParadoxSkip : PlannerRouteKind.Standard, Fire4FragmentSteps);

                if (ShouldPlanIceSwapFromAF(state) && canPlanBlizzard3)
                    yield return new(PlannerRouteKind.Standard, Blizzard3FragmentSteps);
                else if (env.HasBlizzard3 && ShouldPlanIceSwapFromAF(state) && canReserveInstant)
                    yield return new(PlannerRouteKind.Standard, ReserveBlizzard3FragmentSteps);
            }
        }
        else if (state.Element < 0)
        {
            if (state.Element > -3 && canPlanBlizzard3)
                yield return new(PlannerRouteKind.Standard, Blizzard3FragmentSteps);
            else if (env.HasBlizzard3 && state.Element > -3 && canReserveInstant)
                yield return new(PlannerRouteKind.Standard, ReserveBlizzard3FragmentSteps);
            else if (env.HasBlizzard4 && state.Hearts < env.MaxHearts)
                yield return new(PlannerRouteKind.Standard, Blizzard4FragmentSteps);
            else if (env.HasParadox && state.Paradox && ShouldPlanParadox(state))
                yield return new(PlannerRouteKind.Paradox, ParadoxFragmentSteps);
            else if ((state.MP >= BLMGameConstants.FullIceMP || state.Firestarter || state.InstantBudget > 0) && canPlanFire3)
                yield return new(PlannerRouteKind.Standard, Fire3FragmentSteps);
            else if (env.HasFire3 && canReserveInstant)
                yield return new(PlannerRouteKind.Standard, ReserveFire3FragmentSteps);
        }
        else
        {
            if (canPlanFire3)
                yield return new(PlannerRouteKind.Standard, Fire3FragmentSteps);
            else if (env.HasFire3 && canReserveInstant)
                yield return new(PlannerRouteKind.Standard, ReserveFire3FragmentSteps);
        }
    }

    private bool ApplyPlannerStep(ref PlannerState state, PlannerStep step, PlannerRouteKind kind)
    {
        var aid = PlannerStepAction(step, state);
        if (aid == AID.None && step != PlannerStep.Transpose)
            return false;
        if (IsThunderAction(aid) && (!state.AllowThunder || !state.Thunderhead))
            return false;
        var startsDTRWithFlareStar = kind == PlannerRouteKind.DTR && !state.DTRStarted && aid == AID.FlareStar;

        if (step == PlannerStep.ReserveInstant)
        {
            var stacks = PlannerInstantStacks(state.ReservedInstantAction);
            if (stacks <= 0)
                return false;
            if (!PlannerWeave(ref state))
                return false;
            state.Score += BLMPlannerWeights.ReserveInstantScore;
            state.PlannedInstantWeave = true;
            state.ActiveInstantBudget += stacks;
            state.UsedReservedInstantAction = state.ReservedInstantAction;
            state.ReservedInstantAction = AID.None;
            return true;
        }

        if (step == PlannerStep.Transpose)
        {
            if (state.TransposeReadyIn > 0)
                return false;

            var nextElement = state.Element > 0 ? -1 : state.Element < 0 ? 1 : 0;
            if (nextElement == 0)
                return false;

            if (!PlannerWeave(ref state))
                return false;
            state.Score += kind == PlannerRouteKind.DTR ? BLMPlannerWeights.DTRTransposeScore : 0;
            ApplyPlannerElementTransition(ref state, nextElement, fromTranspose: true);
            state.TransposeReadyIn = BLMGameConstants.TransposeRecast;
            return true;
        }

        if (aid == AID.Amplifier)
        {
            if (!ShouldPlanAmplifier(state))
                return false;

            if (!PlannerWeave(ref state))
                return false;
            state.Polyglot = Math.Min(Env.MaxPolyglot, state.Polyglot + 1);
            state.AmplifierReadyIn = BLMGameConstants.AmplifierRecast;
            state.Score += BLMPlannerWeights.AmplifierScore;
            return true;
        }

        if (aid == AID.LucidDreaming)
        {
            if (state.LucidReadyIn > 0)
                return false;

            if (!PlannerWeave(ref state))
                return false;
            state.LucidReadyIn = BLMGameConstants.LucidDreamingRecast;
            state.LucidLeft = BLMGameConstants.LucidDreamingDuration;
            state.LucidTickIn = BLMGameConstants.LucidDreamingTickInterval;
            state.Score += BLMPlannerWeights.LucidDreamingScore;
            return true;
        }

        if (aid == AID.Manafont)
        {
            if (!state.AllowManafont || state.ManafontReadyIn > 0 || state.Element <= 0)
                return false;

            if (!PlannerWeave(ref state))
                return false;
            ApplyPlannerElementTransition(ref state, 3, fromTranspose: false);
            ref readonly var manafontEnv = ref Env;
            state.MP = BLMGameConstants.MaxMP;
            state.Hearts = manafontEnv.MaxHearts;
            state.Thunderhead = true;
            state.Paradox = manafontEnv.HasParadox;
            state.ManafontReadyIn = manafontEnv.ManafontRecast;
            state.Score += BLMPlannerWeights.ManafontScore;
            return true;
        }

        var cost = PlannerManaCost(aid, state);
        if (state.MP < cost)
            return false;

        state.MP -= cost;
        var usesReservedInstant = UsesReservedInstant(aid, state);
        var effective = EffectivePotency(aid, state);
        var elapsed = PlannerActionTime(aid, state, usesReservedInstant);
        state.WeaveSlack = PlannerSlackAfter(elapsed, usesReservedInstant ? 0 : PlannerCastTime(aid, state));
        state.Time += elapsed;
        if (!AdvancePlannerTimers(ref state, elapsed))
            return false;
        state.Score += effective;
        if (usesReservedInstant)
        {
            state.InstantBudget--;
            if (state.ActiveInstantBudget > 0)
                state.ActiveInstantBudget--;
        }

        var usedPolyglot = aid is AID.Xenoglossy or AID.Foul;
        if (usedPolyglot)
            state.UsedPolyglotRecently = true;

        if (usedPolyglot)
            state.Polyglot = Math.Max(0, state.Polyglot - 1);
        else if (IsThunderAction(aid))
        {
            state.Thunderhead = false;
            state.ThunderLeft = ThunderDurationFor(aid);
        }
        else if (aid == AID.Paradox)
        {
            state.Paradox = false;
            if (state.Element > 0)
                state.Firestarter = true;
            RefreshPlannerElementTimer(ref state, aid);
        }
        else if (aid == AID.Fire3)
        {
            // A paid Fire III under Astral Fire consumes an Umbral Heart like any other fire spell (a Firestarter proc
            // costs nothing and keeps the heart). Without this the model funded one Fire IV more than the bar allows.
            if (state.Element > 0 && cost > 0 && state.Hearts > 0)
                state.Hearts--;
            ApplyPlannerElementTransition(ref state, 3, fromTranspose: false);
            state.Firestarter = false;
            state.DTRComplete |= kind == PlannerRouteKind.DTR;
        }
        else if (aid == AID.Blizzard3)
        {
            // Umbral Ice III restores 10000 MP on landing an ice spell; Blizzard III grants UI3 first, so it fills the bar.
            ApplyPlannerElementTransition(ref state, -3, fromTranspose: false);
            state.MP = BLMGameConstants.MaxMP;
        }
        else if (aid == AID.Blizzard4)
        {
            state.Hearts = Env.MaxHearts;
            state.MP = Math.Min(BLMGameConstants.MaxMP, state.MP + (state.Element switch { -1 => 2500, -2 => 5000, _ => BLMGameConstants.MaxMP }));
            RefreshPlannerElementTimer(ref state, aid);
        }
        else if (aid == AID.Fire4)
        {
            // Astral Soul only exists once Flare Star is unlocked; below that the gauge reads 0 forever. Simulating it
            // anyway walked the model into AstralSoul == 6 states whose whole Element == 3 branch (Flare Star, Despair,
            // Fire IV, the ice swap) is closed, and the horizon beam deletes a candidate that cannot expand.
            if (Env.HasFlareStar)
                state.AstralSoul = Math.Min(6, state.AstralSoul + 1);
            if (state.Hearts > 0)
                state.Hearts--;
        }
        else if (aid == AID.Despair)
        {
            // Despair does grant Astral Fire III, but only after its own damage: a bridge Despair cast at AF1 after a
            // Transpose is scored at the AF1 multiplier (EffectivePotency uses the pre-cast element) and leaves AF3 behind.
            ApplyPlannerElementTransition(ref state, 3, fromTranspose: false);
            state.MP = 0;
            RefreshPlannerElementTimer(ref state, aid);
        }
        else if (aid == AID.FlareStar)
        {
            state.AstralSoul = 0;
        }

        if (kind == PlannerRouteKind.DTR)
        {
            state.DTRFlareStarFirst |= startsDTRWithFlareStar;
            state.DTRStarted = true;
        }

        return true;
    }

    private bool AdvancePlannerTimers(ref PlannerState state, float elapsed)
    {
        if (state.Polyglot >= Env.MaxPolyglot && Env.MaxPolyglot > 0)
            state.Score -= elapsed * BLMPlannerWeights.PolyglotCapHoldPenaltyPerSecond;
        state.ThunderLeft = Math.Max(0, state.ThunderLeft - elapsed);
        state.LeyLinesLeft = Math.Max(0, state.LeyLinesLeft - elapsed);
        state.TransposeReadyIn = Math.Max(0, state.TransposeReadyIn - elapsed);
        state.AmplifierReadyIn = Math.Max(0, state.AmplifierReadyIn - elapsed);
        state.ManafontReadyIn = Math.Max(0, state.ManafontReadyIn - elapsed);
        state.LucidReadyIn = Math.Max(0, state.LucidReadyIn - elapsed);
        AdvancePlannerLucid(ref state, elapsed);
        AdvancePlannerRaidBuffs(ref state.RaidBuffsLeft, ref state.RaidBuffsIn, elapsed);
        if (state.UptimeIn != null)
            state.UptimeIn = Math.Max(0, state.UptimeIn.Value - elapsed);

        if (state.DowntimeIn != float.MaxValue)
        {
            state.DowntimeIn -= elapsed;
            if (state.DowntimeIn <= 0)
                return false;
        }

        if (state.Element != 0)
        {
            state.ElementTimer -= elapsed;
            if (state.ElementTimer <= 0)
                return false;
        }

        if (state.Element != 0)
        {
            state.NextPolyglot -= elapsed;
            var maxPolyglot = Env.MaxPolyglot;
            while (state.NextPolyglot <= 0)
            {
                if (state.Polyglot < maxPolyglot)
                    state.Polyglot++;
                else
                    state.Score -= BLMPlannerWeights.PolyglotOvercapPenalty;
                state.NextPolyglot += BLMGameConstants.PolyglotInterval;
            }
        }
        return true;
    }

    private static void AdvancePlannerRaidBuffs(ref float raidBuffsLeft, ref float raidBuffsIn, float elapsed)
    {
        if (raidBuffsLeft > 0)
        {
            raidBuffsLeft = Math.Max(0, raidBuffsLeft - elapsed);
            return;
        }

        raidBuffsIn = Math.Max(0, raidBuffsIn - elapsed);
        if (raidBuffsIn <= 0)
            raidBuffsLeft = BLMTuning.RaidBuffWindowSeconds;
    }

    private static void AdvancePlannerLucid(ref PlannerState state, float elapsed)
    {
        while (state.LucidLeft > 0 && elapsed > 0)
        {
            var step = Math.Min(elapsed, state.LucidTickIn);
            elapsed -= step;
            state.LucidLeft = Math.Max(0, state.LucidLeft - step);
            state.LucidTickIn -= step;

            if (state.LucidTickIn <= 0 && state.LucidLeft > 0)
            {
                if (state.Element < 0)
                    state.MP = Math.Min(BLMGameConstants.MaxMP, state.MP + BLMGameConstants.LucidDreamingMPTick);
                state.LucidTickIn = BLMGameConstants.LucidDreamingTickInterval;
            }
        }
    }

    // Every candidate is scored as "potency achieved by the common horizon": executed potency, plus the time left until
    // the horizon filled at the steady-state filler rate, plus the value of resources still held. Dividing by the horizon
    // keeps the result in potency-per-minute so the existing decision margins keep their meaning. Comparing candidates at
    // the same horizon (instead of per second of their own length) stops the search from favouring whichever route
    // happened to bank more potency early, and stops routes cut short by a downtime from being divided by a fixed floor.
    private float PlannerEvaluation(PlannerState state)
    {
        ref readonly var env = ref Env;
        var horizon = Math.Max(BLMPlannerTuning.PlannerEvaluationMinDuration, state.Horizon);
        // Signed: a route whose last action overshoots the horizon pays for the overshoot at the filler rate, otherwise the
        // boundary hands whichever route happens to start one more action before the cut a free GCD (a Flare Star, typically).
        var remaining = state.Horizon - state.Time;
        return (state.Score + remaining * env.FillerPPS + TerminalStateValue(state)) / horizon * 60;
    }

    private float TerminalStateValue(PlannerState state)
    {
        ref readonly var env = ref Env;
        var value = 0f;
        var carryScale = state.DowntimeIn < BLMPlannerWeights.STShortDowntimeThreshold ? BLMPlannerWeights.STShortDowntimeCarryScale : state.DowntimeIn < BLMPlannerWeights.STMediumDowntimeThreshold ? BLMPlannerWeights.STMediumDowntimeCarryScale : BLMPlannerWeights.CarryScaleFull;
        // Resource values are derived from the potency model: a held Polyglot is one Xenoglossy minus the filler GCD it
        // displaces, an Astral Soul stack is a sixth of a Flare Star, a Thunder second is its tick rate, Ley Lines time is
        // the 15% of filler it buys, MP is the fraction of a fire phase it still funds.
        value += state.AstralSoul * env.AstralSoulValue * carryScale;
        value += state.Polyglot * env.PolyglotValue * carryScale;
        value += state.Firestarter ? env.FirestarterValue : 0;
        value += state.Paradox ? BLMPlannerWeights.ParadoxTerminalValue : 0;
        value += state.InstantBudget * BLMPlannerWeights.InstantBudgetTerminalValue;
        value += Math.Min(state.ElementTimer, BLMPlannerWeights.ElementTimerTerminalCap) * (state.Element == 0 ? 0 : BLMPlannerWeights.ElementTimerTerminalValue);
        // Only the DoT time left AT THE HORIZON belongs here: ThunderPlannerPotency already banked every tick up to the
        // horizon at cast time. A route that stopped short still has its tail ticks counted there, and one that overshot
        // had them cut there, so shift ThunderLeft back to the horizon before crediting it.
        // Guard the no-DoT case: terminal states normally sit just PAST the horizon, so `Horizon - Time` is negative
        // and the subtraction turns into an addition. That is right for a DoT still running (it was running at the
        // horizon too), but with ThunderLeft == 0 it would invent up to one GCD of DoT that expired long ago and
        // quietly pay routes for overshooting.
        var thunderPastHorizon = state.ThunderLeft <= 0 ? 0 : Math.Max(0, state.ThunderLeft - (state.Horizon - state.Time));
        value += Math.Min(thunderPastHorizon, BLMPlannerWeights.ThunderTimerTerminalCap) * env.ThunderTickPPS;
        value += state.MP / (float)BLMGameConstants.MaxMP * BLMPlannerWeights.MPTerminalValue;
        value += state.Element == 3 ? BLMPlannerWeights.FireElementTerminalValue : state.Element == -3 ? BLMPlannerWeights.IceElementTerminalValue : 0;
        value += state.LeyLinesLeft * env.FillerPPS * (1 - BLMGameConstants.LeyLinesGCDMultiplier);
        value += Math.Clamp(BLMGameConstants.AmplifierRecast - state.AmplifierReadyIn, 0, BLMGameConstants.AmplifierRecast) / BLMGameConstants.AmplifierRecast * BLMPlannerWeights.AmplifierCooldownProgressValue;
        if (state.AmplifierReadyIn <= 0 && state.Polyglot < Env.MaxPolyglot && !WouldAmplifierCauseNearOvercap(state.Polyglot, state.NextPolyglot))
            value -= BLMPlannerWeights.ReadyAmplifierPenalty;
        if (state.RaidBuffsLeft > 0)
            value += Math.Min(state.RaidBuffsLeft, BLMTuning.RaidBuffWindowSeconds) * (state.Polyglot * BLMPlannerWeights.RaidBuffPolyglotValue + state.AstralSoul * BLMPlannerWeights.RaidBuffAstralSoulValue);
        else if (state.RaidBuffsIn <= BLMTuning.RaidBuffWindowSeconds)
            value += (BLMTuning.RaidBuffWindowSeconds - state.RaidBuffsIn) * state.Polyglot * BLMPlannerWeights.UpcomingRaidBuffPolyglotValue;
        value += state.DTRComplete ? BLMPlannerWeights.DTRCompleteValue : state.DTRStarted ? BLMPlannerWeights.DTRStartedValue : 0;
        if (state.DowntimeIn < BLMPlannerWeights.DowntimePenaltyThreshold)
            value -= state.Element > 0 ? BLMPlannerWeights.FireDowntimePenalty : BLMPlannerWeights.IceDowntimePenalty;
        return value;
    }

    private bool ShouldPlanAmplifier(PlannerState state)
    {
        ref readonly var env = ref Env;
        return env.HasAmplifier
            && state.AllowBurstActions
            && state.AmplifierReadyIn <= 0
            && state.Polyglot < env.MaxPolyglot
            && state.DowntimeIn > BLMTuning.PlannerOGCDMinDowntime
            && (state.Time > 0 || env.CanWeaveAmplifier)
            && !WouldAmplifierCauseNearOvercap(state.Polyglot, state.NextPolyglot);
    }

    private static readonly PlannerStep[] DTRComparisonStandardSteps = [PlannerStep.Fire4, PlannerStep.Fire4, PlannerStep.Despair, PlannerStep.Blizzard3, PlannerStep.Blizzard4, PlannerStep.Fire3];

    private bool CanPlanDTR(PlannerState state)
    {
        ref readonly var env = ref Env;
        if (!state.AllowBurstActions || state.Element <= 0 || state.MP < 800 || state.AstralSoul != 6 || !state.Firestarter || state.InstantBudget < 2 || !env.HasBlizzard4 || !env.HasDespair)
            return false;

        if (state.AllowManafont && state.ManafontReadyIn <= BLMTuning.NearManafontReadyMax)
            return false;

        var afEnding = state.AstralSoul >= 4 || state.MP < FireSpellCostFor(state) * 2;
        if (!afEnding || state.TransposeReadyIn > env.GCD)
            return false;

        var dtr = state.ReservedInstantAction != AID.None
            ? ScoreRoute(state, DTRRouteSteps(state, reserveInstant: true), PlannerRouteKind.DTR)
            : float.MinValue;
        if (state.ActiveInstantBudget >= 2)
            dtr = Math.Max(dtr, ScoreRoute(state, DTRRouteSteps(state, reserveInstant: false), PlannerRouteKind.DTR));

        var standard = ScoreRoute(state, DTRComparisonStandardSteps, PlannerRouteKind.Standard);
        return dtr > standard + BLMPlannerTuning.DTRRouteWinMargin;
    }

    private bool CanPlanNearManafontBridge(PlannerState state)
    {
        ref readonly var env = ref Env;
        if (!env.HighLevelPlanner)
            return false;

        if (!env.HasDespair || !env.HasParadox || !state.AllowManafont || state.Element != 3 || state.MP < 800)
            return false;

        if (state.ManafontReadyIn is < BLMTuning.NearManafontReadyMin or > BLMTuning.NearManafontReadyMax || state.LucidReadyIn > env.GCD || state.TransposeReadyIn > env.GCD || state.DowntimeIn <= BLMTuning.NearManafontMinDowntime || state.ForcedMoveSoon)
            return false;

        if (state.AstralSoul == 6 || state.MP >= FireSpellCostFor(state) || state.RaidBuffsLeft > 0)
            return false;

        return NearManafontBridgeFiller(state) != PlannerStep.None;
    }

    private PlannerStep NearManafontBridgeFiller(PlannerState state)
    {
        if (state.Polyglot > 0)
            return PlannerStep.Xenoglossy;

        return state.AllowThunder && state.ThunderLeft <= BLMTuning.ThunderRefreshWindow && Env.HasAnyThunder
            ? PlannerStep.Thunder
            : PlannerStep.None;
    }

    private PlannerStep NearManafontBridgeSecondFiller(PlannerState state, PlannerStep firstFiller)
    {
        if (state.Polyglot > (firstFiller == PlannerStep.Xenoglossy ? 1 : 0))
            return PlannerStep.Xenoglossy;

        if (firstFiller != PlannerStep.Thunder
            && state.AllowThunder
            && state.Thunderhead
            && state.ThunderLeft <= BLMTuning.PlannerMovementThunderRefreshWindow
            && state.DowntimeIn > BLMTuning.ThunderMinTargetLife)
            return PlannerStep.Thunder;

        return PlannerStep.None;
    }

    private PlannerStep[] NearManafontBridgeSteps(PlannerState state)
    {
        var filler = NearManafontBridgeFiller(state);
        if (filler == PlannerStep.None)
            return [];

        var secondFiller = NearManafontBridgeSecondFiller(state, filler);

        // Allow a second filler when it helps catch the second Lucid tick before the AF1 Despair.
        if (secondFiller != PlannerStep.None && (state.LeyLinesLeft > 0 || state.ManafontReadyIn >= BLMTuning.NearManafontSecondFillerReadyMin))
        {
            return [
                PlannerStep.Despair,
                PlannerStep.Transpose,
                PlannerStep.LucidDreaming,
                PlannerStep.Paradox,
                filler,
                secondFiller,
                PlannerStep.Transpose,
                PlannerStep.Despair,
                PlannerStep.Manafont
            ];
        }

        return [
            PlannerStep.Despair,
            PlannerStep.Transpose,
            PlannerStep.LucidDreaming,
            PlannerStep.Paradox,
            filler,
            PlannerStep.Transpose,
            PlannerStep.Despair,
            PlannerStep.Manafont
        ];
    }

    private float ScoreRoute(PlannerState state, PlannerStep[] steps, PlannerRouteKind kind)
    {
        foreach (var step in steps)
            if (!ApplyPlannerStep(ref state, step, kind))
                return float.MinValue;

        return PlannerEvaluation(state);
    }

    private bool ShouldPlanParadox(PlannerState state)
    {
        if (!Env.HasParadox || !state.Paradox)
            return false;

        if (state.Element < 0)
            return ShouldPlanIceParadoxBeforeTranspose(state);

        if (state.Element > 0 && state.Firestarter && !Env.MovementPriority)
            return false;

        if (CanGenerateFirestarterWithParadox(state))
            return true;

        if (ShouldPlanDespairBeforeAFParadox(state))
            return false;

        if (state.AstralSoul >= 5 && state.MP >= FireSpellCostFor(state))
            return false;

        if (state.RaidBuffsLeft > 0 && state.RaidBuffsLeft < 15 && state.AstralSoul >= 4)
            return false;

        return state.MP < FireSpellCostFor(state) * 2 || Env.MovementPriority;
    }

    private static bool CanGenerateFirestarterWithParadox(PlannerState state)
        => state.Element < 3;

    private bool ShouldPlanIceParadoxBeforeTranspose(PlannerState state)
        => state.Element < 0 && state.Hearts == Env.MaxHearts && state.MP >= BLMGameConstants.FullIceMP && state.TransposeReadyIn <= Env.GCD;

    private bool ShouldPlanXenoglossy(PlannerState state)
    {
        ref readonly var env = ref Env;
        if (state.AmplifierReadyIn <= 0 && state.Polyglot >= env.MaxPolyglot)
            return true;

        if (state.AmplifierReadyIn <= env.GCDLength
            && state.Polyglot >= env.MaxPolyglot - 1
            && WouldAmplifierCauseNearOvercap(state.Polyglot, state.NextPolyglot))
            return true;

        var overcapSoon = state.Polyglot >= env.MaxPolyglot && state.NextPolyglot <= env.GCDLength;
        var downtimeSoon = state.DowntimeIn <= env.GCDLength * 2;
        var raidBuffNow = state.RaidBuffsLeft > 0;

        if (downtimeSoon || overcapSoon)
            return true;

        if (raidBuffNow)
            return !state.UsedPolyglotRecently;

        if (env.MovementPriority)
            return true;

        return state.RaidBuffsIn > 15 && state.Polyglot == env.MaxPolyglot && !state.UsedPolyglotRecently;
    }

    private bool PlannerAllowsThunder(in Strategy strategy)
        => strategy.Thunder != ThunderStrategy.Delay
        && (strategy.Thunder != ThunderStrategy.InstantOnly || MovementSkillPriorityRequested());

    private bool ShouldPlanThunder(PlannerState state)
        => state.AllowThunder && state.DowntimeIn > BLMTuning.ThunderMinTargetLife
        && (state.ThunderLeft <= BLMTuning.ThunderRefreshWindow
            || state.ThunderLeft <= BLMTuning.PlannerMovementThunderRefreshWindow && Env.MovementPriority);

    private bool CanPlanDespairFinisher(PlannerState state)
        => state.Element == 3 && Unlocked(AID.Despair) && state.MP >= 800;

    private bool ShouldPlanDespairBeforeFire4(PlannerState state)
    {
        // Mirrors ShouldUseDespairBeforeFire4: at Astral Soul 5 the next Fire IV completes Flare Star.
        if (!CanPlanDespairFinisher(state) || state.AstralSoul >= 5)
            return false;

        var fireCost = FireSpellCostFor(state);
        return state.MP >= fireCost
            && state.MP - fireCost < BLMGameConstants.DespairMinMP;
    }

    private bool ShouldPlanDespairBeforeAFParadox(PlannerState state)
    {
        if (!CanPlanDespairFinisher(state) || !state.Paradox || state.Firestarter || state.Element != 3 || state.AstralSoul >= 6)
            return false;

        var paradoxCost = PlannerManaCost(AID.Paradox, state);
        return state.MP >= paradoxCost
            && state.MP - paradoxCost < BLMGameConstants.DespairMinMP;
    }

    private bool ShouldPlanIceSwapFromAF(PlannerState state)
        => state.MP < FireSpellCostFor(state) && !CanPlanDespairFinisher(state);

    private static int PlannerInstantStacks(AID aid) => aid switch
    {
        AID.Triplecast => 3,
        AID.Swiftcast => 1,
        _ => 0
    };

    private int PlannerInstantBudget(bool allowTriplecastDTR, in Strategy strategy, bool allowNewTriplecast)
    {
        var active = PlannerActiveInstantBudget(allowTriplecastDTR);
        var reserved = PlannerPreferredInstantAction(allowTriplecastDTR, strategy, allowNewTriplecast);
        return active + PlannerInstantStacks(reserved);
    }

    private int PlannerActiveInstantBudget(bool allowTriplecastDTR)
    {
        var budget = 0;
        if (allowTriplecastDTR && ActiveTriplecast)
            budget += Math.Max(1, Triplecast.Stacks);
        if (StatusLeft(SID.Swiftcast) > GCD)
            budget++;
        return budget;
    }

    private bool ShouldReserveTriplecastForDTR(bool allowTriplecastDTR, in Strategy strategy, bool allowNewTriplecast)
        => allowTriplecastDTR
        && allowNewTriplecast
        && !ShouldHoldEvenBurstRecoveryAction(BLMGameConstants.AmplifierRecast)
        && Fire > 0
        && MP >= BLMGameConstants.DespairMinMP
        && AstralSoul == 6
        && Firestarter
        && Unlocked(AID.Blizzard4)
        && Unlocked(AID.Despair)
        && TransposeReadySoon()
        && (!ManafontAllowedBySetting(strategy) || ReadyIn(AID.Manafont) > BLMTuning.NearManafontReadyMax)
        && Unlocked(AID.Triplecast)
        && CanWeave(AID.Triplecast, 1);

    private AID PlannerPreferredInstantAction(bool allowTriplecastDTR, in Strategy strategy, bool allowNewTriplecast)
    {
        if (MovementSkillPriorityRequested() && HasPreferredMovementInstantGCD(strategy))
            return AID.None;

        // The planner presses whatever it reserves here, so the tracks have to be checked before the reservation, not after.
        if (AllowTriplecast(strategy) && ShouldReserveTriplecastForDTR(allowTriplecastDTR, strategy, allowNewTriplecast))
            return AID.Triplecast;
        if (AllowSwiftcast(strategy) && Unlocked(AID.Swiftcast) && CanWeave(AID.Swiftcast, 1))
            return AID.Swiftcast;
        if (AllowTriplecast(strategy) && allowTriplecastDTR && allowNewTriplecast && Unlocked(AID.Triplecast) && CanWeave(AID.Triplecast, 1))
            return AID.Triplecast;
        return AID.None;
    }

    private AID PlannerStepAction(PlannerStep step, PlannerState state) => step switch
    {
        PlannerStep.ReserveInstant => state.ReservedInstantAction,
        PlannerStep.Amplifier => AID.Amplifier,
        PlannerStep.LucidDreaming => AID.LucidDreaming,
        PlannerStep.Manafont => AID.Manafont,
        PlannerStep.Transpose => AID.Transpose,
        PlannerStep.Blizzard3 => AID.Blizzard3,
        PlannerStep.Blizzard4 => AID.Blizzard4,
        PlannerStep.Fire3 => AID.Fire3,
        PlannerStep.Fire4 => AID.Fire4,
        PlannerStep.Despair => AID.Despair,
        PlannerStep.FlareStar => AID.FlareStar,
        PlannerStep.Paradox => AID.Paradox,
        PlannerStep.Xenoglossy => AID.Xenoglossy,
        PlannerStep.Thunder => Env.BestThunder,
        _ => AID.None
    };

    private AID BestPlannerThunder() => Unlocked(AID.HighThunder) ? AID.HighThunder : Unlocked(AID.Thunder3) ? AID.Thunder3 : AID.Thunder1;

    private int FireSpellCostFor(PlannerState state) => state.Hearts > 0 ? 800 : 1600;

    private int PlannerManaCost(AID aid, PlannerState state)
    {
        // Fire spells are free under Umbral Ice (the standard Fire III -> 6x Fire IV + Paradox + Despair line only fits a
        // 10000 MP bar that way); Astral Fire doubles fire costs unless an Umbral Heart absorbs it.
        int adjustFire(int cost) => state.Element < 0 ? 0 : state.Element > 0 && state.Hearts == 0 ? cost * 2 : cost;
        return aid switch
        {
            AID.Despair => 800,
            AID.Paradox => state.Element > 0 ? 1600 : 0,
            AID.Fire3 => state.Firestarter ? 0 : adjustFire(2000),
            AID.Fire1 or AID.Fire4 => adjustFire(BLMGameConstants.FireSpellBaseMP),
            _ => 0
        };
    }

    private bool UsesReservedInstant(AID aid, PlannerState state)
        => state.ActiveInstantBudget > 0
        && PlannerCastTime(aid, state) > 0
        && !PlannerParadoxWouldFreeFire3(aid, state);

    // An instant charge is a movement / element-swap / recovery resource, not a throughput buff. Spending one on the
    // Astral Fire re-entry Fire III while the Paradox marker is still unspent buys 1.1s of cast time and pays for it
    // with 2000 MP and an Umbral Heart (800 MP of the next Fire IV): casting that Paradox first grants Firestarter,
    // which makes the very same Fire III both instant and free and keeps the heart. Those 2800 MP are exactly what
    // funds the Despair that closes the fire phase - and the Manafont woven inside it. Without this guard the greedy
    // "first cast in the route always eats the live charge" rule made the paid Fire III look 1.1s cheaper than it is,
    // and the beam preferred it (2026-09-22: 1273 of 2867 combat runs diverged on exactly this, at t=90.00).
    private static bool PlannerParadoxWouldFreeFire3(AID aid, PlannerState state)
        => aid == AID.Fire3
        && state.Element > 0
        && state.Paradox
        && !state.Firestarter;

    private bool PlannerWouldHardcastElementSwap(AID aid, PlannerState state)
    {
        if (!Env.MovementPriority || !state.ElementSwapHardcastTransitionActive || state.ElementSwapHardcastSuppressionExcludedState)
            return false;

        if (aid is not AID.Fire3 and not AID.Blizzard3)
            return false;

        return !PlannerNaturallyInstant(aid, state)
            && !UsesReservedInstant(aid, state);
    }

    private bool PlannerNaturallyInstant(AID aid, PlannerState state)
    {
        ref readonly var env = ref Env;
        return aid is AID.Xenoglossy or AID.Paradox or AID.Amplifier
            || IsThunderAction(aid) && state.Thunderhead
            || aid == AID.Fire3 && state.Firestarter
            || aid == AID.Foul && env.FoulInstant
            || aid == AID.Despair && env.DespairInstant
            || env.ContinuousInstantLeft > env.GCD + state.Time;
    }

    private float PlannerActionTime(AID aid, PlannerState state, bool usesReservedInstant)
    {
        var instant = usesReservedInstant || PlannerNaturallyInstant(aid, state);
        var gcd = PlannerGCD(state.LeyLinesLeft);
        // A completed cast carries a short animation lock before the next spell can start (the "caster tax").
        return instant ? gcd : Math.Max(gcd, PlannerCastTime(aid, state) + BLMPlannerTuning.CasterTax);
    }

    private float PlannerGCD(float leyLinesLeft)
    {
        ref readonly var env = ref Env;
        return leyLinesLeft > 0 ? env.GCDLeyLines : env.GCDNormal;
    }

    // An oGCD only costs time when the current GCD has no room left for it; otherwise it is woven for free. Charging a
    // fixed 0.6s per weave made routes with many weaves look "shorter", which (with a common horizon) handed them
    // filler credit they never earned.
    private bool PlannerWeave(ref PlannerState state)
    {
        var clip = Math.Max(0, BLMPlannerTuning.PlannerWeaveElapsed - state.WeaveSlack);
        state.WeaveSlack = Math.Max(0, state.WeaveSlack - BLMPlannerTuning.PlannerWeaveElapsed);
        state.Time += clip;
        return AdvancePlannerTimers(ref state, clip);
    }

    private bool PlannerWeave(ref AOEPlannerState state)
    {
        var clip = Math.Max(0, BLMPlannerTuning.PlannerWeaveElapsed - state.WeaveSlack);
        state.WeaveSlack = Math.Max(0, state.WeaveSlack - BLMPlannerTuning.PlannerWeaveElapsed);
        state.Time += clip;
        return AdvanceAOEPlannerTimers(ref state, clip);
    }

    // Room left after a GCD action: an instant leaves the GCD minus its animation lock, a cast leaves whatever the cast
    // (plus caster tax) did not use.
    private static float PlannerSlackAfter(float elapsed, float castTime)
        => castTime <= 0 ? Math.Max(0, elapsed - BLMPlannerTuning.PlannerWeaveElapsed) : Math.Max(0, elapsed - castTime - BLMPlannerTuning.CasterTax);

    private float ApplyLeyLinesSpeed(float duration, float leyLinesLeft)
    {
        var stats = World.Client.PlayerStats;
        var haste = InLeyLines ? (int)MathF.Round(stats.Haste / BLMGameConstants.LeyLinesGCDMultiplier) : stats.Haste;
        if (leyLinesLeft > 0)
            haste = haste * 85 / 100;
        // Reconstruct from stats, not an already rounded/buffed GCD. Haste is snapshotted at action start.
        var milliseconds = (int)MathF.Round(duration * 1000) * ActionSpeed.SpeedStatToModifier(stats.SpellSpeed, Player.Level) / 1000 * haste / 100;
        return ActionSpeed.Round(milliseconds);
    }

    private float PlannerCastTime(AID aid, PlannerState state)
    {
        if (PlannerNaturallyInstant(aid, state))
            return 0;

        var slot = PlannerCastSlot(aid);
        if (slot < 0)
            return 0;

        var halved = state.Element == -3 && PlannerFireAspect[slot] || state.Element == 3 && PlannerIceAspect[slot];
        return PlannerCastTimes[slot * 4 + (state.LeyLinesLeft > 0 ? 2 : 0) + (halved ? 1 : 0)];
    }

    private void ApplyPlannerElementTransition(ref PlannerState state, int nextElement, bool fromTranspose)
    {
        var oldElement = state.Element;
        state.Element = nextElement;
        state.ElementTimer = BLMGameConstants.ElementTimer;
        if (oldElement == 0 && nextElement != 0)
            state.NextPolyglot = BLMGameConstants.PolyglotInterval;
        if (nextElement < 0)
            state.AstralSoul = 0;

        if (oldElement == 0 || Math.Sign(oldElement) != Math.Sign(nextElement))
            state.Thunderhead = true;

        if (oldElement != 0 && Math.Sign(oldElement) != Math.Sign(nextElement))
        {
            ref readonly var env = ref Env;
            if (env.HasParadox)
            {
                if (nextElement < 0 && Math.Abs(oldElement) == 3)
                    state.Paradox = true;
                else if (nextElement > 0 && oldElement == -3 && state.Hearts == env.MaxHearts)
                    state.Paradox = true;
            }
        }
    }

    private void RefreshPlannerElementTimer(ref PlannerState state, AID aid)
    {
        if (state.Element == 0)
            return;

        if (aid is AID.Paradox or AID.Blizzard4 or AID.Despair)
            state.ElementTimer = BLMGameConstants.ElementTimer;
    }

    private static PlannerStep[] DTRRouteSteps(PlannerState state, bool reserveInstant)
    {
        if (state.AstralSoul == 6)
            return reserveInstant
                ? [PlannerStep.FlareStar, PlannerStep.Despair, PlannerStep.Transpose, PlannerStep.ReserveInstant, PlannerStep.Blizzard3, PlannerStep.Blizzard4, PlannerStep.Paradox, PlannerStep.Transpose, PlannerStep.Fire3]
                : [PlannerStep.FlareStar, PlannerStep.Despair, PlannerStep.Transpose, PlannerStep.Blizzard3, PlannerStep.Blizzard4, PlannerStep.Paradox, PlannerStep.Transpose, PlannerStep.Fire3];

        if (state.AstralSoul == 5)
            return reserveInstant
                ? [PlannerStep.Despair, PlannerStep.FlareStar, PlannerStep.Transpose, PlannerStep.ReserveInstant, PlannerStep.Blizzard3, PlannerStep.Blizzard4, PlannerStep.Paradox, PlannerStep.Transpose, PlannerStep.Fire3]
                : [PlannerStep.Despair, PlannerStep.FlareStar, PlannerStep.Transpose, PlannerStep.Blizzard3, PlannerStep.Blizzard4, PlannerStep.Paradox, PlannerStep.Transpose, PlannerStep.Fire3];

        return reserveInstant
            ? [PlannerStep.Despair, PlannerStep.Transpose, PlannerStep.ReserveInstant, PlannerStep.Blizzard3, PlannerStep.Blizzard4, PlannerStep.Paradox, PlannerStep.Transpose, PlannerStep.Fire3]
            : [PlannerStep.Despair, PlannerStep.Transpose, PlannerStep.Blizzard3, PlannerStep.Blizzard4, PlannerStep.Paradox, PlannerStep.Transpose, PlannerStep.Fire3];
    }

    private AID[] PlannerStepsToActions(PlannerStep[] steps, AID reservedInstantAction)
        => [.. steps.Select(step => step == PlannerStep.ReserveInstant ? reservedInstantAction : PlannerStepAction(step, default)).Where(aid => aid != AID.None)];

    private static AID[] DTRRouteActions(PlannerState state, bool reserveInstant)
    {
        if (state.AstralSoul == 6)
            return reserveInstant
                ? [AID.FlareStar, AID.Despair, AID.Transpose, state.ReservedInstantAction, AID.Blizzard3, AID.Blizzard4, AID.Paradox, AID.Transpose, AID.Fire3]
                : [AID.FlareStar, AID.Despair, AID.Transpose, AID.Blizzard3, AID.Blizzard4, AID.Paradox, AID.Transpose, AID.Fire3];

        if (state.AstralSoul == 5)
            return reserveInstant
                ? [AID.Despair, AID.FlareStar, AID.Transpose, state.ReservedInstantAction, AID.Blizzard3, AID.Blizzard4, AID.Paradox, AID.Transpose, AID.Fire3]
                : [AID.Despair, AID.FlareStar, AID.Transpose, AID.Blizzard3, AID.Blizzard4, AID.Paradox, AID.Transpose, AID.Fire3];

        return reserveInstant
            ? [AID.Despair, AID.Transpose, state.ReservedInstantAction, AID.Blizzard3, AID.Blizzard4, AID.Paradox, AID.Transpose, AID.Fire3]
            : [AID.Despair, AID.Transpose, AID.Blizzard3, AID.Blizzard4, AID.Paradox, AID.Transpose, AID.Fire3];
    }

    private float EffectivePotency(AID aid, PlannerState state)
    {
        var potency = aid switch
        {
            AID.Fire4 => BLMGameConstants.Fire4Potency,
            AID.Despair => BLMGameConstants.DespairPotency,
            AID.FlareStar => BLMGameConstants.FlareStarPotency,
            AID.Fire3 => 290,
            AID.Blizzard3 => 290,
            AID.Blizzard4 => BLMGameConstants.Blizzard4Potency,
            AID.Paradox => BLMGameConstants.ParadoxPotency,
            AID.Xenoglossy => BLMGameConstants.XenoglossyPotency,
            AID.Foul => BLMGameConstants.FoulPotency,
            _ when IsThunderAction(aid) => ThunderPlannerPotency(state, ThunderDataFor(aid)),
            _ => 0
        };

        var slot = PlannerCastSlot(aid);
        var fire = slot >= 0 && PlannerFireAspect[slot];
        var ice = slot >= 0 && PlannerIceAspect[slot];
        // The Astral Fire / Umbral Ice multiplier always comes from the element the spell was cast UNDER, never the one it
        // grants. Measured 2026-09-23 against the player own 7.5 replays (per-file medians of non-crit non-direct hits,
        // normalised to Fire IV under AF3): Fire III 0.370x at UI3, 0.42x at neutral, 0.74x at AF1, 0.96x at AF3, and
        // Blizzard III 0.53x under Umbral Ice against 0.375x under AF3. Those are exactly potency x {0.7, 1.0 with no
        // Enochian, 1.4, 1.8} and {1.0, 0.7}; the old "Fire III and Despair grant AF3 before damage" reading would have
        // made all four Fire III numbers equal. Ice spells take the Astral Fire penalty for the same reason.
        var element = state.Element;
        var multiplier = 1f;
        if (fire && element > 0)
            multiplier *= FireMultiplier(element);
        else if (fire && element < 0)
            multiplier *= 1f + 0.1f * element;
        else if (ice && element > 0)
            multiplier *= 1f - 0.1f * element;

        multiplier *= Env.Enochian;

        if (state.RaidBuffsLeft > 0)
            multiplier *= 1.05f;

        return potency * multiplier;
    }

    private static float ThunderDurationFor(AID aid) => aid switch
    {
        AID.Thunder1 => BLMGameConstants.Thunder1Duration,
        AID.Thunder2 => BLMGameConstants.Thunder2Duration,
        AID.Thunder3 => BLMGameConstants.Thunder3Duration,
        AID.Thunder4 => BLMGameConstants.Thunder4Duration,
        AID.HighThunder => BLMGameConstants.HighThunderDuration,
        AID.HighThunder2 => BLMGameConstants.HighThunder2Duration,
        _ => 0
    };

    private static (int Initial, int Dot, float Duration) ThunderDataFor(AID aid) => aid switch
    {
        AID.Thunder1 => (BLMGameConstants.Thunder1InitialPotency, BLMGameConstants.Thunder1DotPotency, BLMGameConstants.Thunder1Duration),
        AID.Thunder2 => (BLMGameConstants.Thunder2InitialPotency, BLMGameConstants.Thunder2DotPotency, BLMGameConstants.Thunder2Duration),
        AID.Thunder3 => (BLMGameConstants.Thunder3InitialPotency, BLMGameConstants.Thunder3DotPotency, BLMGameConstants.Thunder3Duration),
        AID.Thunder4 => (BLMGameConstants.Thunder4InitialPotency, BLMGameConstants.Thunder4DotPotency, BLMGameConstants.Thunder4Duration),
        AID.HighThunder => (BLMGameConstants.HighThunderInitialPotency, BLMGameConstants.HighThunderDotPotency, BLMGameConstants.HighThunderDuration),
        AID.HighThunder2 => (BLMGameConstants.HighThunder2InitialPotency, BLMGameConstants.HighThunder2DotPotency, BLMGameConstants.HighThunder2Duration),
        _ => (0, 0, 0)
    };

    // Reference instant: ApplyPlannerStep calls EffectivePotency BEFORE `state.Time += elapsed`, so Time, ThunderLeft
    // and DowntimeIn below are all pre-cast (the AoE twin scores after the advance - see ThunderAOEPlannerPotency).
    // Only the ticks that land before the common evaluation horizon belong in Score: everything past it is paid for
    // once, by the ThunderLeft term in TerminalStateValue. Without the horizon clamp a Thunder placed anywhere in the
    // route booked its whole 30s of ticks against a 45s yardstick AND collected the terminal credit on top, so a cast
    // 2.5s before the horizon was worth 952 + 381 for zero ticks actually inside the window.
    private static float ThunderPlannerPotency(PlannerState state, (int Initial, int Dot, float Duration) data)
    {
        var usableDuration = Math.Min(data.Duration, Math.Max(0, Math.Min(state.Horizon - state.Time, state.DowntimeIn)));
        var newTicks = MathF.Floor(usableDuration / BLMGameConstants.LucidDreamingTickInterval);
        var clippedTicks = MathF.Floor(Math.Min(Math.Max(0, state.ThunderLeft), usableDuration) / BLMGameConstants.LucidDreamingTickInterval);
        return data.Initial + Math.Max(0, newTicks - clippedTicks) * data.Dot;
    }

    private bool TryPushPlannerLock(in Strategy strategy, Enemy primaryTarget)
    {
        AdvancePlannerLock();
        if (PlannerLockedIndex >= PlannerLockedRoute.Count)
            return false;

        var aid = PlannerLockedRoute[PlannerLockedIndex];
        if (aid == AID.None)
        {
            ClearPlannerLock();
            return false;
        }

        if (aid == AID.Manafont && !CanExecuteManafontFromPlanner(strategy, primaryTarget, hasPriorityTarget: true))
        {
            ClearPlannerLock();
            return false;
        }

        if (IsThunderAction(aid) && (!Thunderhead || !PlannerAllowsThunder(strategy)))
        {
            ClearPlannerLock();
            return false;
        }

        if (!IsPolyglotAction(aid) && TryPushUrgentPolyglot(strategy, primaryTarget, GCDPriority.ResourceCap))
            return true;

        if (!IsThunderAction(aid) && TryPushThunderRefresh(strategy, GCDPriority.FuturePlan))
            return true;

        if (aid != AID.Manafont && TryPushAFHoldForNearManafont(strategy, primaryTarget, GCDPriority.FuturePlan))
            return true;

        PushPlannerAction(strategy, aid, primaryTarget);
        return true;
    }

    private bool TryStartStandardDTRLock(in Strategy strategy, Enemy primaryTarget)
    {
        if (!CanUseHighLevelPlanner())
            return false;

        if (PolyglotOvercapOnly(strategy))
            return false;

        var state = CurrentPlannerState(strategy, CanUseTriplecastForTarget(primaryTarget));
        if (!CanPlanDTR(state))
            return false;

        var useReservedInstant = state.ActiveInstantBudget < 2 && state.ReservedInstantAction != AID.None;
        SetPlannerLock(DTRRouteActions(state, useReservedInstant));
        return TryPushPlannerLock(strategy, primaryTarget);
    }

    private void AdvancePlannerLock()
    {
        if (PlannerLockedRoute.Count == 0 || World.CurrentTime > PlannerLockUntil)
        {
            ClearPlannerLock();
            return;
        }

        var cast = Manager.LastCast.Data;
        if (cast == null || cast.SourceSequence == 0 || cast.SourceSequence == PlannerLockLastAdvancedSequence)
            return;

        if (PlannerLockedIndex < PlannerLockedRoute.Count && cast.IsSpell(PlannerLockedRoute[PlannerLockedIndex]))
        {
            PlannerLockedIndex++;
            PlannerLockLastAdvancedSequence = cast.SourceSequence;
        }
        else if (IsPlannerLockTolerantInsertion(cast))
        {
            // TryPushUrgentPolyglot / TryPushThunderRefresh run ahead of the lock and legitimately insert an instant
            // (Xenoglossy, Thunder, Amplifier) between two locked steps. That insertion does not invalidate the route
            // (the search already credited the whole DTR); clearing the lock here re-planned the remainder from a worse
            // state (hardcast Blizzard III / a second Transpose). Keep the lock, do not advance it.
            PlannerLockLastAdvancedSequence = cast.SourceSequence;
        }
        else
        {
            PlannerLockLastAdvancedSequence = cast.SourceSequence;
            ClearPlannerLock();
            return;
        }

        if (PlannerLockedIndex >= PlannerLockedRoute.Count)
            ClearPlannerLock();
    }

    private static bool IsPlannerLockTolerantInsertion(ActorCastEvent cast)
    {
        if (!cast.IsSpell())
            return false;
        var aid = (AID)cast.Action.ID;
        return IsPolyglotAction(aid) || IsThunderAction(aid) || aid == AID.Amplifier;
    }

    private void SetPlannerLock(AID[] route)
    {
        PlannerLockedRoute.Clear();
        PlannerLockedRoute.AddRange(route);
        PlannerLockedIndex = 0;
        PlannerLockLastAdvancedSequence = Manager.LastCast.Data?.SourceSequence ?? 0;
        PlannerLockUntil = World.FutureTime(BLMTuning.PlannerLockWindowSeconds);
    }

    private void ClearPlannerLock()
    {
        PlannerLockedRoute.Clear();
        PlannerLockedIndex = 0;
        PlannerLockLastAdvancedSequence = 0;
    }

    private void HandleRotationModeHandoff(in Strategy strategy)
    {
        var rotationMode = strategy.Rotation.Value;
        if (rotationMode == RotationStrategy.PolyglotOvercapOnly)
            ClearManualToAutoManafontHandoff("entered-manual-assist");

        if (!HaveLastRotationMode)
        {
            HaveLastRotationMode = true;
            LastRotationMode = rotationMode;
            return;
        }

        if (LastRotationMode == rotationMode)
            return;

        var previousMode = LastRotationMode;
        LastRotationMode = rotationMode;
        RotationModeHandoffUntil = World.FutureTime(BLMTuning.HandoffWindowSeconds);
        var fromManualControl = previousMode == RotationStrategy.PolyglotOvercapOnly;
        ResetPlannerHandoffState(strategy, fromManualControl);
        if (fromManualControl && UseAbilities(strategy))
            StartManualToAutoManafontHandoff(strategy);
    }

    private void HandleManualControlHandoff(in Strategy strategy, bool resumedAfterManualGap)
    {
        if (!resumedAfterManualGap || !Player.InCombat)
            return;

        RotationModeHandoffUntil = World.FutureTime(BLMTuning.HandoffWindowSeconds);
        ResetPlannerHandoffState(strategy, true);
    }

    private void HandleExternalHintHandoff(in Strategy strategy)
    {
        var mode = strategy.MechanicHints.Value;
        if (!HaveLastExternalHintStrategy)
        {
            HaveLastExternalHintStrategy = true;
            LastExternalHintStrategy = mode;
            return;
        }

        if (LastExternalHintStrategy == mode)
            return;

        LastExternalHintStrategy = mode;
        RotationModeHandoffUntil = World.FutureTime(BLMTuning.HandoffWindowSeconds);
        ResetPlannerHandoffState(strategy, fromManualControl: false);
    }

    private void ResetPlannerHandoffState(in Strategy strategy, bool fromManualControl)
    {
        ClearPlannerLock();
        ResetAOEToSingleBridge();
        SyncStandard57OpenerStateForHandoff(strategy, fromManualControl);
        ResetInstantB3TransposeReentry();
    }

    private bool ManualToAutoManafontHandoffActive
        => ManualToAutoManafontHandoffPending
        && World.CurrentTime <= ManualToAutoManafontHandoffUntil;

    private void LogManualToAutoManafontHandoff(string message)
    {
        if (World.CurrentTime < NextManafontHandoffLogAt)
            return;

        NextManafontHandoffLogAt = World.FutureTime(BLMTuning.ManafontBlockedLogInterval);
        Service.Log($"[BLM][Manafont][Handoff] {message}");
    }

    private void StartManualToAutoManafontHandoff(in Strategy strategy)
    {
        ManualToAutoManafontHandoffPending = true;
        ManualToAutoManafontHandoffUntil = World.FutureTime(BLMTuning.HandoffWindowSeconds);
        LogManualToAutoManafontHandoff($"state=start mode={strategy.Rotation.Value}");
    }

    private void ClearManualToAutoManafontHandoff(string reason)
    {
        if (!ManualToAutoManafontHandoffPending)
            return;

        ManualToAutoManafontHandoffPending = false;
        ManualToAutoManafontHandoffUntil = default;
        LogManualToAutoManafontHandoff($"state=clear reason={reason}");
    }

    private void UpdateManualToAutoManafontHandoff(in Strategy strategy)
    {
        if (!ManualToAutoManafontHandoffPending)
            return;

        if (World.CurrentTime > ManualToAutoManafontHandoffUntil)
        {
            ClearManualToAutoManafontHandoff("expired");
            return;
        }

        if (!UseAbilities(strategy))
        {
            ClearManualToAutoManafontHandoff("no-abilities");
            return;
        }

        if (!Player.InCombat)
        {
            ClearManualToAutoManafontHandoff("not-in-combat");
            return;
        }

        if (Fire <= 0)
        {
            ClearManualToAutoManafontHandoff("not-fire");
            return;
        }

        if (!ManafontAllowedBySetting(strategy))
        {
            ClearManualToAutoManafontHandoff("no-setting");
            return;
        }

        if (Standard57PostManafont)
        {
            ClearManualToAutoManafontHandoff("standard57-post-manafont");
            return;
        }

        if (ReadyIn(AID.Manafont) > GCDLength * 2)
            ClearManualToAutoManafontHandoff("used-or-not-ready");
    }

    private void UpdateAOEToSingleBridgeState(bool effectiveAOE)
    {
        if (effectiveAOE)
        {
            if (!WasInAOE)
                SingleToAOETransitionUntil = World.FutureTime(BLMTuning.AOEToSingleTransitionWindow);

            AOEToSingleBridgeActive = false;
            AOEToSingleTransitionUntil = default;
            WasInAOE = true;
            return;
        }

        if (WasInAOE)
        {
            if (ShouldKeepAOEToSingleFireBridge())
            {
                AOEToSingleBridgeActive = true;
                AOEToSingleTransitionUntil = World.FutureTime(BLMTuning.AOEToSingleTransitionWindow);
            }
            else
            {
                AOEToSingleBridgeActive = false;
                AOEToSingleTransitionUntil = default;
            }
        }
        WasInAOE = false;
    }

    private void ResetAOEToSingleBridge()
    {
        WasInAOE = false;
        AOEToSingleBridgeActive = false;
        AOEToSingleTransitionUntil = default;
        SingleToAOETransitionUntil = default;
    }

    private bool AOEToSingleTransitionActive => World.CurrentTime <= AOEToSingleTransitionUntil;
    private bool SingleToAOETransitionActive => World.CurrentTime <= SingleToAOETransitionUntil;
    private bool ElementSwapHardcastTransitionActive => AOEToSingleTransitionActive || SingleToAOETransitionActive;
    private bool StableElementSwapState => Ice == 3 || Fire == 3;
    private bool NeutralElementSwapState => Ice == 0 && Fire == 0;
    private bool ElementSwapHardcastSuppressionExcludedState => StableElementSwapState || NeutralElementSwapState;

    private void ObserveExternalCastForHandoff()
    {
        var cast = Manager.LastCast.Data;
        if (cast == null || cast.SourceSequence == 0 || cast.SourceSequence == LastObservedCastSequence)
            return;

        LastObservedCastSequence = cast.SourceSequence;
        if (cast.IsSpell(AID.FlareStar))
            LastFlareStarCastAt = Manager.LastCast.Time;

        if (cast.IsSpell(AID.Manafont))
        {
            LastFlareStarCastAt = default;
            ClearManualToAutoManafontHandoff("observed");
        }

        if (cast.IsSpell(AID.Xenoglossy) || cast.IsSpell(AID.Foul))
            LastPolyglotSpendAt = World.CurrentTime;

        if (PlannerLockedRoute.Count > 0)
            return;

        if (AOEToSingleBridgeActive)
        {
            if (AstralSoul == 0 || UseAOERotation() || cast.IsSpell(AID.FlareStar))
                AOEToSingleBridgeActive = false;
        }

        if (InstantB3TransposeReentryActive)
        {
            if (cast.IsSpell(AID.Transpose) && Fire > 0)
                ResetInstantB3TransposeReentry();
            else if (!cast.IsSpell(AID.Transpose) && !cast.IsSpell(AID.Swiftcast) && !cast.IsSpell(AID.Triplecast) && !cast.IsSpell(AID.Blizzard3) && !cast.IsSpell(AID.Blizzard4) && !cast.IsSpell(AID.Paradox))
                ResetInstantB3TransposeReentry();
        }
    }

    private void PushPlannerStep(in Strategy strategy, PlannerStep step, Enemy primaryTarget, AID reservedInstantAction)
    {
        var aid = step == PlannerStep.ReserveInstant ? reservedInstantAction : PlannerStepAction(step, default);
        PushPlannerAction(strategy, aid, primaryTarget);
    }

    private void PushPlannerAction(in Strategy strategy, AID aid, Enemy primaryTarget)
    {
        if (aid == AID.None)
            return;

        if (aid == AID.Manafont)
        {
            if (!CanExecuteManafontFromPlanner(strategy, primaryTarget, hasPriorityTarget: true))
            {
                ClearPlannerLock();
                return;
            }

            var manafontPriority = ShouldUseStandard57OpenerControl(strategy) && Standard57AnyManafontWindow(strategy)
                ? GCDPriority.Opener
                : GCDPriority.FuturePlan;
            PushManafont(strategy, manafontPriority, primaryTarget);
            return;
        }

        if (IsThunderAction(aid) && (!Thunderhead || !PlannerAllowsThunder(strategy)))
        {
            ClearPlannerLock();
            return;
        }

        if (aid == AID.Paradox && Fire > 0 && !CanUseAFParadox(strategy))
        {
            ClearPlannerLock();
            return;
        }

        if (aid is AID.Swiftcast or AID.Triplecast
            && MovementSkillPriorityRequested()
            && HasHigherPriorityMovementInstantBeforeCastEnabler(strategy))
        {
            ClearPlannerLock();
            return;
        }

        if (aid == AID.Transpose && !TransposeReadySoon())
        {
            ClearPlannerLock();
            return;
        }

        // the client refuses this category right now (Silence / Amnesia): the lock would wait on a step that cannot run
        if (IsActionLocked(aid))
        {
            ClearPlannerLock();
            return;
        }

        if (aid is (AID.Fire3 or AID.Blizzard3)
            && ShouldSuppressTransitionElementSwapHardcast(aid)
            && GetCastTime(aid) > 0)
        {
            ClearPlannerLock();
            return;
        }

        if (!Unlocked(aid))
        {
            ClearPlannerLock();
            return;
        }

        if (aid == AID.Despair)
            PushManafontFinisherCastEnabler(strategy, primaryTarget, aid);

        if (aid is AID.Transpose or AID.Triplecast or AID.Swiftcast or AID.Amplifier or AID.LucidDreaming)
            PushAction(aid, Player, ActionQueue.Priority.High + (int)GCDPriority.FuturePlan, 0);
        else if (aid is AID.Xenoglossy or AID.Foul)
            PushGCD(aid, primaryTarget, GCDPriority.FuturePlan);
        else if (IsSingleThunderAction(aid))
            PushGCD(aid, BestThunderTarget ?? primaryTarget, GCDPriority.FuturePlan);
        else
            PushGCD(aid, primaryTarget, GCDPriority.FuturePlan);
    }

    private struct AOEPlannerState
    {
        public int Element;
        public int MP;
        public int Hearts;
        public int Polyglot;
        public int AstralSoul;
        public bool Paradox;
        public bool Thunderhead;
        public bool AllowThunder;
        public int ActiveInstantBudget;
        public float ThunderLeft;
        public float NextPolyglot;
        public float ElementTimer;
        public float TransposeReadyIn;
        public float DowntimeIn;
        public float LeyLinesLeft;
        public float AmplifierReadyIn;
        public float ManafontReadyIn;
        public float RaidBuffsLeft;
        public float RaidBuffsIn;
        public bool ForcedMoveSoon;
        public bool UsedPolyglotRecently;
        public bool AllowManafont;
        public bool AllowBurstActions;
        public int Targets;
        public int DotTargets;
        public float Time;
        public float Score;
        public AID FirstAction;
        public float Horizon;
        // Same meaning as PlannerState.Finished: reached the horizon, or ran out of legal successors before it.
        public bool Finished;
        public float WeaveSlack;

        public readonly bool SameState(in AOEPlannerState other)
            => Element == other.Element && MP == other.MP && Hearts == other.Hearts && Polyglot == other.Polyglot && AstralSoul == other.AstralSoul
            && Paradox == other.Paradox && Thunderhead == other.Thunderhead && ActiveInstantBudget == other.ActiveInstantBudget
            && MathF.Abs(Time - other.Time) < 0.05f
            && MathF.Abs(ThunderLeft - other.ThunderLeft) < 0.5f
            && MathF.Abs(LeyLinesLeft - other.LeyLinesLeft) < 0.5f
            && MathF.Abs(TransposeReadyIn - other.TransposeReadyIn) < 0.5f
            && MathF.Abs(ManafontReadyIn - other.ManafontReadyIn) < 0.5f
            && MathF.Abs(AmplifierReadyIn - other.AmplifierReadyIn) < 0.5f;
    }

    private readonly record struct AOEPlannerCacheKey(
        int Element,
        int MP,
        int Hearts,
        int Polyglot,
        int AstralSoul,
        bool Paradox,
        bool Thunderhead,
        bool AllowThunder,
        int ActiveInstantBudget,
        int ThunderLeft,
        int NextPolyglot,
        int ElementTimer,
        int TransposeReadyIn,
        int DowntimeIn,
        int LeyLinesLeft,
        int AmplifierReadyIn,
        int ManafontReadyIn,
        int RaidBuffsLeft,
        int RaidBuffsIn,
        bool ForcedMoveSoon,
        bool UsedPolyglotRecently,
        bool AllowManafont,
        bool AllowBurstActions,
        int Targets,
        int DotTargets,
        int PlayerLevel,
        int MaxHearts,
        int MaxPolyglot,
        int GCD,
        int GCDLength,
        bool CanWeaveAmplifier,
        bool MovementPriorityRequested,
        uint LastCastSequence,
        RotationStrategy Rotation);

    private void UseAOEFuturePlanner(in Strategy strategy, Enemy primaryTarget)
    {
        if (TryPushUrgentPolyglot(strategy, primaryTarget, GCDPriority.ResourceCap))
            return;

        if (TryPushThunderRefresh(strategy, GCDPriority.FuturePlan))
            return;

        var reuseKey = BuildPlannerReuseKey(strategy, allowNewTriplecast: true);
        AOEPlannerState best;
        if (PlanReusable(AOEPlanReuseValid, AOEPlanReuseKey, reuseKey, AOEPlanReusedAt))
            best = AOEPlanReused;
        else
        {
            best = FindBestAOEPlannerState(strategy);
            AOEPlanReused = best;
            AOEPlanReuseKey = reuseKey;
            AOEPlanReusedAt = World.CurrentTime;
            AOEPlanReuseValid = true;
        }
        if (best.FirstAction == AID.None)
            return;

        PushAOEPlannerAction(strategy, best.FirstAction, primaryTarget);
    }

    private AOEPlannerState FindBestAOEPlannerState(in Strategy strategy, float? leylinesLeftOverride = null)
    {
        var initial = CurrentAOEPlannerState(strategy, leylinesLeftOverride);
        PreparePlannerCache();
        var cacheKey = BuildAOEPlannerCacheKey(initial, strategy);
        if (AOEPlannerCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var beam = AOEPlannerBeam;
        var next = AOEPlannerNextBeam;
        beam[0] = (initial, 0);
        var beamCount = 1;

        for (var depth = 0; depth < BLMPlannerTuning.AOEPlannerMaxDepth; ++depth)
        {
            var nextCount = 0;
            var expanded = false;
            for (var index = 0; index < beamCount; ++index)
            {
                var state = beam[index].State;
                if (state.Finished || state.Time >= state.Horizon)
                {
                    InsertPlannerState(next, ref nextCount, state, beam[index].Score);
                    continue;
                }

                var anySuccessor = false;
                foreach (var aid in AOEPlannerActions(state))
                {
                    var candidate = state;
                    if (!ApplyAOEPlannerAction(ref candidate, aid))
                        continue;

                    if (candidate.FirstAction == AID.None)
                        candidate.FirstAction = aid;

                    expanded = true;
                    anySuccessor = true;
                    InsertPlannerState(next, ref nextCount, candidate, AOEPlannerEvaluation(candidate));
                }

                if (!anySuccessor)
                {
                    // Same rule as the single-target beam: a route with no legal move left before the horizon is
                    // finished, so it is carried once at its own evaluation instead of being deleted. Terminates for the
                    // same reason - the flagged copy is skipped by the guard above, so it produces no candidates, never
                    // sets `expanded`, and occupies exactly one beam slot per depth.
                    var finished = state;
                    finished.Finished = true;
                    InsertPlannerState(next, ref nextCount, finished, beam[index].Score);
                }
            }

            if (nextCount == 0)
                break;

            (beam, next) = (next, beam);
            beamCount = nextCount;
            if (!expanded)
                break;
        }

        var best = beam[0].State;
        var minimumTime = Math.Min(BLMPlannerTuning.AOEPlannerMinUsefulTimeSeconds, initial.DowntimeIn);
        for (var index = 0; index < beamCount; ++index)
        {
            var candidate = beam[index].State;
            if (candidate.FirstAction != AID.None && candidate.Time >= minimumTime)
            {
                best = candidate;
                break;
            }
        }
        AOEPlannerCache[cacheKey] = best;
        return best;
    }

    private AOEPlannerState CurrentAOEPlannerState(in Strategy strategy, float? leylinesLeftOverride = null)
    {
        var timeline = CurrentTimelineContext();
        var aoeThunderLeft = GetTargetThunderLeft(BestAOEThunderTarget?.Actor);
        return new()
        {
            Element = Element,
            MP = (int)MP,
            Hearts = Hearts,
            Polyglot = Polyglot,
            AstralSoul = AstralSoul,
            Paradox = Paradox,
            Thunderhead = Thunderhead,
            AllowThunder = PlannerAllowsThunder(strategy),
            ActiveInstantBudget = PlannerActiveInstantBudget(true),
            ThunderLeft = aoeThunderLeft == float.MaxValue ? ThunderDurationFor(BestAOEThunder()) : aoeThunderLeft,
            NextPolyglot = Math.Max(0, NextPolyglot),
            ElementTimer = Element == 0 ? 0 : ElementTimerLeft,
            TransposeReadyIn = Unlocked(AID.Transpose) ? ReadyIn(AID.Transpose) : float.MaxValue,
            DowntimeIn = timeline.PlannerDowntimeIn,
            LeyLinesLeft = leylinesLeftOverride ?? (InLeyLines ? Math.Max(0, StatusLeft(SID.CircleOfPower)) : 0),
            AmplifierReadyIn = PlannerAmplifierReadyIn(strategy),
            ManafontReadyIn = Unlocked(AID.Manafont) ? ReadyIn(AID.Manafont) : float.MaxValue,
            RaidBuffsLeft = Math.Max(0, RaidBuffsLeft),
            RaidBuffsIn = Math.Max(0, RaidBuffsIn),
            ForcedMoveSoon = timeline.ForcedMoveSoon,
            UsedPolyglotRecently = RecentlySpentPolyglot(),
            AllowManafont = ManafontForced(strategy) || ManafontAutomatic(strategy) && !ShouldHoldEvenBurstRecoveryAction(CurrentManafontRecast),
            AllowBurstActions = !ShouldHoldEvenBurstRecoveryAction(BLMGameConstants.AmplifierRecast),
            Targets = NumAOETargets,
            DotTargets = NumAOEDotTargets,
            Horizon = Math.Min(BLMPlannerTuning.AOEPlannerHorizonSeconds, timeline.PlannerDowntimeIn),
            WeaveSlack = Math.Max(0, GCD),
        };
    }

    private AOEPlannerCacheKey BuildAOEPlannerCacheKey(AOEPlannerState state, in Strategy strategy)
        => new(
            state.Element,
            state.MP,
            state.Hearts,
            state.Polyglot,
            state.AstralSoul,
            state.Paradox,
            state.Thunderhead,
            state.AllowThunder,
            state.ActiveInstantBudget,
            PlannerCacheTimeBucket(state.ThunderLeft),
            PlannerCacheTimeBucket(state.NextPolyglot),
            PlannerCacheTimeBucket(state.ElementTimer),
            PlannerCacheTimeBucket(state.TransposeReadyIn),
            PlannerCacheTimeBucket(state.DowntimeIn),
            PlannerCacheTimeBucket(state.LeyLinesLeft),
            PlannerCacheTimeBucket(state.AmplifierReadyIn),
            PlannerCacheTimeBucket(state.ManafontReadyIn),
            PlannerCacheTimeBucket(state.RaidBuffsLeft),
            PlannerCacheTimeBucket(state.RaidBuffsIn),
            state.ForcedMoveSoon,
            state.UsedPolyglotRecently,
            state.AllowManafont,
            state.AllowBurstActions,
            state.Targets,
            state.DotTargets,
            Player.Level,
            MaxHearts,
            MaxPolyglot,
            PlannerCacheTimeBucket(GCD),
            PlannerCacheTimeBucket(GCDLength),
            Unlocked(AID.Amplifier) && CanWeave(AID.Amplifier),
            MovementSkillPriorityRequested(),
            LastObservedCastSequence,
            strategy.Rotation.Value);

    private IEnumerable<AID> AOEPlannerActions(AOEPlannerState state)
    {
        var env = Env;
        if (ShouldPlanAOEAmplifier(state))
            yield return AID.Amplifier;

        if (state.Element > 0 && state.MP < 800 && state.AllowManafont && state.ManafontReadyIn <= 0)
            yield return AID.Manafont;

        if (env.HasFoul && state.Polyglot > 0 && ShouldPlanAOEFoul(state))
            yield return AID.Foul;

        if (state.AllowThunder && state.Thunderhead
            && state.DotTargets >= AOEBreakpoint
            && state.DowntimeIn > BLMTuning.AOEThunderMinTargetLife
            && (state.ThunderLeft <= BLMTuning.ThunderRefreshWindow
                || env.MovementPriority && state.ThunderLeft <= BLMTuning.PlannerMovementThunderRefreshWindow))
            yield return env.BestAOEThunder;

        if (env.HasParadox
            && state.Paradox
            && state.Element < 0
            && (state.Hearts == env.MaxHearts && state.MP >= 2400
                || env.MovementPriority
                || state.DowntimeIn <= env.GCDLength * 2))
            yield return AID.Paradox;

        if (state.Element > 0)
        {
            if (env.HasFlareStar && state.AstralSoul == 6)
                yield return AID.FlareStar;

            if (env.HasFlare && state.MP >= 800 && state.AstralSoul < 6)
                yield return AID.Flare;

            if (env.HasTranspose && state.MP < 800 && state.AstralSoul < 6 && state.TransposeReadyIn <= 0)
                yield return AID.Transpose;
        }
        else if (state.Element < 0)
        {
            if (state.Hearts < env.MaxHearts || state.MP < 2400 || env.HasFlareStar && state.TransposeReadyIn > 0)
            {
                var iceSpell = BestAOEPlannerIceSpell(state);
                if (iceSpell != AID.None)
                    yield return iceSpell;
            }

            if (env.HasTranspose && state.Hearts == env.MaxHearts && state.MP >= 2400 && state.TransposeReadyIn <= 0)
                yield return AID.Transpose;
        }
        else
        {
            if (AOEBlizzard2Worthwhile(state.Targets))
                yield return BestAOEBlizzard2();
            else if (env.HasBlizzard3)
                yield return AID.Blizzard3;
            else if (env.HasBlizzard1)
                yield return AID.Blizzard1;
            else if (env.HasFire1)
                yield return AID.Fire1;
        }
    }

    private bool ApplyAOEPlannerAction(ref AOEPlannerState state, AID aid)
    {
        if (aid == AID.None)
            return false;
        if (IsAOEThunderAction(aid) && (!state.AllowThunder || !state.Thunderhead))
            return false;
        if (aid is AID.Freeze or AID.Blizzard4 && state.Element >= 0)
            return false;

        if (aid == AID.Transpose)
        {
            if (state.TransposeReadyIn > 0 || state.Element == 0)
                return false;

            if (!PlannerWeave(ref state))
                return false;
            ApplyAOEPlannerElementTransition(ref state, state.Element > 0 ? -1 : 1, fromTranspose: true);
            state.TransposeReadyIn = BLMGameConstants.TransposeRecast;
            return true;
        }

        if (aid == AID.Amplifier)
        {
            if (!ShouldPlanAOEAmplifier(state))
                return false;

            if (!PlannerWeave(ref state))
                return false;
            state.Polyglot = Math.Min(Env.MaxPolyglot, state.Polyglot + 1);
            state.AmplifierReadyIn = BLMGameConstants.AmplifierRecast;
            state.Score += Math.Max(BLMPlannerWeights.AOEAmplifierScoreMin, AOEPolyglotPotency(state.Targets) * BLMPlannerWeights.AOEAmplifierPolyglotScale);
            return true;
        }

        if (aid == AID.Manafont)
        {
            if (!state.AllowManafont || state.ManafontReadyIn > 0 || state.Element <= 0)
                return false;

            if (!PlannerWeave(ref state))
                return false;
            ApplyAOEPlannerElementTransition(ref state, 3, fromTranspose: false);
            ref readonly var manafontEnv = ref Env;
            state.MP = BLMGameConstants.MaxMP;
            state.Hearts = manafontEnv.MaxHearts;
            state.Thunderhead = true;
            state.Paradox = manafontEnv.HasParadox;
            state.ManafontReadyIn = manafontEnv.ManafontRecast;
            state.Score += BLMPlannerWeights.ManafontScore;
            return true;
        }

        var cost = AOEPlannerManaCost(aid, state);
        if (state.MP < cost)
            return false;

        var mpBeforeAction = state.MP;
        var aoeCastTime = AOEPlannerCastTime(aid, state);
        var usesInstant = state.ActiveInstantBudget > 0 && aoeCastTime > 0;
        var elapsed = AOEPlannerActionTime(aid, state);
        state.WeaveSlack = PlannerSlackAfter(elapsed, usesInstant ? 0 : aoeCastTime);
        state.MP -= cost;
        state.Time += elapsed;
        if (!AdvanceAOEPlannerTimers(ref state, elapsed))
            return false;

        if (usesInstant)
            --state.ActiveInstantBudget;
        state.Score += AOEPlannerPotency(aid, state);

        if (aid == AID.Foul)
        {
            state.Polyglot = Math.Max(0, state.Polyglot - 1);
            state.UsedPolyglotRecently = true;
        }
        else if (IsAOEThunderAction(aid))
        {
            state.Thunderhead = false;
            state.ThunderLeft = ThunderDurationFor(aid);
        }
        else if (aid == AID.Paradox)
        {
            state.Paradox = false;
            state.ElementTimer = BLMGameConstants.ElementTimer;
        }
        else if (aid is AID.Freeze or AID.Blizzard4)
        {
            state.Hearts = Env.MaxHearts;
            // A Transpose ice phase stays at UI1: its heart spell restores 2500 MP, not a full UI3 bar.
            var recoveredMP = state.Element switch { -1 => 2500, -2 => 5000, _ => BLMGameConstants.MaxMP };
            state.MP = Math.Min(BLMGameConstants.MaxMP, state.MP + recoveredMP);
        }
        else if (aid is AID.Blizzard3 or AID.Blizzard2 or AID.HighBlizzard2)
        {
            ApplyAOEPlannerElementTransition(ref state, -3, fromTranspose: false);
            state.MP = BLMGameConstants.MaxMP;
        }
        else if (aid == AID.Blizzard1)
        {
            ApplyAOEPlannerElementTransition(ref state, -1, fromTranspose: false);
            state.MP = Math.Min(BLMGameConstants.MaxMP, state.MP + 2500);
        }
        else if (aid == AID.Fire1)
        {
            var consumeHeart = state.Element > 0 && state.Hearts > 0;
            ApplyAOEPlannerElementTransition(ref state, 1, fromTranspose: false);
            if (consumeHeart)
                state.Hearts--;
        }
        else if (aid == AID.Fire2)
        {
            var consumeHeart = state.Element > 0 && state.Hearts > 0;
            ApplyAOEPlannerElementTransition(ref state, 3, fromTranspose: false);
            if (consumeHeart)
                state.Hearts--;
        }
        else if (aid == AID.Flare)
        {
            var hadHearts = state.Hearts > 0;
            ApplyAOEPlannerElementTransition(ref state, 3, fromTranspose: false);
            state.MP = hadHearts ? mpBeforeAction / 3 : 0;
            state.Hearts = 0;
            if (Env.HasFlareStar) // no Astral Soul gauge below Flare Star; see the Fire IV note in ApplyPlannerStep
                state.AstralSoul = Math.Min(6, state.AstralSoul + 3);
        }
        else if (aid == AID.FlareStar)
        {
            state.AstralSoul = 0;
        }

        return true;
    }

    private void ApplyAOEPlannerElementTransition(ref AOEPlannerState state, int nextElement, bool fromTranspose)
    {
        var oldElement = state.Element;
        state.Element = nextElement;
        state.ElementTimer = BLMGameConstants.ElementTimer;
        if (oldElement == 0 && nextElement != 0)
            state.NextPolyglot = BLMGameConstants.PolyglotInterval;
        if (nextElement < 0)
            state.AstralSoul = 0;

        if (oldElement == 0 || Math.Sign(oldElement) != Math.Sign(nextElement))
            state.Thunderhead = true;

        if (oldElement != 0 && Math.Sign(oldElement) != Math.Sign(nextElement))
        {
            ref readonly var env = ref Env;
            if (env.HasParadox)
            {
                if (nextElement < 0 && Math.Abs(oldElement) == 3)
                    state.Paradox = true;
                else if (nextElement > 0 && oldElement == -3 && state.Hearts == env.MaxHearts)
                    state.Paradox = true;
            }
        }
    }

    private bool AdvanceAOEPlannerTimers(ref AOEPlannerState state, float elapsed)
    {
        if (state.Polyglot >= Env.MaxPolyglot && Env.MaxPolyglot > 0)
            state.Score -= elapsed * BLMPlannerWeights.PolyglotCapHoldPenaltyPerSecond;
        state.ThunderLeft = Math.Max(0, state.ThunderLeft - elapsed);
        state.LeyLinesLeft = Math.Max(0, state.LeyLinesLeft - elapsed);
        state.TransposeReadyIn = Math.Max(0, state.TransposeReadyIn - elapsed);
        state.AmplifierReadyIn = Math.Max(0, state.AmplifierReadyIn - elapsed);
        state.ManafontReadyIn = Math.Max(0, state.ManafontReadyIn - elapsed);
        AdvancePlannerRaidBuffs(ref state.RaidBuffsLeft, ref state.RaidBuffsIn, elapsed);

        if (state.DowntimeIn != float.MaxValue)
        {
            state.DowntimeIn -= elapsed;
            if (state.DowntimeIn <= 0)
                return false;
        }

        if (state.Element != 0)
        {
            state.ElementTimer -= elapsed;
            if (state.ElementTimer <= 0)
                return false;
        }

        if (state.Element != 0)
        {
            state.NextPolyglot -= elapsed;
            var maxPolyglot = Env.MaxPolyglot;
            while (state.NextPolyglot <= 0)
            {
                if (state.Polyglot < maxPolyglot)
                    state.Polyglot++;
                else
                    state.Score -= AOEPolyglotPotency(state.Targets);
                state.NextPolyglot += BLMGameConstants.PolyglotInterval;
            }
        }

        return true;
    }

    // Potency per second of the AoE filler (Flare) in the same units as AOEPlannerPotency (no Enochian factor), over the time the planner itself
    // charges for a Flare: its cast time plus caster tax, never less than a GCD.
    private float AOEFillerPPS(int targets) => FlareAOEPotency(targets) * BLMGameConstants.FireMultiplierAF3 / Math.Max(Env.GCDNormal, PlannerCastTimes[5 * 4] + BLMPlannerTuning.CasterTax);

    private float AOEPlannerEvaluation(AOEPlannerState state)
    {
        var horizon = Math.Max(BLMPlannerTuning.AOEPlannerEvaluationMinDuration, state.Horizon);
        var remaining = state.Horizon - state.Time;
        return (state.Score + remaining * AOEFillerPPS(state.Targets) + AOEPlannerTerminalValue(state)) / horizon * 60;
    }

    private float AOEPlannerTerminalValue(AOEPlannerState state)
    {
        ref readonly var env = ref Env;
        var carryScale = state.DowntimeIn < BLMPlannerWeights.STShortDowntimeThreshold ? BLMPlannerWeights.AOEShortDowntimeCarryScale : state.DowntimeIn < BLMPlannerWeights.STMediumDowntimeThreshold ? BLMPlannerWeights.AOEMediumDowntimeCarryScale : BLMPlannerWeights.CarryScaleFull;
        var fillerPPS = AOEFillerPPS(state.Targets);
        var fillerGCD = fillerPPS * env.GCDNormal;
        var value = 0f;
        value += state.AstralSoul * FlareStarAOEPotency(state.Targets) * BLMGameConstants.FireMultiplierAF3 / 6 * carryScale;
        value += state.Polyglot * Math.Max(0, AOEPolyglotPotency(state.Targets) - fillerGCD) * carryScale;
        value += state.Hearts * BLMPlannerWeights.AOEHeartValue;
        // The AoE DoT is applied to the same Math.Min(DotTargets, Targets) enemies the cast was scored against, and
        // ThunderLeft is that shared timer, so the carry scales with the same multiplier. Only the time left AT THE
        // horizon belongs here - ThunderAOEPlannerPotency already banked everything before it.
        // Same no-DoT guard as TerminalStateValue: past the horizon the shift adds, which is only legitimate while the
        // DoT is actually running.
        var thunderPastHorizon = state.ThunderLeft <= 0 ? 0 : Math.Max(0, state.ThunderLeft - (state.Horizon - state.Time));
        value += Math.Min(state.DotTargets, state.Targets) * Math.Min(thunderPastHorizon, BLMPlannerWeights.ThunderTimerTerminalCap) * env.AOEThunderTickPPS;
        value += state.MP / (float)BLMGameConstants.MaxMP * BLMPlannerWeights.MPTerminalValue;
        value += Math.Min(state.ElementTimer, BLMPlannerWeights.ElementTimerTerminalCap) * BLMPlannerWeights.AOEElementTimerTerminalValue;
        value += state.LeyLinesLeft * fillerPPS * (1 - BLMGameConstants.LeyLinesGCDMultiplier);
        value += Math.Clamp(BLMGameConstants.AmplifierRecast - state.AmplifierReadyIn, 0, BLMGameConstants.AmplifierRecast) / BLMGameConstants.AmplifierRecast * BLMPlannerWeights.AmplifierCooldownProgressValue;
        if (state.AmplifierReadyIn <= 0 && state.Polyglot < Env.MaxPolyglot && !WouldAmplifierCauseNearOvercap(state.Polyglot, state.NextPolyglot))
            value -= AOEPolyglotPotency(state.Targets) * BLMPlannerWeights.AOEReadyAmplifierPenaltyScale;
        if (state.RaidBuffsLeft > 0)
            value += Math.Min(state.RaidBuffsLeft, BLMTuning.RaidBuffWindowSeconds) * state.Polyglot * AOEPolyglotPotency(state.Targets) * BLMPlannerWeights.AOERaidBuffPolyglotMultiplier;
        else if (state.RaidBuffsIn <= BLMTuning.RaidBuffWindowSeconds)
            value += (BLMTuning.RaidBuffWindowSeconds - state.RaidBuffsIn) * state.Polyglot * AOEPolyglotPotency(state.Targets) * BLMPlannerWeights.AOEUpcomingRaidBuffPolyglotMultiplier;
        if (state.DowntimeIn < BLMPlannerWeights.DowntimePenaltyThreshold && state.Element > 0)
            value -= BLMPlannerWeights.FireDowntimePenalty;
        return value;
    }

    private bool ShouldPlanAOEFoul(AOEPlannerState state)
    {
        ref readonly var env = ref Env;
        return state.AmplifierReadyIn <= 0 && state.Polyglot >= env.MaxPolyglot
            || state.AmplifierReadyIn <= env.GCDLength
                && state.Polyglot >= env.MaxPolyglot - 1
                && WouldAmplifierCauseNearOvercap(state.Polyglot, state.NextPolyglot)
            || state.DowntimeIn <= env.GCDLength * 2
            || state.Polyglot >= env.MaxPolyglot && state.NextPolyglot <= env.GCDLength
            || state.RaidBuffsLeft > 0 && !state.UsedPolyglotRecently
            || env.MovementPriority
            || state.TransposeReadyIn > env.GCD && state.Element < 0;
    }

    private bool ShouldPlanAOEAmplifier(AOEPlannerState state)
    {
        ref readonly var env = ref Env;
        return env.HasAmplifier
            && state.AllowBurstActions
            && state.AmplifierReadyIn <= 0
            && state.Polyglot < env.MaxPolyglot
            && state.DowntimeIn > BLMTuning.PlannerOGCDMinDowntime
            && (state.Time > 0 || env.CanWeaveAmplifier)
            && !WouldAmplifierCauseNearOvercap(state.Polyglot, state.NextPolyglot);
    }

    private AID BestAOEPlannerIceSpell(AOEPlannerState state)
    {
        ref readonly var env = ref Env;
        return state.Targets >= FreezeBreakpoint && env.HasFreeze ? AID.Freeze
            : env.HasBlizzard4 ? AID.Blizzard4
            : env.HasBlizzard1 ? AID.Blizzard1
            : env.HasFire1 ? AID.Fire1
            : AID.None;
    }

    private int AOEPlannerManaCost(AID aid, AOEPlannerState state)
    {
        int adjustFire(int cost) => state.Element < 0 ? 0 : state.Element > 0 && state.Hearts == 0 ? cost * 2 : cost;
        return aid switch
        {
            AID.Flare => 800,
            AID.Fire1 => adjustFire(BLMGameConstants.FireSpellBaseMP),
            AID.Fire2 or AID.HighFire2 => adjustFire(BLMGameConstants.Fire2MP),
            _ => 0
        };
    }

    private float AOEPlannerActionTime(AID aid, AOEPlannerState state)
    {
        var gcd = PlannerGCD(state.LeyLinesLeft);
        var castTime = AOEPlannerCastTime(aid, state);
        return state.ActiveInstantBudget > 0 || castTime <= 0 ? gcd : Math.Max(gcd, castTime + BLMPlannerTuning.CasterTax);
    }

    private float AOEPlannerCastTime(AID aid, AOEPlannerState state)
        => PlannerCastTime(aid, new PlannerState
        {
            Element = state.Element,
            Thunderhead = state.Thunderhead,
            LeyLinesLeft = state.LeyLinesLeft,
            Time = state.Time,
        });

    private float AOEPlannerPotency(AID aid, AOEPlannerState state)
    {
        var potency = aid switch
        {
            AID.Foul => AOEPolyglotPotency(state.Targets),
            _ when IsAOEThunderAction(aid) => Math.Min(state.DotTargets, state.Targets) * ThunderAOEPlannerPotency(state, aid),
            AID.Freeze => BLMGameConstants.FreezePotency * state.Targets,
            AID.Blizzard3 => 290,
            AID.Blizzard2 => BLMGameConstants.Blizzard2Potency * state.Targets,
            AID.HighBlizzard2 => BLMGameConstants.HighBlizzard2Potency * state.Targets,
            AID.Blizzard4 => BLMGameConstants.Blizzard4Potency,
            AID.Paradox => BLMGameConstants.ParadoxPotency,
            AID.Flare => FlareAOEPotency(state.Targets) * FireMultiplier(state.Element),
            AID.FlareStar => FlareStarAOEPotency(state.Targets) * FireMultiplier(state.Element),
            _ => 0
        };
        return state.RaidBuffsLeft > 0 ? potency * 1.05f : potency;
    }

    // Same model as the single-target ThunderPlannerPotency: only the ticks that land before the common horizon go into
    // Score, minus the ticks of the DoT this cast overwrites, and whatever is still running at the horizon is paid once
    // by AOEPlannerTerminalValue. Note the two sides read the state at different instants: ApplyPlannerStep calls
    // EffectivePotency BEFORE `state.Time += elapsed`, so the single-target version sees pre-cast Time/ThunderLeft/
    // DowntimeIn, while ApplyAOEPlannerAction scores AFTER the advance, so everything here already refers to the moment
    // the cast lands. The arithmetic is identical; only the reference instant differs.
    private static float ThunderAOEPlannerPotency(AOEPlannerState state, AID aid)
    {
        var data = ThunderDataFor(aid);
        var usableDuration = Math.Min(data.Duration, Math.Max(0, Math.Min(state.Horizon - state.Time, state.DowntimeIn)));
        var newTicks = MathF.Floor(usableDuration / BLMGameConstants.LucidDreamingTickInterval);
        var clippedTicks = MathF.Floor(Math.Min(Math.Max(0, state.ThunderLeft), usableDuration) / BLMGameConstants.LucidDreamingTickInterval);
        return data.Initial + Math.Max(0, newTicks - clippedTicks) * data.Dot;
    }

    private static float AOEPolyglotPotency(int targets) => BLMGameConstants.FoulPotency * (1 + BLMGameConstants.FoulFalloffMultiplier * Math.Max(0, targets - 1));
    private static float FlareAOEPotency(int targets) => BLMGameConstants.FlarePotency * (1 + BLMGameConstants.FlareFalloffMultiplier * Math.Max(0, targets - 1));
    private static float FlareStarAOEPotency(int targets) => BLMGameConstants.FlareStarPotency * (1 + BLMGameConstants.FlareStarFalloffMultiplier * Math.Max(0, targets - 1));
    private static float FireMultiplier(int element) => element switch
    {
        3 => BLMGameConstants.FireMultiplierAF3,
        2 => BLMGameConstants.FireMultiplierAF2,
        1 => BLMGameConstants.FireMultiplierAF1,
        _ => 1f
    };

    private void PushAOEPlannerAction(in Strategy strategy, AID aid, Enemy primaryTarget)
    {
        if (aid == AID.None)
            return;

        if (aid == AID.Manafont)
        {
            if (!CanExecuteManafontFromPlanner(strategy, primaryTarget, hasPriorityTarget: true))
            {
                ClearPlannerLock();
                return;
            }

            var manafontPriority = ShouldUseStandard57OpenerControl(strategy) && Standard57AnyManafontWindow(strategy)
                ? GCDPriority.Opener
                : GCDPriority.FuturePlan;
            PushManafont(strategy, manafontPriority, primaryTarget);
            return;
        }

        if (IsAOEThunderAction(aid) && (!HasAOEThunder() || !Thunderhead || !PlannerAllowsThunder(strategy)))
        {
            ClearPlannerLock();
            return;
        }

        if (aid == AID.Transpose && !TransposeReadySoon())
        {
            ClearPlannerLock();
            return;
        }

        // the client refuses this category right now (Silence / Amnesia): the lock would wait on a step that cannot run
        if (IsActionLocked(aid))
        {
            ClearPlannerLock();
            return;
        }

        if (!IsAOEThunderAction(aid) && !Unlocked(aid))
        {
            ClearPlannerLock();
            return;
        }

        if (aid == AID.Flare && Hearts == 0 && AstralSoul < 6)
            PushManafontFinisherCastEnabler(strategy, primaryTarget, aid);

        if (aid is AID.Transpose or AID.Amplifier)
            PushAction(aid, Player, ActionQueue.Priority.High + (int)GCDPriority.FuturePlan, 0);
        else if (aid is AID.Foul or AID.Freeze or AID.Flare or AID.FlareStar or AID.Fire2)
            PushGCD(aid, BestAOETarget ?? primaryTarget, GCDPriority.FuturePlan);
        else if (IsAOEThunderAction(aid))
            PushGCD(BestAOEThunder(), BestAOEThunderTarget ?? BestAOETarget ?? primaryTarget, GCDPriority.FuturePlan);
        else
            PushGCD(aid, primaryTarget, GCDPriority.FuturePlan);
    }

    private void TryInstantCast(
        in Strategy strategy,
        Enemy? primaryTarget,
        GCDPriority prioBase,
        bool useFirestarter = true,
        bool useThunderhead = true,
        bool usePolyglot = true,
        bool preferTriplecast = false,
        bool useCastEnabler = true
    )
    {
        if (Polyglot > 0 && usePolyglot && ShouldSpendPolyglotNow(strategy))
            PushPolyglotGCD(primaryTarget, prioBase + (int)InstantCastPriority.Polyglot);

        var movementCastEnablerRequested = MovementCastEnablerRequested();
        var castEnablerPrio = movementCastEnablerRequested
            ? ForMove(InstantCastPriority.CastEnabler)
            : prioBase;

        if (useCastEnabler && CanUseInstantCastEnabler(strategy) && (!movementCastEnablerRequested || !IsLookAwayActive() && !MovementCastEnablerRetryLocked()) && !HasHigherPriorityMovementInstantBeforeCastEnabler(strategy))
        {
            if (preferTriplecast)
                PushTripleOrSwiftcast(strategy, castEnablerPrio, primaryTarget, movementCastEnabler: movementCastEnablerRequested);
            else
                PushSwiftOrTriplecast(strategy, castEnablerPrio, primaryTarget: primaryTarget, movementCastEnabler: movementCastEnablerRequested);
        }

        if (Firestarter && useFirestarter && Unlocked(AID.Fire3))
            PushGCD(AID.Fire3, primaryTarget, prioBase + (int)InstantCastPriority.Firestarter);

        if (Fire > 0 && Unlocked(AID.Despair) && Unlocked(TraitID.EnhancedAstralFire))
            PushGCD(AID.Despair, primaryTarget, prioBase + (int)InstantCastPriority.Despair, mpCutoff: FireSpellCost);

        if (useThunderhead)
        {
            var thunderPrio = ThunderInstantPriority(strategy);
            if (thunderPrio != GCDPriority.None || SingleThunderRefreshReady(strategy) || AOEThunderRefreshReady(strategy))
                PushThunder(strategy, thunderPrio != GCDPriority.None ? thunderPrio : GCDPriority.DotRefresh);
        }
    }

    private bool TryInstantOrTranspose(in Strategy strategy, Enemy? primaryTarget, bool useThunderhead = true, bool allowIceParadoxBeforeTranspose = true)
    {
        var pushed = false;
        if (useThunderhead)
        {
            var thunderPrio = ThunderInstantPriority(strategy);
            if (thunderPrio != GCDPriority.None || SingleThunderRefreshReady(strategy) || AOEThunderRefreshReady(strategy))
            {
                PushThunder(strategy, thunderPrio != GCDPriority.None ? thunderPrio : GCDPriority.DotRefresh);
                pushed = true;
            }
        }

        var useManafontNow = ShouldUseManafontNow(strategy, primaryTarget, !Hints.PriorityTargetsSpan.IsEmpty);
        if (useManafontNow)
        {
            PushManafont(strategy, GCDPriority.Standard, primaryTarget);
            pushed = true;
        }

        if (!UseAbilities(strategy))
            return pushed;

        if (!useManafontNow && TransposeReadySoon() && Element != 0 && (!allowIceParadoxBeforeTranspose || !ShouldUseIceParadoxBeforeTranspose()))
        {
            PushUtilityOGCD(AID.Transpose, GCDPriority.Standard);
            pushed = true;
        }

        return pushed;
    }

    private void UseLeylines(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Unlocked(AID.LeyLines))
            return;

        if (Player.FindStatus(SID.LeyLines) != null)
            return;

        if (!Player.InCombat)
        {
            if (CountdownRemaining == null)
                return;

            if (Manager.Planner?.Plan == null)
                return;
        }

        var opt = strategy.Leylines;
        var forced = opt.Value == LeylinesStrategy.Force;
        // Movement is the one hold Force does not override. The LLMove track exists precisely to allow automatic Ley
        // Lines while moving and is off by default, so honouring Force here would make that track unreachable - and
        // lines dropped under your feet on the way somewhere else spend a charge on a circle you are about to leave.
        if (ShouldSuppressLeylinesForMovement(strategy))
            return;

        if (!forced && ShouldUseStandard57OpenerControl(strategy) && !CanUseStandard57LeyLines(strategy, primaryTarget))
            return;

        if (!forced && ShouldHoldLeylinesForDyingAdds())
            return;

        if (!forced && ShouldHoldLeylinesForDyingTarget(primaryTarget))
            return;

        var timeline = CurrentTimelineContext();
        if (!forced && ShouldHoldNewLeyLinesForMovement(timeline))
            return;

        var prio = opt.Value switch
        {
            LeylinesStrategy.OpenerOnly => CombatTimer < BLMTuning.OpenerOnlyLeyLinesWindow ? DefaultOGCDPriority : 0,
            LeylinesStrategy.Force => opt.Priority(),
            // A ready Ley Lines is never held back to line up with the next raid buff: the opener use puts the 120 s recast on the
            // even-minute buffs by itself, and every wait cost damage (combat matrix: about +2% over waiting, +1.2..2.0% under irregular
            // stops). The holds above (movement, dying target, downtime) still apply, and so does the Windurst profile's own hold.
            LeylinesStrategy.EvenBurst => WindurstBurstWindowOpen(strategy) || !ShouldHoldForWindurstBurst(strategy, ReadyIn(AID.LeyLines)) ? DefaultOGCDPriority : 0,
            // the planner only decides Ley Lines while it runs the rotation; with Rotation left at its default (or below the
            // planner's level) this option used to never press Ley Lines at all, so it falls back to the even-burst logic there
            LeylinesStrategy.FuturePlanner => (FuturePlannerSelected(strategy) && CanUseHighLevelPlanner() ? ShouldUseLeylinesFuturePlanner(strategy) : ShouldUseLeylinesEvenBurst(strategy)) ? DefaultOGCDPriority : 0,
            _ => 0,
        };

        if (prio > 0)
            PushAction(AID.LeyLines, Player, prio, 0);
    }

    private bool ShouldUseLeylinesEvenBurst(in Strategy strategy)
    {
        if (WindurstBurstWindowOpen(strategy))
            return true;

        if (ShouldHoldForWindurstBurst(strategy, ReadyIn(AID.LeyLines)))
            return false;

        return CombatTimer < BLMTuning.OpenerOnlyLeyLinesWindow
            || RaidBuffsLeft > BLMTuning.BurstWarmupSeconds
            || RaidBuffsLeft <= GCD && RaidBuffsIn <= BLMTuning.RaidBuffWindowSeconds;
    }

    private bool ShouldUseLeylinesFuturePlanner(in Strategy strategy)
    {
        if (!Unlocked(AID.LeyLines)
            || !Player.InCombat
            || !FuturePlannerSelected(strategy)
            || !CanUseHighLevelPlanner()
            || ReadyIn(AID.LeyLines) > GCD)
            return false;

        if (ShouldSuppressLeylinesForMovement(strategy))
            return false;

        if (WindurstBurstWindowOpen(strategy))
            return true;

        if (ShouldHoldForWindurstBurst(strategy, ReadyIn(AID.LeyLines)))
            return false;

        var timeline = CurrentTimelineContext();
        if (ShouldHoldNewLeyLinesForMovement(timeline)
            || timeline.DowntimeIn <= BLMPlannerTuning.LeyLinesUseNowWinMargin && RaidBuffsLeft <= BLMTuning.BurstWarmupSeconds)
            return false;

        var reuseKey = BuildPlannerReuseKey(strategy, allowNewTriplecast: true);
        if (PlanReusable(LeyLinesDecisionValid, LeyLinesDecisionKey, reuseKey, LeyLinesDecisionAt))
            return LeyLinesDecision;

        var useAOEPlanner = UseAOERotation();
        var hold = useAOEPlanner
            ? AOEPlannerEvaluation(FindBestAOEPlannerState(strategy, 0))
            : PlannerEvaluation(FindBestPlannerState(strategy, 0));
        var useNow = useAOEPlanner
            ? AOEPlannerEvaluation(FindBestAOEPlannerState(strategy, BLMPlannerTuning.LeyLinesUseNowWinMargin))
            : PlannerEvaluation(FindBestPlannerState(strategy, BLMPlannerTuning.LeyLinesUseNowWinMargin));
        LeyLinesDecision = useNow > hold + BLMPlannerTuning.LeyLinesUseNowWinMargin;
        LeyLinesDecisionKey = reuseKey;
        LeyLinesDecisionAt = World.CurrentTime;
        LeyLinesDecisionValid = true;
        return LeyLinesDecision;
    }

    private void UseTriplecastForced(in Strategy strategy, Enemy? primaryTarget)
    {
        if (Player.FindStatus(SID.Triplecast) != null)
            return;

        if (strategy.Triplecast == TriplecastStrategy.Force && Unlocked(AID.Triplecast))
            PushAction(AID.Triplecast, Player, strategy.Triplecast.Priority(), 0);
    }

    private void UseSwiftcastForced(in Strategy strategy)
    {
        if (Player.FindStatus(SID.Swiftcast) != null)
            return;

        if (strategy.Swiftcast == SwiftcastStrategy.Force && Unlocked(AID.Swiftcast))
            PushAction(AID.Swiftcast, Player, strategy.Swiftcast.Priority(), 0);
    }

    private bool ManafontAllowedBySetting(in Strategy strategy)
        => Unlocked(AID.Manafont) && strategy.Manafont.Value is OffensiveStrategy.Automatic or OffensiveStrategy.Force;

    private bool ManafontAutomatic(in Strategy strategy)
        => ManafontAllowedBySetting(strategy) && strategy.Manafont.Value == OffensiveStrategy.Automatic;

    private bool ManafontForced(in Strategy strategy)
        => ManafontAllowedBySetting(strategy) && strategy.Manafont.Value == OffensiveStrategy.Force;

    private bool ShouldPrepareAutomaticManafontFinisher(in Strategy strategy, Enemy? primaryTarget, AID finisher)
    {
        if (!UseAbilities(strategy)
            || !ManafontAutomatic(strategy)
            || !AllowAnyCastEnabler(strategy)
            || primaryTarget == null
            || primaryTarget.Actor.PendingDead
            || !Player.InCombat
            || Fire <= 0
            || ActiveInstantCast
            || GetCastTime(finisher) <= GCDLength
            || ReadyIn(AID.Manafont) > GCDLength + BLMPlannerTuning.PlannerWeaveElapsed)
            return false;

        if (ShouldUseStandard57OpenerControl(strategy)
            || ShouldHoldEvenBurstRecoveryAction(CurrentManafontRecast))
            return false;

        var timeline = CurrentTimelineContext();
        return !timeline.DowntimeNow && timeline.DowntimeIn > GCDLength * 2;
    }

    private bool CanUseManafontFinisherCastEnabler(AID aid)
    {
        if (!Unlocked(aid) || ReadyIn(aid) > 0.05f)
            return false;

        var def = ActionDefinitions.Instance[ActionID.MakeSpell(aid)]!;
        return def.Category != ActionCategory.Ability || Player.FindStatus(1092) == null;
    }

    private void PushManafontFinisherCastEnabler(in Strategy strategy, Enemy? primaryTarget, AID finisher)
    {
        if (!ShouldPrepareAutomaticManafontFinisher(strategy, primaryTarget, finisher))
            return;

        var priority = GCDPriority.FuturePlan + (int)InstantCastPriority.CastEnabler;
        if (AllowSwiftcast(strategy) && CanUseManafontFinisherCastEnabler(AID.Swiftcast))
            PushUtilityOGCD(AID.Swiftcast, priority);
        else if (AllowTriplecast(strategy) && CanUseTriplecastForTarget(primaryTarget) && CanUseManafontFinisherCastEnabler(AID.Triplecast))
            PushUtilityOGCD(AID.Triplecast, priority);
    }

    private bool TryPushManafontWeaveFiller(in Strategy strategy, Enemy? primaryTarget, GCDPriority priority)
    {
        if (!ManafontAutomatic(strategy) || primaryTarget == null || CanWeave(AID.Manafont))
            return false;

        var polyglotAction = BestPolyglotGCD();
        if (Polyglot > 0 && polyglotAction != AID.None && GetCastTime(polyglotAction) <= 0 && ShouldSpendPolyglotNow(strategy))
        {
            PushPolyglotGCD(primaryTarget, priority + (int)InstantCastPriority.Polyglot);
            return true;
        }

        if (TryPushThunderRefresh(strategy, priority + (int)InstantCastPriority.TP))
            return true;

        if (CanUseAFParadox(strategy) && MP >= GetManaCost(AID.Paradox))
        {
            PushGCD(AID.Paradox, primaryTarget, priority + (int)InstantCastPriority.Paradox);
            return true;
        }

        if (Firestarter && Unlocked(AID.Fire3))
        {
            PushGCD(AID.Fire3, primaryTarget, priority + (int)InstantCastPriority.Firestarter);
            return true;
        }

        return false;
    }

    private void PushManafont(in Strategy strategy, GCDPriority fallbackPriority, Enemy? primaryTarget = null)
    {
        if (!ManafontAllowedBySetting(strategy))
            return;

        if (ManafontForced(strategy))
            PushAction(AID.Manafont, Player, strategy.Manafont.Priority(), 0);
        else
        {
            TryPushManafontWeaveFiller(strategy, primaryTarget, fallbackPriority);
            PushUtilityOGCD(AID.Manafont, fallbackPriority);
        }
    }

    private bool RotationModeHandoffActive => World.CurrentTime <= RotationModeHandoffUntil;

    private bool CanExecuteManafontFromPlanner(in Strategy strategy, Enemy? primaryTarget, bool hasPriorityTarget)
    {
        if (!ManafontAllowedBySetting(strategy))
            return false;

        if (!Player.InCombat || Fire <= 0 || !CanWeave(AID.Manafont, 1))
            return false;

        if (ManafontForced(strategy))
            return true;

        if (!ManafontAutomatic(strategy))
            return false;

        if (ShouldFinishAFWithDespair(strategy))
            return false;

        if (ShouldHoldEvenBurstRecoveryAction(CurrentManafontRecast))
            return false;

        var timeline = CurrentTimelineContext();
        var hasManafontTarget = primaryTarget != null || hasPriorityTarget;
        if (!hasManafontTarget || timeline.DowntimeNow || timeline.DowntimeIn <= GCDLength * 2)
            return false;

        if (ShouldUseStandard57OpenerControl(strategy))
            return CanUseStandard57Manafont(strategy);

        if (MP < MinAstralFireMP || Standard57AnyManafontWindow(strategy))
            return true;

        return false;
    }

    private bool ShouldUseManafontNow(in Strategy strategy, Enemy? primaryTarget, bool hasPriorityTarget)
    {
        if (!ManafontAllowedBySetting(strategy))
            return false;

        if (!Player.InCombat || Fire <= 0 || !CanWeave(AID.Manafont, 1))
            return false;

        if (ManafontForced(strategy))
            return true;

        if (!ManafontAutomatic(strategy))
            return false;

        if (ShouldFinishAFWithDespair(strategy))
            return false;

        if (ShouldHoldEvenBurstRecoveryAction(CurrentManafontRecast))
            return false;

        var timeline = CurrentTimelineContext();
        var hasManafontTarget = primaryTarget != null || hasPriorityTarget;
        if (!hasManafontTarget || timeline.DowntimeNow || timeline.DowntimeIn <= GCDLength * 2)
            return false;

        var standard57Control = ShouldUseStandard57OpenerControl(strategy);
        if (standard57Control && RotationModeHandoffActive && CanUseStandard57Manafont(strategy))
            return true;

        if (standard57Control && !CanUseStandard57Manafont(strategy))
            return false;

        if (standard57Control)
            return CanUseStandard57Manafont(strategy);

        if (MP < MinAstralFireMP)
            return true;

        return false;
    }

    private bool ShouldUseManafontOnManualToAutoHandoff(in Strategy strategy, Enemy? primaryTarget, bool hasPriorityTarget)
    {
        if (PolyglotOvercapOnly(strategy) || !UseAbilities(strategy))
            return false;

        if (!ManafontAllowedBySetting(strategy))
            return false;

        if (!Player.InCombat || Fire <= 0)
            return false;

        if (!CanWeave(AID.Manafont) && !CanWeave(AID.Manafont, 1))
        {
            LogManualToAutoManafontHandoff($"state=pending fire={Fire} mp={(int)MP} soul={AstralSoul} ready={ReadyIn(AID.Manafont).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} canWeave=false window={Standard57ManafontWindow}");
            return false;
        }

        var timeline = CurrentTimelineContext();
        var hasManafontTarget = primaryTarget != null || hasPriorityTarget;
        if (!hasManafontTarget || timeline.DowntimeNow || timeline.DowntimeIn <= GCDLength * 2)
            return false;

        if (ManafontForced(strategy))
            return true;

        if (!ManafontAutomatic(strategy))
            return false;

        if (ShouldFinishAFWithDespair(strategy))
            return false;

        if (ShouldHoldEvenBurstRecoveryAction(CurrentManafontRecast))
            return false;

        if (ShouldUseStandard57OpenerControl(strategy))
            return CanUseStandard57Manafont(strategy);

        return MP < MinAstralFireMP;
    }

    private bool UseAbilities(in Strategy strategy) => strategy.Rotation != RotationStrategy.PolyglotOvercapOnly;

    private float CurrentManafontRecast => Unlocked(TraitID.EnhancedManafont) ? BLMGameConstants.ManafontRecast : BLMGameConstants.ManafontPreEnhancedRecast;

    private float ManafontGCDWindow => Math.Max(GCD, GCDLength);

    private bool PolyglotOvercapOnly(in Strategy strategy) => strategy.Rotation == RotationStrategy.PolyglotOvercapOnly;

    private bool AllowSwiftcast(in Strategy strategy) => strategy.Swiftcast == SwiftcastStrategy.Automatic;
    private bool AllowTriplecast(in Strategy strategy) => strategy.Triplecast == TriplecastStrategy.Automatic;

    // Anywhere the module only needs "some instant cast is available", either charge will do, so one track being off must not
    // disable the other. This is what used to read the Triplecast track for both.
    private bool AllowAnyCastEnabler(in Strategy strategy) => AllowSwiftcast(strategy) || AllowTriplecast(strategy);

    private bool Standard57OpenerActive => Player.InCombat && CombatTimer < BLMTuning.Standard57OpenerWindow && Unlocked(AID.FlareStar);
    private bool Standard57PostManafont => Standard57OpenerActive && Standard57ManafontObserved;
    // The Standard 5+7 opener is a fixed script: Manafont, Ley Lines and the Polyglot spends sit at set points of the first
    // Astral Fire. Whether a loss rules it out is decided when it starts (the first GCD of the pull): a loss already due within
    // DowntimeSoonSeconds keeps the old behaviour, no script and the downtime-aware rotation plays the pull (with
    // FuturePlanner that is where the hints earn the most: long losses 12 s into the pull). A loss that comes into range after
    // the script started used to break it mid-way instead: DowntimeSoon ended it, the HoldBurst two GCDs out blocked Manafont
    // and Ley Lines, and the steady-state rotation took over with Manafont ready and Astral Soul half built, closing Astral
    // Fire into ice (Despair, Transpose, Blizzard III) or holding Ley Lines right before the loss. Every opening scenario with
    // a loss 17-32 s into the pull lost 270-1,820 potency that way against running the script through, which is what the
    // rotation does without hints (the loss interrupts one cast, the script resumes after it). So a script that started clear
    // hides the upcoming losses that start inside its window. A running loss and its return stay visible, and so does a loss
    // starting after the window (hiding those as well cost FuturePlanner 0.07% on the combat matrix's 60 s warmups). The
    // planners keep seeing every loss (TimelineContext.PlannerDowntimeIn). The conditions are those of
    // ShouldUseStandard57Timeline that do not read the timeline (it reads this flag through CurrentTimelineContext).
    private void UpdateStandard57ScriptHidesUpcomingLoss(in Strategy strategy)
    {
        Standard57ScriptHidesUpcomingLoss = false;
        if (!Standard57OpenerActive)
        {
            Standard57ScriptStartedClear = true;
            return;
        }

        var scriptCanRun = UseAbilities(strategy)
            && ManafontAllowedBySetting(strategy)
            && NumAOETargets < AOEBreakpoint
            && !EvenBurstRecoveryActive
            && World.CurrentTime > Standard57BurstInterruptionSuppressedUntil;
        if (!scriptCanRun)
            return;

        // the first GCD of the pull counts as the start: a timeline hint synced on the combat start can land a few frames late
        if (CombatTimer < GCDLength && CurrentTimelineContext().DowntimeSoon)
            Standard57ScriptStartedClear = false;
        Standard57ScriptHidesUpcomingLoss = Standard57ScriptStartedClear;
    }

    private bool ShouldUseStandard57Timeline(in Strategy strategy)
    {
        if (!UseAbilities(strategy) || !ManafontAllowedBySetting(strategy))
            return false;

        var timeline = CurrentTimelineContext();
        return !EvenBurstRecoveryActive && World.CurrentTime > Standard57BurstInterruptionSuppressedUntil && Standard57OpenerActive && NumAOETargets < AOEBreakpoint && !timeline.DowntimeSoon && !timeline.ForcedMoveSoon;
    }
    private bool ShouldHoldTransposeForStandard57Manafont(in Strategy strategy) => ShouldUseStandard57Timeline(strategy) && Fire > 0 && MP < MinAstralFireMP && AstralSoul < 6 && ReadyIn(AID.Manafont) <= ManafontGCDWindow;
    private bool ShouldSkipStandard57AFParadox(in Strategy strategy) => ShouldUseStandard57Timeline(strategy) && Fire == 3 && AstralSoul < 6;
    private bool ShouldUseStandard57IceReentry(in Strategy strategy) => ShouldUseStandard57Timeline(strategy) && Standard57PostManafont && Unlocked(AID.Blizzard4);
    private bool ShouldHoldStandard57IceForTranspose(in Strategy strategy) => ShouldUseStandard57IceReentry(strategy) && Ice > 0 && Hearts == MaxHearts && AlmostMaxMP && Firestarter && !Paradox;
    private bool ShouldUseStandard57AFParadoxReentry(in Strategy strategy) => ShouldUseStandard57Timeline(strategy) && Standard57PostManafont && Fire is > 0 and < 3 && ShouldUseParadoxGCD();
    private bool ShouldUseStandard57OpenerControl(in Strategy strategy) => ShouldUseStandard57Timeline(strategy) && !Standard57PostManafont;
    private bool ShouldUseStandard57OpenerRoute(in Strategy strategy) => ShouldUseStandard57Timeline(strategy);
    private bool Standard57ManafontWindow => Fire > 0 && AstralSoul >= 5 && MP < FireSpellCost;
    private bool Standard57NoHeartManafontRecoveryWindow(in Strategy strategy)
        => ShouldUseStandard57OpenerControl(strategy)
        && Fire > 0
        && MaxHearts > 0
        && Hearts == 0
        && AstralSoul == 4
        && MP < FireSpellCost;
    private bool Standard57AnyManafontWindow(in Strategy strategy) => Standard57ManafontWindow || Standard57NoHeartManafontRecoveryWindow(strategy);
    private bool CanUseStandard57LeyLines(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!ShouldUseStandard57OpenerControl(strategy) || Standard57Fire4Count <= 0)
            return false;

        if (strategy.Leylines.Value == LeylinesStrategy.Delay || HaveLeyLines || !Unlocked(AID.LeyLines) || !CanWeave(AID.LeyLines))
            return false;

        if (ShouldSuppressLeylinesForMovement(strategy) || ShouldHoldLeylinesForDyingTarget(primaryTarget))
            return false;

        var timeline = CurrentTimelineContext();
        return !ShouldHoldNewLeyLinesForMovement(timeline);
    }

    private void UpdateStandard57ManafontWindowState(in Strategy strategy)
    {
        if (!Standard57AnyManafontWindow(strategy))
        {
            Standard57ManafontWindowEnteredAt = default;
            return;
        }

        if (Standard57ManafontWindowEnteredAt == default)
            Standard57ManafontWindowEnteredAt = World.CurrentTime;
    }

    private bool Standard57XenoBeforeManafontBlockedTooLong()
        => Standard57ManafontWindowEnteredAt != default
        && (World.CurrentTime - Standard57ManafontWindowEnteredAt).TotalSeconds >= BLMTuning.Standard57ManafontXenoWaitTimeout;

    private bool Standard57ManafontWindowExpiringSoon()
        => Fire > 0
        && Standard57ManafontWindow
        && ElementTimerLeft <= GCDLength * 2;

    private bool CanUseStandard57Manafont(in Strategy strategy)
    {
        if (AstralSoul == 6 && Unlocked(AID.FlareStar))
            return false;

        if (!Standard57AnyManafontWindow(strategy))
            return Fire > 0 && MP < MinAstralFireMP;

        if (Standard57NoHeartManafontRecoveryWindow(strategy))
            return true;

        return Standard57ManafontWindow
            && (Standard57XenoBeforeManafontSpent
                || Polyglot == 0
                || Standard57ManafontWindowExpiringSoon()
                || Standard57XenoBeforeManafontBlockedTooLong());
    }
    private bool ShouldSpendStandard57XenoBeforeManafont(in Strategy strategy)
        => Unlocked(AID.Xenoglossy)
        && ManafontAutomatic(strategy)
        && ShouldUseStandard57OpenerControl(strategy)
        && Standard57ManafontWindow
        && AstralSoul < 6
        && !Standard57NoHeartManafontRecoveryWindow(strategy)
        && Polyglot > 0
        && !Standard57XenoBeforeManafontSpent;

    private void TryStandard57OpenerGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!ShouldUseStandard57OpenerControl(strategy) || primaryTarget == null)
            return;

        if (!Standard57ThunderObserved && PlannerAllowsThunder(strategy) && Thunderhead && AstralSoul == 0 && DotExpiring(TargetThunderLeft))
        {
            PushGCD(BestPlannerThunder(), BestThunderTarget ?? primaryTarget, GCDPriority.Opener);
            if (AllowNonMovementCastEnabler(strategy) && AllowSwiftcast(strategy) && Unlocked(AID.Swiftcast) && CanWeave(AID.Swiftcast))
                PushUtilityOGCD(AID.Swiftcast, GCDPriority.Opener);
            if (!ShouldHoldForWindurstBurst(strategy, ReadyIn(AID.Amplifier))
                && Unlocked(AID.Amplifier)
                && CanWeave(AID.Amplifier))
                PushUtilityOGCD(AID.Amplifier, GCDPriority.Opener);
            return;
        }

        if (Standard57ThunderObserved && !Standard57SwiftcastObserved && AllowNonMovementCastEnabler(strategy) && AllowSwiftcast(strategy) && Unlocked(AID.Swiftcast) && CanWeave(AID.Swiftcast))
            PushUtilityOGCD(AID.Swiftcast, GCDPriority.Opener);

        if (Standard57ThunderObserved
            && !Standard57AmplifierObserved
            && !ShouldHoldForWindurstBurst(strategy, ReadyIn(AID.Amplifier))
            && Unlocked(AID.Amplifier)
            && CanWeave(AID.Amplifier))
            PushUtilityOGCD(AID.Amplifier, GCDPriority.Opener);

        if (ShouldSpendStandard57XenoBeforeManafont(strategy))
            PushGCD(AID.Xenoglossy, primaryTarget, GCDPriority.Opener);
    }

    private void ObserveStandard57OpenerCast()
    {
        if (!Standard57OpenerActive)
        {
            ResetStandard57OpenerState();
            return;
        }

        var cast = Manager.LastCast.Data;
        if (cast == null || cast.SourceSequence == 0 || cast.SourceSequence == Standard57OpenerObservedSequence)
            return;

        Standard57OpenerObservedSequence = cast.SourceSequence;
        if (cast.IsSpell(AID.Thunder1) || cast.IsSpell(AID.Thunder3) || cast.IsSpell(AID.HighThunder))
            Standard57ThunderObserved = true;

        if (cast.IsSpell(AID.Swiftcast))
            Standard57SwiftcastObserved = true;

        if (cast.IsSpell(AID.Amplifier))
            Standard57AmplifierObserved = true;

        if (cast.IsSpell(AID.Fire4) && !Standard57ManafontObserved)
            ++Standard57Fire4Count;

        if (cast.IsSpell(AID.Manafont))
        {
            Standard57ManafontObserved = true;
            ClearManualToAutoManafontHandoff("observed");
        }

        if (!Standard57PostManafont && (cast.IsSpell(AID.Xenoglossy) || cast.IsSpell(AID.Foul)))
            Standard57XenoBeforeManafontSpent = true;
    }

    private void ResetStandard57OpenerState()
    {
        Standard57OpenerObservedSequence = 0;
        Standard57ThunderObserved = false;
        Standard57SwiftcastObserved = false;
        Standard57AmplifierObserved = false;
        Standard57Fire4Count = 0;
        Standard57XenoBeforeManafontSpent = false;
        Standard57ManafontObserved = false;
        Standard57ManafontWindowEnteredAt = default;
    }

    private void SyncStandard57OpenerStateForHandoff(in Strategy strategy, bool fromManualControl)
    {
        if (strategy.Rotation.Value == RotationStrategy.PolyglotOvercapOnly || !ShouldUseStandard57Timeline(strategy))
        {
            ResetStandard57OpenerState();
            return;
        }

        Standard57OpenerObservedSequence = Manager.LastCast.Data?.SourceSequence ?? 0;

        // When returning from manual/overcap-only control mid-opener, rebuild the opener guards from current resources.
        // Otherwise the module can incorrectly require a pre-Manafont Xenoglossy that was already spent manually.
        if (fromManualControl)
        {
            if (ReadyIn(AID.Manafont) > GCD)
                Standard57ManafontObserved = true;

            if (Standard57ManafontObserved || Polyglot == 0 || MP < MinAstralFireMP)
                Standard57XenoBeforeManafontSpent = true;
        }
    }

    private void ResetInstantB3TransposeReentry()
    {
        InstantB3TransposeReentryActive = false;
        InstantB3TransposeReentryUsedSwiftcast = false;
    }

    private bool ShouldCompleteInstantB3TransposeReentry()
        => InstantB3TransposeReentryActive && Ice > 0 && Hearts == MaxHearts && Unlocked(AID.Paradox);

    private bool ShouldForceSwiftInstantB3ReentryParadox()
        => ShouldCompleteInstantB3TransposeReentry() && InstantB3TransposeReentryUsedSwiftcast && Paradox;

    private bool ShouldUseIceParadoxBeforeTranspose()
        => Unlocked(AID.Paradox) && Paradox && TransposeReadySoon() && Ice > 0 && Hearts == MaxHearts && AlmostMaxMP;

    private bool ShouldUseParadoxGCD(bool allowWithFirestarter = false)
        => Unlocked(AID.Paradox) && Paradox && (!Firestarter || allowWithFirestarter);

    private bool RecentObservedSpell(AID aid, float maxAge = BLMTuning.RecentSpellWindowSeconds)
    {
        var action = ActionID.MakeSpell(aid);
        var cast = Manager.LastCast.Data;
        return cast != null && (cast.IsSpell(aid) || cast.Action == action) && (World.CurrentTime - Manager.LastCast.Time).TotalSeconds <= maxAge;
    }

    private bool AllowNonMovementCastEnabler(in Strategy strategy)
        => UseAbilities(strategy)
        && !MovementSkillPriorityRequested()
        && AllowAnyCastEnabler(strategy);

    private bool CanUseInstantCastEnabler(in Strategy strategy) => UseAbilities(strategy) && AllowAnyCastEnabler(strategy) && !ActiveInstantCast && !CanFitGCD(InstantCastLeft) && CanUseCastEnablerBeforeFlareStar();

    private bool FlareStarPendingForInstantEnabler()
        => Unlocked(AID.FlareStar)
        && Fire > 0
        && (AstralSoul == 6
            || AstralSoul == 5 && MP >= FireSpellCost && Unlocked(AID.Despair)
            || UseAOERotation() && AstralSoul >= 3 && MP >= 800 && Unlocked(AID.Flare));

    private bool CanUseCastEnablerBeforeFlareStar()
        => MovementSkillPriorityRequested() || !FlareStarPendingForInstantEnabler();

    private bool MovementCastEnablerRequested()
    {
        if (ForecastForcedMoveImminent())
            return true;

        if (!ActionManagerEx.Config.PreventMovingWhileCasting)
            return false;

        if (MovementOverride.Instance?.IsForceUnblocked() == true)
            MovementKeyRequestUntil = World.FutureTime(BLMTuning.MovementKeyGraceSeconds);

        return World.CurrentTime <= MovementKeyRequestUntil;
    }

    // A telegraphed mechanic is about to displace us for long enough to drop a GCD, which is the ForcedMoveSoon case for instants:
    // spend a cast enabler so the next GCD lands during the move. Ley Lines and burst holds deliberately ignore this.
    private bool ForecastForcedMoveImminent()
    {
        var forecast = Hints.Disengage;
        return forecast.ForcedMoveIn <= GCD + BLMTuning.ForecastMoveEnablerLead && forecast.ForcedMoveFor >= BLMTuning.ForecastMoveEnablerMinMove;
    }

    // While the character is actually moving, an instant is the only GCD that can go out, so prefer one even when no cast enabler was
    // requested. The cast enabler itself deliberately keeps its own, narrower trigger: a charge is too expensive for a short step.
    private bool MovementSkillPriorityRequested()
        => IsMoving || MovementCastEnablerRequested();

    private bool ShouldSuppressLeylinesForMovement(in Strategy strategy)
        => !strategy.LLMove.IsEnabled()
        && (IsMoving || MovementRequested || MovementCastEnablerRequested());

    private bool MovementCastEnablerRetryLocked()
        => !ActiveInstantCast && World.CurrentTime < MovementCastEnablerRetryAfter;

    private void MarkMovementCastEnablerPushed()
        => MovementCastEnablerRetryAfter = World.FutureTime(BLMTuning.MovementCastEnablerRetryLockoutSeconds);

    private bool ShouldUseMovementCastEnabler(in Strategy strategy) => CanUseInstantCastEnabler(strategy) && MovementCastEnablerRequested() && !IsLookAwayActive() && !MovementCastEnablerRetryLocked() && !HasHigherPriorityMovementInstantBeforeCastEnabler(strategy);

    private bool IsLookAwayActive()
        => TryGetLookAwayFacing(out _);

    private bool TryGetLookAwayFacing(out Angle facing)
    {
        facing = Player.Rotation;
        var deadline = World.FutureTime(0.5f);
        var haveForbiddenDirection = false;

        foreach (var d in Hints.ForbiddenDirections)
        {
            if (d.activation <= deadline)
            {
                haveForbiddenDirection = true;
                break;
            }
        }

        if (!haveForbiddenDirection)
            return false;

        if (!LookAwayFacingForbidden(facing, deadline))
            return true;

        foreach (var d in Hints.ForbiddenDirections)
        {
            if (d.activation > deadline)
                continue;

            var margin = 5f.Degrees();
            var candidate = d.center + d.halfWidth + margin;
            if (!LookAwayFacingForbidden(candidate, deadline))
            {
                facing = candidate;
                return true;
            }

            candidate = d.center - d.halfWidth - margin;
            if (!LookAwayFacingForbidden(candidate, deadline))
            {
                facing = candidate;
                return true;
            }

            candidate = d.center + 180f.Degrees();
            if (!LookAwayFacingForbidden(candidate, deadline))
            {
                facing = candidate;
                return true;
            }
        }

        return true;
    }

    private bool LookAwayFacingForbidden(Angle facing, DateTime deadline)
    {
        foreach (var d in Hints.ForbiddenDirections)
        {
            if (d.activation <= deadline && (facing - d.center).Normalized().Abs().Rad <= d.halfWidth.Rad)
                return true;
        }

        return false;
    }

    private bool ShouldSuppressHardcastForMovement(AID aid)
        => MovementCastEnablerRequested()
        && GetCastTime(aid) > 0
        && ActionManagerEx.Config.PreventMovingWhileCasting;

    private void PushUtilityOGCD(AID aid, GCDPriority priority)
    {
        if (!Unlocked(aid))
            return;

        PushAction(aid, Player, ActionQueue.Priority.High + Math.Min((int)priority, MaxModuleGCDPriority), 0);
    }

    // Highest priority the module may add on top of ActionQueue.Priority.High; ManualGCD is High + 999.
    private const int MaxModuleGCDPriority = 998;

    private bool AllowTriplecastForDoubleTranspose(in Strategy strategy) => AllowTriplecast(strategy);

    private bool AllowTriplecastForCastEnabler(in Strategy strategy) => AllowTriplecast(strategy) && (AllowTriplecastForDoubleTranspose(strategy) || ShouldUseStandard57IceReentry(strategy));

    private bool AllowTriplecastForTransposeB3(in Strategy strategy) => AllowTriplecast(strategy);

    private bool CanUseCastEnablerAction(in Strategy strategy, AID aid, bool movementCastEnabler)
    {
        // Both push helpers funnel through here, so this is the one place the per-action track has to be honoured.
        if (!(aid == AID.Swiftcast ? AllowSwiftcast(strategy) : AllowTriplecast(strategy)))
            return false;

        if (IsActionLocked(aid))
            return false;

        if (!movementCastEnabler)
            return CanWeave(aid);

        return Unlocked(aid) && ReadyIn(aid) <= 0.05f;
    }

    private void PushSwiftOrTriplecast(in Strategy strategy, GCDPriority prioBase, bool allowTriplecast = true, Enemy? primaryTarget = null, bool movementCastEnabler = false)
    {
        if (CanUseCastEnablerAction(strategy, AID.Swiftcast, movementCastEnabler))
        {
            PushUtilityOGCD(AID.Swiftcast, prioBase);
            if (movementCastEnabler)
                MarkMovementCastEnablerPushed();
        }
        else if (allowTriplecast && CanUseTriplecastForTarget(primaryTarget) && AllowTriplecastForCastEnabler(strategy) && CanUseCastEnablerAction(strategy, AID.Triplecast, movementCastEnabler))
        {
            PushUtilityOGCD(AID.Triplecast, prioBase);
            if (movementCastEnabler)
                MarkMovementCastEnablerPushed();
        }
    }

    private void PushTripleOrSwiftcast(in Strategy strategy, GCDPriority prioBase, Enemy? primaryTarget = null, bool movementCastEnabler = false) => PushTripleOrSwiftcast(strategy, prioBase, AllowTriplecastForTransposeB3(strategy), primaryTarget, movementCastEnabler);

    private void PushTripleOrSwiftcast(in Strategy strategy, GCDPriority prioBase, bool allowTriplecast, Enemy? primaryTarget = null, bool movementCastEnabler = false)
    {
        // Triplecast first (this is the preferTriplecast path); Swiftcast is the fallback.
        if (allowTriplecast && CanUseTriplecastForTarget(primaryTarget) && CanUseCastEnablerAction(strategy, AID.Triplecast, movementCastEnabler))
        {
            PushUtilityOGCD(AID.Triplecast, prioBase);
            if (movementCastEnabler)
                MarkMovementCastEnablerPushed();
        }
        else if (CanUseCastEnablerAction(strategy, AID.Swiftcast, movementCastEnabler))
        {
            PushUtilityOGCD(AID.Swiftcast, prioBase);
            if (movementCastEnabler)
                MarkMovementCastEnablerPushed();
        }
    }

    private AID SelectInstantB3Action(in Strategy strategy, int extraGCDs = 0, Enemy? primaryTarget = null)
    {
        if (!UseAbilities(strategy) || !AllowAnyCastEnabler(strategy) || !CanUseCastEnablerBeforeFlareStar())
            return AID.None;

        if (AllowSwiftcast(strategy) && Unlocked(AID.Swiftcast) && CanWeave(AID.Swiftcast, extraGCDs) && !IsActionLocked(AID.Swiftcast))
            return AID.Swiftcast;

        var canTriplecast = CanUseTriplecastForTarget(primaryTarget) && AllowTriplecastForTransposeB3(strategy) && Unlocked(AID.Triplecast) && CanWeave(AID.Triplecast, extraGCDs) && !IsActionLocked(AID.Triplecast);
        return canTriplecast ? AID.Triplecast : AID.None;
    }

    private bool CanInstantB3(in Strategy strategy, int extraGCDs = 0, Enemy? primaryTarget = null)
        => SelectInstantB3Action(strategy, extraGCDs, primaryTarget) != AID.None;

    // Hardcast GCDs the ice re-entry is about to pay for: Blizzard III always, plus the Blizzard IV that refills the
    // Umbral Hearts whenever they are missing.
    private int IceReentryInstantsWanted()
        => Unlocked(AID.Blizzard4) && Hearts < MaxHearts ? 2 : 1;

    // Instant-cast stacks the ice re-entry can actually draw on: whatever is already running, or - when nothing is -
    // the one reservation SelectInstantB3Action is about to press for it, which is one stack for Swiftcast and three
    // for a fresh Triplecast. Asking the selector rather than "is any instant available" keeps this honest: the
    // reentry gets whatever that call will really hand it.
    private int AvailableIceReentryInstantStacks(in Strategy strategy, Enemy? primaryTarget)
    {
        var stacks = 0;
        if (ActiveTriplecast)
            stacks += Math.Max(1, Triplecast.Stacks);
        if (ActiveSwiftcast)
            ++stacks;
        if (stacks > 0)
            return stacks;

        return SelectInstantB3Action(strategy, primaryTarget: primaryTarget) switch
        {
            AID.Triplecast => 3,
            AID.Swiftcast => 1,
            _ => 0
        };
    }

    private AID SelectElementSwapInstantAction(in Strategy strategy, int extraGCDs = 0, Enemy? primaryTarget = null)
        => SelectInstantB3Action(strategy, extraGCDs, primaryTarget);

    private bool ShouldSuppressTransitionElementSwapHardcast(AID aid)
    {
        if (!MovementCastEnablerRequested())
            return false;

        if (!ElementSwapHardcastTransitionActive)
            return false;

        if (ElementSwapHardcastSuppressionExcludedState)
            return false;

        if (aid is not AID.Blizzard3 and not AID.Fire3)
            return false;

        if (aid == AID.Fire3 && Firestarter)
            return false;

        if (ActiveInstantCast || CanFitGCD(InstantCastLeft))
            return false;

        return true;
    }

    private bool ShouldPreferAOEToSingleElementSwapInstant(AID aid)
        => AOEToSingleTransitionActive
        && aid is AID.Blizzard3 or AID.Fire3
        && !(aid == AID.Fire3 && Firestarter)
        && !ActiveInstantCast
        && !CanFitGCD(InstantCastLeft);

    private bool TryPushTransitionElementSwapGCD(in Strategy strategy, AID aid, Enemy? target, GCDPriority priority, int mpCutoff = int.MaxValue)
    {
        var suppressHardcast = ShouldSuppressTransitionElementSwapHardcast(aid);
        var preferAOEToSingleInstant = ShouldPreferAOEToSingleElementSwapInstant(aid);
        if (!suppressHardcast && !preferAOEToSingleInstant)
        {
            PushGCD(aid, target, priority, mpCutoff: mpCutoff);
            return true;
        }

        var instantAction = SelectElementSwapInstantAction(strategy, primaryTarget: target);
        if (instantAction == AID.None)
        {
            if (!suppressHardcast)
            {
                PushGCD(aid, target, priority, mpCutoff: mpCutoff);
                return true;
            }

            return false;
        }

        if (MP >= mpCutoff)
            return true;

        if (!CanQueueGCD(aid))
            return false;

        PushUtilityOGCD(instantAction, priority);
        return true;
    }

    private AID PushInstantB3(in Strategy strategy, GCDPriority priority, Enemy? primaryTarget = null)
    {
        if (ActiveInstantCast || CanFitGCD(InstantCastLeft))
            return AID.None;

        var instantAction = SelectInstantB3Action(strategy, primaryTarget: primaryTarget);
        if (instantAction == AID.None)
            return AID.None;

        PushUtilityOGCD(instantAction, priority);
        return instantAction;
    }

    private bool ShouldTranspose(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!TransposeReadySoon())
            return false;

        if (!Unlocked(AID.Fire3))
            return Fire > 0 && MP < 1600 || Ice > 0 && MP > Player.HPMP.MaxMP * 0.9f;

        if (UseAOERotation())
            return
                // AF: transpose if we can't flare or flare star
                AstralSoul < 6 && Fire > 0 && MP < 800
                // UI: transpose after the Freeze/Blizzard IV heart GCD and enough MP to double flare
                || Ice > 0 && Hearts == MaxHearts && MP >= 2400;

        if (ShouldHoldTransposeForStandard57Manafont(strategy))
            return false;

        if (ShouldUseIceParadoxBeforeTranspose())
            return false;

        // fire phase transpose: only swap before B3 if B3 can actually be instant
        if (Fire > 0 && MP < MinAstralFireMP && AstralSoul < 6)
        {
            if (!Unlocked(AID.FlareStar) && Unlocked(AID.Blizzard3))
                return false;

            // Manafont is about to refill the bar: fill the remaining GCDs with instants instead of swapping to ice.
            // This oGCD runs ahead of the planner path, so the hold decision has to be honoured here as well.
            if (ShouldHoldAFForNearManafont(strategy, primaryTarget))
                return false;

            // Leaving Astral Fire is only worth it when the whole re-entry can be instant, not just its first GCD:
            // one stack makes Blizzard III instant and then hardcasts the Blizzard IV behind it anyway. Reading this
            // as "any instant will do" is what let the level-94 Swiftcast recast end fire phases early - Swiftcast is
            // simply up far more often than a fire phase should end - costing the Despair and the Manafont woven
            // around it (2026-09-22: 14% more Transposes, 16% fewer Manafonts, 26% fewer Despairs).
            return AvailableIceReentryInstantStacks(strategy, primaryTarget) >= IceReentryInstantsWanted();
        }

        // ice phase transpose
        var haveInstantFire = Firestarter && Ice > 0 // we have a firestarter
            || Ice == 3 && Unlocked(AID.Paradox) // transpose will give us firestarter
            || ActiveInstantCast; // use tc/sc to cast regular f3

        if (!InstantB3TransposeReentryActive && Ice > 0 && Hearts == MaxHearts && AlmostMaxMP && Firestarter && !Paradox)
            return true;

        if (InstantB3TransposeReentryActive && Ice > 0 && Hearts == MaxHearts && Firestarter && !Paradox)
            return true;

        return Hearts == MaxHearts && AlmostMaxMP && haveInstantFire && !Paradox;
    }

    // A Transpose the client would refuse (Amnesia) is not "ready": the swap lines then take their hardcast fallbacks.
    private bool TransposeReadySoon()
        => Unlocked(AID.Transpose) && ReadyIn(AID.Transpose) <= GCD && !IsActionLocked(AID.Transpose);

    private static bool IsPhantomGCD(AID aid)
        => aid == (AID)PhantomID.Zeninage
        || aid == (AID)PhantomID.Iainuki
        || aid == (AID)PhantomID.OccultQuick
        || aid == (AID)PhantomID.OccultComet;

    private bool CanQueueGCD(AID aid)
        => aid != AID.None
        && (IsPhantomGCD(aid) || Unlocked(aid));

    private void PushGCD(AID aid, Enemy? target, GCDPriority priority, float delay = 0, int mpCutoff = int.MaxValue)
    {
        if (!CanQueueGCD(aid))
            return;

        if (ShouldSuppressHardcastForMovement(aid))
            return;

        if (MP >= GetManaCost(aid))
        {
            if (MP >= mpCutoff)
                return;
            base.PushGCD(aid, target, (GCDPriority)Math.Min((int)priority, MaxModuleGCDPriority), delay);
        }
    }
}
