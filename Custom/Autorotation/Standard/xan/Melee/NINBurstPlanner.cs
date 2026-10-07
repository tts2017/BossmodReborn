using AID = BossMod.NIN.AID;

namespace BossMod.Autorotation.xan;

public enum NinPotencyVariant
{
    Base,
    Combo,
    Positional,
    ComboPositional,
    Meisui
}

// 7.5 potencies as the client describes them (ffxivgame.ver 2026.09.15), including the lower-level branches of each description.
// The harness command potency-audit re-reads every value from the action sheets, so a patch that moves one fails there instead of
// silently skewing the burst planner.
public static class NinPotency
{
    public const float KassatsuBonus = 1.30f;
    public const float KunaiBonus = 1.10f;
    public const float DokumoriBonus = 1.05f;
    public const float KazematoiBonus = 100;
    public const float BunshinMelee = 160;
    public const float BunshinArea = 80;

    public static float Of(AID aid, int level, NinPotencyVariant variant = NinPotencyVariant.Base) => aid switch
    {
        AID.SpinningEdge => level >= 94 ? 300 : level >= 84 ? 220 : 180,
        AID.GustSlash => variant == NinPotencyVariant.Combo
            ? level >= 94 ? 400 : level >= 84 ? 320 : level >= 74 ? 280 : 260
            : level >= 94 ? 240 : level >= 84 ? 160 : level >= 74 ? 120 : 100,
        AID.AeolianEdge => variant switch
        {
            NinPotencyVariant.Combo => level >= 94 ? 400 : level >= 74 ? 320 : 280,
            NinPotencyVariant.ComboPositional => level >= 94 ? 460 : level >= 74 ? 380 : 340,
            NinPotencyVariant.Positional => level >= 94 ? 280 : level >= 74 ? 200 : 160,
            _ => level >= 94 ? 220 : level >= 74 ? 140 : 100
        },
        AID.ArmorCrush => variant switch
        {
            NinPotencyVariant.Combo => level >= 94 ? 440 : level >= 74 ? 340 : 300,
            NinPotencyVariant.ComboPositional => level >= 94 ? 500 : level >= 74 ? 400 : 360,
            NinPotencyVariant.Positional => level >= 94 ? 300 : level >= 74 ? 200 : 160,
            _ => level >= 94 ? 240 : level >= 74 ? 140 : 100
        },
        AID.DeathBlossom => 100,
        AID.HakkeMujinsatsu => variant == NinPotencyVariant.Combo ? 120 : 100,
        AID.ThrowingDagger => level >= 84 ? 200 : 120,
        AID.Mug => 150,
        AID.Dokumori => 400,
        AID.TrickAttack => variant == NinPotencyVariant.Positional ? 400 : 300,
        AID.KunaisBane => 700,
        AID.FumaShuriken or AID.FumaTen or AID.FumaChi or AID.FumaJin => level >= 94 ? 500 : 450,
        AID.Raiton or AID.TCJRaiton => level >= 94 ? 740 : 650,
        AID.Katon or AID.TCJKaton => 350,
        AID.Hyoton or AID.TCJHyoton => 350,
        AID.Huton or AID.TCJHuton => 240,
        AID.Suiton or AID.TCJSuiton => level >= 94 ? 580 : 500,
        AID.Doton or AID.TCJDoton => 80,
        AID.HyoshoRanryu => 1300,
        AID.GokaMekkyaku => 850,
        AID.Assassinate => 200,
        AID.DreamWithinADream => 3 * 180,
        AID.HellfrogMedium => 250,
        AID.Bhavacakra => variant == NinPotencyVariant.Meisui && level >= 88
            ? level >= 94 ? 550 : 500
            : level >= 94 ? 400 : 350,
        AID.DeathfrogMedium => 400,
        AID.ZeshoMeppo => variant == NinPotencyVariant.Meisui ? 850 : 700,
        AID.TenriJindo => 1100,
        AID.PhantomKamaitachi => 700,
        AID.FleetingRaiju or AID.ForkedRaiju => level >= 94 ? 700 : 560,
        _ => 0
    };
}

public enum NinBurstVariant
{
    None,
    RaitonFirst,
    KassatsuFirst
}

// Which selector decides the variant. The two automatic ones read the MechanicHints track for their hint input.
// The rules come first: on the harness they match or beat the planner everywhere (Raiton first with a clear forecast +0.24%,
// DMU +0.35%), and the planner cannot see the losses that make Raiton first fragile.
public enum NinBurstMode
{
    [Option("自動(事前規則): 予告が無傷なら先雷遁、他は先活殺")]
    Rules,

    [Option("自動(計画器): 両案を模擬して rDPS の高い方")]
    Planner,

    [Option("先雷遁固定")]
    RaitonFirst,

    [Option("先活殺固定")]
    KassatsuFirst
}

// Snapshot of everything the variant decision depends on. Times are seconds from now; float.MaxValue means "not scheduled".
public struct NinBurstContext
{
    public int Level;
    public float GCD;             // until the next GCD may start
    public float GcdLength;       // weaponskill recast
    public float AnimLock;        // current animation lock left
    public float WeaveLock;       // one oGCD including ping
    public int MudraCharges;
    public int MudraMax;
    public float MudraNextCharge; // until the next charge returns (0 when capped)
    public bool MudraInProgress;
    public float KassatsuLeft;
    public float KassatsuReadyIn;
    public float TenChiJinReadyIn;
    public float DreamReadyIn;
    public float MeisuiReadyIn;
    public float BunshinReadyIn;
    public float PhantomLeft;
    public int RaijuStacks;
    public int Ninki;
    public int Kazematoi;
    public int ComboStep;         // 0 = next is Spinning Edge, 1 = Gust Slash, 2 = finisher
    public int BunshinStacks;
    public float HigiLeft;
    public float MeisuiLeft;
    public float TenriLeft;
    public float ShadowWalkerLeft;
    // since the press that opened the window running now, float.MaxValue when none runs (an earlier burst's press is not this burst's)
    public float KunaiSincePress;
    public float KunaiReadyIn;
    public float DokumoriSincePress;
    public float DokumoriReadyIn;
    public bool EvenBurst;        // Dokumori belongs to this burst
    public int GCDsBeforeDokumori; // opener: GCDs that still have to run before Dokumori may be pressed
    public int GCDsBeforeKunai;
    public float PotionLeft;
    public float PartyBuffLeft;
    public float PartyBuffIn;
    public float TargetLossIn;
    public float TargetReturnIn;
    public bool HintsEnabled;     // target-loss forecasts are being read (float.MaxValue above then means "none known")
    public bool HyoshoUnlocked;
    public bool RaijuUnlocked;
    public bool TenChiJinUnlocked;
    public bool TenriUnlocked;
    public bool ZeshoUnlocked;
    public bool PhantomUnlocked;
    public bool MeisuiUnlocked;
    public bool DreamUnlocked;
    public bool ShukihoUnlocked;
}

public readonly record struct NinBurstEvaluation(NinBurstVariant Choice, float RaitonFirstValue, float KassatsuFirstValue, bool RaitonFirstFeasible, bool KassatsuFirstFeasible, string Reason);

public readonly record struct NinPlannedStep(float Time, AID Action, float Potency, bool InKunai);

public interface INinBurstSelector
{
    NinBurstEvaluation Select(in NinBurstContext ctx, NinBurstVariant tentative);
}

public sealed class NinFixedSelector(NinBurstVariant variant) : INinBurstSelector
{
    public NinBurstEvaluation Select(in NinBurstContext ctx, NinBurstVariant tentative)
    {
        var rf = NinBurstFeasibility.RaitonFirst(ctx);
        var kf = NinBurstFeasibility.KassatsuFirst(ctx);
        // a fixed choice still falls back when its shape cannot be built at all
        var choice = variant == NinBurstVariant.RaitonFirst ? rf ? variant : kf ? NinBurstVariant.KassatsuFirst : NinBurstVariant.None
            : kf ? variant : rf ? NinBurstVariant.RaitonFirst : NinBurstVariant.None;
        return new(choice, 0, 0, rf, kf, "fixed");
    }
}

// Thresholds of the pre-tuned rule selector, fitted offline with the harness (nin-burst-sweep): per-burst RF/KF differences against
// the features below.
public sealed record class NinBurstTuning
{
    // Raiton first spends a charge before Kunai's Bane: worth it when the charges would otherwise sit capped for this long before the window
    public float CappedSecondsFavouringRaitonFirst = 3f;
    // with Phantom Kamaitachi ready the Raiton first pair is Raiton + Phantom, which keeps a combo GCD out of the pre-Kunai slots
    public bool PhantomFavoursRaitonFirst = true;
    // a known target loss starting this soon after Kunai's Bane favours the variant that front-loads Hyosho Ranryu
    public float LossWithinWindowFavouringKassatsuFirst = 8f;
    // Raiton first pays in windows that run uninterrupted, which without a forecast provider cannot be told apart from the ones a loss
    // cuts short (harness, 2867 scenarios: fixed Raiton first -0.9% blind, +0.24% with 30s forecasts): with a provider publishing and
    // no loss forecast through the window it is chosen outright
    public bool ForecastClearWindowFavoursRaitonFirst = true;
}

// Pre-tuned rules: feasibility, then hint conditions, then the fitted conditions. The default: cheaper than the planner and, on the
// harness, never worse than it.
public sealed class NinBurstRules : INinBurstSelector
{
    public NinBurstTuning Tuning = new();

    public NinBurstEvaluation Select(in NinBurstContext ctx, NinBurstVariant tentative)
    {
        var rf = NinBurstFeasibility.RaitonFirst(ctx);
        var kf = NinBurstFeasibility.KassatsuFirst(ctx);
        if (!rf && !kf)
            return new(NinBurstVariant.None, 0, 0, false, false, "infeasible");
        if (rf != kf)
            return new(rf ? NinBurstVariant.RaitonFirst : NinBurstVariant.KassatsuFirst, 0, 0, rf, kf, "only-feasible");

        var kunai = NinBurstFeasibility.EarliestKunai(ctx);
        if (ctx.TargetLossIn < float.MaxValue && ctx.TargetLossIn - kunai < Tuning.LossWithinWindowFavouringKassatsuFirst && ctx.TargetReturnIn > ctx.TargetLossIn)
            return new(NinBurstVariant.KassatsuFirst, 0, 0, rf, kf, "rules-loss");

        if (Tuning.ForecastClearWindowFavoursRaitonFirst && ctx.HintsEnabled && ctx.TargetLossIn >= kunai + NinBurstTiming.KunaiEffective + ctx.GcdLength)
            return new(NinBurstVariant.RaitonFirst, 0, 0, rf, kf, "rules-clear");
        // seconds the charges would spend capped before the Kunai's Bane window if nothing is spent before it
        var capped = ctx.MudraCharges >= ctx.MudraMax ? kunai : MathF.Max(0, kunai - ctx.MudraNextCharge - (ctx.MudraMax - ctx.MudraCharges - 1) * NinBurstTiming.MudraRecast);
        if (ctx.EvenBurst && capped >= Tuning.CappedSecondsFavouringRaitonFirst)
            return new(NinBurstVariant.RaitonFirst, 0, 0, rf, kf, "rules-capped");
        if (ctx.EvenBurst && Tuning.PhantomFavoursRaitonFirst && ctx.PhantomLeft > kunai)
            return new(NinBurstVariant.RaitonFirst, 0, 0, rf, kf, "rules-phantom");
        return new(NinBurstVariant.KassatsuFirst, 0, 0, rf, kf, "rules-default");
    }
}

public static class NinBurstTiming
{
    // Kunai's Bane and Dokumori show on the target at the press and are re-applied when the server confirms them (+1.29s / +1.07s,
    // measured from replays), so a hit pressed within 16.29s / 21.07s of the press gets the bonus; damage checks them at the press.
    public const float KunaiEffective = 16.29f;
    public const float DokumoriEffective = 21.07f;
    public const float MudraStep = 0.5f;
    public const float NinjutsuRecast = 1.5f;
    public const float TenChiJinFast = 1.0f;
    public const float TenChiJinSlow = 1.5f;
    public const float MudraRecast = 20f;
    public const float KassatsuDuration = 15f;
    // Kunai's Bane is placed so that its window ends with Dokumori's: this many seconds after the Dokumori press
    public const float KunaiAfterDokumori = DokumoriEffective - KunaiEffective;
}

public static class NinBurstFeasibility
{
    // When Kunai's Bane can be pressed at the earliest: its cooldown, the opener GCD minimum and, in an even burst, the end alignment
    // with Dokumori. The planner uses the same estimate for its horizon.
    public static float EarliestKunai(in NinBurstContext ctx)
    {
        var at = MathF.Max(ctx.KunaiReadyIn, ctx.GCD);
        if (ctx.EvenBurst)
        {
            var dokumori = DokumoriRunning(ctx) ? -ctx.DokumoriSincePress : MathF.Max(ctx.DokumoriReadyIn, ctx.GCD + ctx.GcdLength * Math.Max(0, ctx.GCDsBeforeDokumori - 1));
            at = MathF.Max(at, dokumori + NinBurstTiming.KunaiAfterDokumori);
        }
        // the bound's last GCD is the one that starts at ctx.GCD (as NIN.cs OpenerBoundKunai counts it)
        return MathF.Max(at, ctx.GCD + ctx.GcdLength * Math.Max(0, ctx.GCDsBeforeKunai - 1));
    }

    // the window this burst is about is already running: its Kunai's Bane / Dokumori press is behind us
    public static bool KunaiRunning(in NinBurstContext ctx) => ctx.KunaiSincePress <= NinBurstTiming.KunaiEffective;
    public static bool DokumoriRunning(in NinBurstContext ctx) => ctx.DokumoriSincePress <= NinBurstTiming.DokumoriEffective;

    // Raiton first needs a mudra charge for the Raiton that goes before Kunai's Bane (by the time that GCD comes), and Kassatsu back
    // early enough to fit Hyosho Ranryu inside the Kunai's Bane window - but not already up: Kassatsu would turn that Raiton into its
    // own ninjutsu, so an active one means Kassatsu first.
    public static bool RaitonFirst(in NinBurstContext ctx)
    {
        if (!ctx.HyoshoUnlocked || KunaiRunning(ctx) || ctx.MudraInProgress || ctx.KassatsuLeft > 0)
            return false;
        var chargeBy = ctx.GCD + ctx.GcdLength;
        var hasCharge = ctx.MudraCharges > 0 || ctx.MudraNextCharge <= chargeBy;
        var kunaiBy = MathF.Max(EarliestKunai(ctx), ctx.GCD + ctx.GcdLength * 2);
        // the pair only works while Shadow Walker lasts until Kunai's Bane goes after it
        var shadowWalkerLasts = !ctx.EvenBurst || ctx.ShadowWalkerLeft > kunaiBy + 2 * NinBurstTiming.MudraStep;
        return hasCharge && shadowWalkerLasts
            && ctx.KassatsuReadyIn <= kunaiBy + NinBurstTiming.KunaiEffective - NinBurstTiming.NinjutsuRecast - 2 * NinBurstTiming.MudraStep;
    }

    // Kassatsu first needs Kassatsu pressed before Kunai's Bane with Hyosho Ranryu still inside its 15s after Kunai's Bane.
    public static bool KassatsuFirst(in NinBurstContext ctx)
    {
        if (!ctx.HyoshoUnlocked || KunaiRunning(ctx) || ctx.MudraInProgress)
            return false;
        if (ctx.KassatsuLeft > 0)
            return true;
        return ctx.KassatsuReadyIn <= EarliestKunai(ctx);
    }
}

// Simulates the coming burst GCD by GCD under each variant with the rules NIN.cs executes, scores every press with the multipliers
// that apply at the press (Kunai's Bane 16.29s / Dokumori 21.07s from their press, Kassatsu on its ninjutsu), and values what is
// left at a horizon both variants share. Party raid buffs are not NIN's rDPS, so they only break near ties.
public sealed class NinBurstPlanner : INinBurstSelector
{
    public float SwitchMargin = 20f;
    // what a resource still held at the horizon is worth over the filler it will displace later
    private const float ChargeValue = 600;
    private const float RaijuValue = 280;
    private const float PhantomValue = 280;
    private const float KassatsuValue = 1270;
    private const float NinkiValue = 8;
    private const float KazematoiValue = 100;
    private const float PotionMultiplier = 1.06f;
    private const float ShadowWalkerDuration = 20;

    public NinBurstEvaluation Select(in NinBurstContext ctx, NinBurstVariant tentative)
    {
        var rf = NinBurstFeasibility.RaitonFirst(ctx);
        var kf = NinBurstFeasibility.KassatsuFirst(ctx);
        if (!rf && !kf)
            return new(NinBurstVariant.None, 0, 0, false, false, "infeasible");
        var rfResult = rf ? Run(ctx, NinBurstVariant.RaitonFirst, null) : default;
        var kfResult = kf ? Run(ctx, NinBurstVariant.KassatsuFirst, null) : default;
        if (rf != kf)
            return new(rf ? NinBurstVariant.RaitonFirst : NinBurstVariant.KassatsuFirst, rfResult.Value, kfResult.Value, rf, kf, "only-feasible");
        var diff = rfResult.Value - kfResult.Value;
        NinBurstVariant choice;
        // Near ties keep the tentative choice; without one, Kassatsu first: Kassatsu pressed before the window comes back before the
        // next one too, which keeps both variants open a minute later (after Kunai's Bane it drifts past it). Party buffs decide only
        // when that does not apply.
        if (MathF.Abs(diff) < SwitchMargin)
            choice = tentative != NinBurstVariant.None ? tentative
                : rfResult.Party - kfResult.Party >= SwitchMargin ? NinBurstVariant.RaitonFirst : NinBurstVariant.KassatsuFirst;
        else
            choice = diff > 0 ? NinBurstVariant.RaitonFirst : NinBurstVariant.KassatsuFirst;
        return new(choice, rfResult.Value, kfResult.Value, true, true, MathF.Abs(diff) < SwitchMargin ? "planner-tie" : "planner");
    }

    public float Simulate(in NinBurstContext ctx, NinBurstVariant variant, List<NinPlannedStep>? trace = null) => Run(ctx, variant, trace).Value;

    private struct State
    {
        public float T;           // start of the next GCD
        public float WeaveFrom;   // earliest start of the next oGCD before that GCD
        public int Charges;
        public float NextCharge;  // absolute time the next charge returns, MaxValue when capped
        public float KassatsuUntil;
        public float KassatsuReadyAt;
        public float TcjReadyAt;
        public int TcjStep;       // -1 idle, 0..2 next Ten Chi Jin ninjutsu
        public float DreamReadyAt;
        public float MeisuiReadyAt;
        public float BunshinReadyAt;
        public bool Phantom;
        public int Raiju;
        public int Ninki;
        public int Kazematoi;
        public int Combo;
        public int Bunshin;
        public bool Higi;
        public bool MeisuiBuff;
        public bool Tenri;
        public float ShadowWalkerUntil; // Hidden counts as never running out
        public float KunaiAt;
        public float DokumoriAt;
        public bool KassatsuUsedInBurst;
        public bool HyoshoDone;
        public int PreKunaiStep;  // Raiton first: GCDs of the pre-Kunai pair already pressed
        public int GCDs;
        public float Value;
        public float Party;
    }

    private (float Value, float Party) Run(in NinBurstContext ctx, NinBurstVariant variant, List<NinPlannedStep>? trace)
    {
        var s = new State
        {
            T = ctx.GCD,
            WeaveFrom = ctx.AnimLock,
            Charges = ctx.MudraCharges,
            NextCharge = ctx.MudraCharges < ctx.MudraMax ? ctx.MudraNextCharge : float.MaxValue,
            KassatsuUntil = ctx.KassatsuLeft > 0 ? ctx.KassatsuLeft : -1,
            KassatsuReadyAt = ctx.KassatsuLeft > 0 ? float.MaxValue : ctx.KassatsuReadyIn,
            TcjReadyAt = ctx.TenChiJinUnlocked ? ctx.TenChiJinReadyIn : float.MaxValue,
            TcjStep = -1,
            DreamReadyAt = ctx.DreamUnlocked ? ctx.DreamReadyIn : float.MaxValue,
            MeisuiReadyAt = ctx.MeisuiUnlocked ? ctx.MeisuiReadyIn : float.MaxValue,
            BunshinReadyAt = ctx.BunshinReadyIn,
            Phantom = ctx.PhantomLeft > 0,
            Raiju = ctx.RaijuStacks,
            Ninki = ctx.Ninki,
            Kazematoi = ctx.Kazematoi,
            Combo = ctx.ComboStep,
            Bunshin = ctx.BunshinStacks,
            Higi = ctx.HigiLeft > 0,
            MeisuiBuff = ctx.MeisuiLeft > 0,
            Tenri = ctx.TenriLeft > 0,
            ShadowWalkerUntil = ctx.ShadowWalkerLeft,
            KunaiAt = NinBurstFeasibility.KunaiRunning(ctx) ? -ctx.KunaiSincePress : float.NaN,
            DokumoriAt = NinBurstFeasibility.DokumoriRunning(ctx) ? -ctx.DokumoriSincePress : float.NaN,
            KassatsuUsedInBurst = ctx.KassatsuLeft > 0
        };

        // one horizon for both variants: the latest the Kunai's Bane window can reach, plus a GCD
        var horizon = (float.IsNaN(s.KunaiAt) ? NinBurstFeasibility.EarliestKunai(ctx) + ctx.GcdLength * 2 : s.KunaiAt) + NinBurstTiming.KunaiEffective + ctx.GcdLength;

        for (var guard = 0; guard < 48 && s.T < horizon; ++guard)
        {
            PlaceAbilities(ref s, ctx, variant, horizon, trace);
            Regenerate(ref s, ctx, s.T);

            // a target loss pauses the GCD until the return
            if (s.T >= ctx.TargetLossIn && s.T < ctx.TargetReturnIn)
            {
                s.T = ctx.TargetReturnIn;
                s.WeaveFrom = s.T;
                if (s.KassatsuUntil >= 0 && s.KassatsuUntil < s.T)
                    s.KassatsuUntil = -1;
                continue;
            }

            var (action, press, duration, preKunaiStep) = ChooseGcd(s, ctx, variant);
            if (press < horizon)
                Execute(ref s, ctx, action, press, trace);
            if (preKunaiStep)
                ++s.PreKunaiStep;
            s.WeaveFrom = press + ctx.WeaveLock;
            s.T += duration;
            ++s.GCDs;
        }

        Regenerate(ref s, ctx, horizon);
        var terminal = s.Charges * ChargeValue + (s.NextCharge < float.MaxValue ? MathF.Max(0, 1 - (s.NextCharge - horizon) / NinBurstTiming.MudraRecast) * ChargeValue : 0)
            + s.Raiju * RaijuValue + (s.Phantom ? PhantomValue : 0) + (s.KassatsuUntil > horizon ? KassatsuValue : 0)
            + (s.Tenri ? NinPotency.Of(AID.TenriJindo, ctx.Level) : 0) + (s.Higi ? 300 : 0) + (s.MeisuiBuff ? 150 : 0)
            + s.Ninki * NinkiValue + s.Kazematoi * KazematoiValue;
        return (s.Value + terminal, s.Party);
    }

    private static void Regenerate(ref State s, in NinBurstContext ctx, float until)
    {
        while (s.NextCharge <= until)
        {
            ++s.Charges;
            s.NextCharge = s.Charges >= ctx.MudraMax ? float.MaxValue : s.NextCharge + NinBurstTiming.MudraRecast;
        }
    }

    private static void SpendCharge(ref State s, in NinBurstContext ctx, float at)
    {
        Regenerate(ref s, ctx, at);
        if (s.Charges >= ctx.MudraMax)
            s.NextCharge = at + NinBurstTiming.MudraRecast;
        s.Charges = Math.Max(0, s.Charges - 1);
    }

    private static bool InKunai(in State s, float at) => !float.IsNaN(s.KunaiAt) && at >= s.KunaiAt && at <= s.KunaiAt + NinBurstTiming.KunaiEffective;
    private static bool InDokumori(in State s, float at) => !float.IsNaN(s.DokumoriAt) && at >= s.DokumoriAt && at <= s.DokumoriAt + NinBurstTiming.DokumoriEffective;

    // oGCDs go into the weave window before the next GCD, in the priority NIN.cs pushes them.
    private static void PlaceAbilities(ref State s, in NinBurstContext ctx, NinBurstVariant variant, float horizon, List<NinPlannedStep>? trace)
    {
        var last = s.T - ctx.WeaveLock;
        for (var guard = 0; guard < 6 && s.WeaveFrom <= last + 1e-4f && s.WeaveFrom < horizon; ++guard)
        {
            var x = s.WeaveFrom;
            if (x >= ctx.TargetLossIn && x < ctx.TargetReturnIn)
                return;
            // what is ready now goes first, in priority order; otherwise a burst cooldown coming back later in this window is pressed
            // the moment it does
            var (pick, at) = PickAbility(s, ctx, variant, x, x, last);
            if (pick == default)
                (pick, at) = PickAbility(s, ctx, variant, x, last, last);
            if (pick == default || at > last + 1e-4f || at >= ctx.TargetLossIn && at < ctx.TargetReturnIn)
                return;
            Execute(ref s, ctx, pick, at, trace);
            s.WeaveFrom = at + ctx.WeaveLock;
        }
    }

    // The oGCD NIN.cs would press in the weave slot starting at x, among those whose cooldown is back by readyBy (x, or the end of the
    // window for the burst cooldowns), with the time it goes out.
    private static (AID Pick, float At) PickAbility(in State s, in NinBurstContext ctx, NinBurstVariant variant, float x, float readyBy, float last)
    {
        if (ctx.EvenBurst && float.IsNaN(s.DokumoriAt) && ctx.DokumoriReadyIn <= readyBy && s.GCDs >= ctx.GCDsBeforeDokumori)
            return (AID.Dokumori, MathF.Max(x, ctx.DokumoriReadyIn));
        if (variant == NinBurstVariant.KassatsuFirst && float.IsNaN(s.KunaiAt) && s.KassatsuUntil < 0 && s.KassatsuReadyAt <= readyBy && !s.KassatsuUsedInBurst
            && KunaiExpectedWithin(s, ctx, MathF.Max(x, s.KassatsuReadyAt), 10))
            return (AID.Kassatsu, MathF.Max(x, s.KassatsuReadyAt));
        if (float.IsNaN(s.KunaiAt) && KunaiAllowed(s, ctx, variant, x, last, out var kunaiAt))
            return (AID.KunaisBane, kunaiAt);
        if (!float.IsNaN(s.KunaiAt) && x >= s.KunaiAt && s.KassatsuUntil < 0 && !s.KassatsuUsedInBurst && s.KassatsuReadyAt <= readyBy)
            return (AID.Kassatsu, MathF.Max(x, s.KassatsuReadyAt));
        if (!float.IsNaN(s.KunaiAt) && x >= s.KunaiAt && s.DreamReadyAt <= readyBy)
            return (AID.DreamWithinADream, MathF.Max(x, s.DreamReadyAt));
        if (readyBy > x)
            return default;
        if (s.BunshinReadyAt <= x && s.Ninki >= 50)
            return (AID.Bunshin, x);
        if (TcjAllowed(s, ctx, x))
            return (AID.TenChiJin, x);
        if (s.ShadowWalkerUntil > x && s.TcjStep < 0 && s.MeisuiReadyAt <= x && s.Ninki <= 50 && !float.IsNaN(s.KunaiAt) && s.KunaiAt < x)
            return (AID.Meisui, x);
        if (s.Tenri && InKunai(s, x))
            return (AID.TenriJindo, x);
        if (s.Ninki >= 50 && (InKunai(s, x) || s.Ninki >= 90))
            return (ctx.ZeshoUnlocked && s.Higi ? AID.ZeshoMeppo : AID.Bhavacakra, x);
        return default;
    }

    private static bool KunaiExpectedWithin(in State s, in NinBurstContext ctx, float x, float seconds)
    {
        var at = MathF.Max(ctx.KunaiReadyIn, x);
        if (ctx.EvenBurst)
            at = MathF.Max(at, (float.IsNaN(s.DokumoriAt) ? MathF.Max(ctx.DokumoriReadyIn, x) : s.DokumoriAt) + NinBurstTiming.KunaiAfterDokumori);
        return at - x <= seconds;
    }

    // Kunai's Bane: needs Shadow Walker, the opener GCD minimum, Raiton first's two pre-Kunai GCDs, and in an even burst Dokumori's
    // end alignment (it is woven as late as the window allows once that time has come).
    private static bool KunaiAllowed(in State s, in NinBurstContext ctx, NinBurstVariant variant, float x, float last, out float at)
    {
        at = x;
        if (ctx.KunaiReadyIn > last || s.ShadowWalkerUntil <= x || s.GCDs < ctx.GCDsBeforeKunai || s.TcjStep >= 0)
            return false;
        // Shadow Walker about to run out: neither the pair nor the alignment is waited for (NIN.cs presses Kunai's Bane at once)
        var runningOut = s.ShadowWalkerUntil <= s.T;
        if (variant == NinBurstVariant.RaitonFirst && ctx.EvenBurst && s.PreKunaiStep < 2 && !runningOut)
            return false;
        var target = MathF.Max(x, ctx.KunaiReadyIn);
        if (ctx.EvenBurst)
        {
            if (float.IsNaN(s.DokumoriAt))
                return false;
            if (!runningOut)
                target = MathF.Max(target, s.DokumoriAt + NinBurstTiming.KunaiAfterDokumori);
        }
        if (target > last + 1e-4f || target >= s.ShadowWalkerUntil)
            return false;
        at = target;
        return true;
    }

    private static bool TcjAllowed(in State s, in NinBurstContext ctx, float x)
    {
        if (!ctx.EvenBurst || s.TcjStep >= 0 || s.TcjReadyAt > x || s.KassatsuUntil >= x || !InKunai(s, x))
            return false;
        // Hyosho Ranryu first: a Kassatsu still to come in this window keeps Ten Chi Jin waiting
        if (!s.HyoshoDone && s.KassatsuReadyAt <= s.KunaiAt + NinBurstTiming.KunaiEffective - 4)
            return false;
        // two charges would stall during the three Ten Chi Jin GCDs: spend one on Raiton first
        if (s.Charges >= ctx.MudraMax)
            return false;
        // its first ninjutsu inside the window is enough (NIN.cs: holding it for the next window costs more)
        return s.KunaiAt + NinBurstTiming.KunaiEffective - s.T >= 0.1f;
    }

    private static (AID Action, float Press, float Duration, bool PreKunaiStep) ChooseGcd(in State s, in NinBurstContext ctx, NinBurstVariant variant)
    {
        var t = s.T;
        if (s.TcjStep >= 0)
            return s.TcjStep switch
            {
                0 => (AID.FumaTen, t, NinBurstTiming.TenChiJinFast, false),
                1 => (AID.TCJRaiton, t, NinBurstTiming.TenChiJinFast, false),
                _ => (AID.TCJSuiton, t, NinBurstTiming.TenChiJinSlow, false)
            };

        var ninjutsuAt = t + 2 * NinBurstTiming.MudraStep;
        var ninjutsuTime = 2 * NinBurstTiming.MudraStep + NinBurstTiming.NinjutsuRecast;
        var kunaiOpen = !float.IsNaN(s.KunaiAt) && t >= s.KunaiAt - 1e-4f;
        var inWindow = kunaiOpen && t <= s.KunaiAt + NinBurstTiming.KunaiEffective;
        var chargeReady = s.Charges > 0 || s.NextCharge <= t;
        var capped = s.Charges >= ctx.MudraMax;

        // Kassatsu's ninjutsu waits for the window unless it would expire first
        if (s.KassatsuUntil >= t && (inWindow || s.KassatsuUntil < t + ctx.GcdLength + ninjutsuTime || float.IsNaN(s.KunaiAt) && ctx.KunaiReadyIn > s.KassatsuUntil))
            return (AID.HyoshoRanryu, ninjutsuAt, ninjutsuTime, false);

        if (!kunaiOpen)
        {
            if (variant == NinBurstVariant.RaitonFirst && ctx.EvenBurst && !float.IsNaN(s.DokumoriAt) && s.PreKunaiStep < 2)
            {
                // the pre-Kunai pair: Raiton then Phantom Kamaitachi, or a combo step then Raiton without Phantom
                var raitonNow = s.Phantom ? s.PreKunaiStep == 0 : s.PreKunaiStep == 1;
                if (raitonNow && chargeReady)
                    return (AID.Raiton, ninjutsuAt, ninjutsuTime, true);
                if (!raitonNow && s.Phantom)
                    return (AID.PhantomKamaitachi, t, ctx.GcdLength, true);
                if (!raitonNow)
                    return (AID.SpinningEdge, t, ctx.GcdLength, true);
            }
            if (ctx.EvenBurst && s.Phantom && !float.IsNaN(s.DokumoriAt))
                return (AID.PhantomKamaitachi, t, ctx.GcdLength, false);
            if (s.Raiju > 0 && !(variant == NinBurstVariant.RaitonFirst && s.PreKunaiStep >= 1))
                return (AID.FleetingRaiju, t, ctx.GcdLength, false);
            if (capped && s.KassatsuUntil < t && ctx.KunaiReadyIn - t > NinBurstTiming.MudraRecast)
                return (AID.Raiton, ninjutsuAt, ninjutsuTime, false);
            return (AID.SpinningEdge, t, ctx.GcdLength, false);
        }

        if (inWindow)
        {
            if (capped)
                return (AID.Raiton, ninjutsuAt, ninjutsuTime, false);
            if (s.Raiju > 0)
                return (AID.FleetingRaiju, t, ctx.GcdLength, false);
            if (chargeReady)
                return (AID.Raiton, ninjutsuAt, ninjutsuTime, false);
            if (s.Phantom && t + ctx.GcdLength <= s.KunaiAt + NinBurstTiming.KunaiEffective)
                return (AID.PhantomKamaitachi, t, ctx.GcdLength, false);
            return (AID.SpinningEdge, t, ctx.GcdLength, false);
        }

        if (s.Raiju > 0)
            return (AID.FleetingRaiju, t, ctx.GcdLength, false);
        // a mudra would spend an active Kassatsu on a plain Raiton, which NIN.cs never does
        if (capped && s.KassatsuUntil < t)
            return (AID.Raiton, ninjutsuAt, ninjutsuTime, false);
        return (AID.SpinningEdge, t, ctx.GcdLength, false);
    }

    private static void Execute(ref State s, in NinBurstContext ctx, AID action, float at, List<NinPlannedStep>? trace)
    {
        var level = ctx.Level;
        var potency = 0f;
        var weaponskill = false;
        switch (action)
        {
            case AID.SpinningEdge:
                // stands for whichever combo step comes next
                weaponskill = true;
                s.Raiju = 0; // a melee weaponskill ends Raiju Ready
                if (s.Combo == 0)
                {
                    potency = NinPotency.Of(AID.SpinningEdge, level);
                    GainShukiho(ref s, ctx, 5);
                }
                else if (s.Combo == 1)
                {
                    potency = NinPotency.Of(AID.GustSlash, level, NinPotencyVariant.Combo);
                    GainShukiho(ref s, ctx, 5);
                }
                else if (s.Kazematoi > 0)
                {
                    potency = NinPotency.Of(AID.AeolianEdge, level, NinPotencyVariant.ComboPositional) + NinPotency.KazematoiBonus;
                    --s.Kazematoi;
                    GainShukiho(ref s, ctx, level >= 84 ? 15 : level >= 78 ? 10 : 5);
                }
                else
                {
                    potency = NinPotency.Of(AID.ArmorCrush, level, NinPotencyVariant.ComboPositional);
                    s.Kazematoi = Math.Min(5, s.Kazematoi + 2);
                    GainShukiho(ref s, ctx, level >= 84 ? 15 : level >= 78 ? 10 : 5);
                }
                s.Combo = (s.Combo + 1) % 3;
                break;
            case AID.Raiton:
                SpendCharge(ref s, ctx, at - 2 * NinBurstTiming.MudraStep);
                potency = NinPotency.Of(AID.Raiton, level);
                if (ctx.RaijuUnlocked)
                    s.Raiju = Math.Min(3, s.Raiju + 1);
                break;
            case AID.HyoshoRanryu:
                potency = NinPotency.Of(AID.HyoshoRanryu, level) * NinPotency.KassatsuBonus;
                s.KassatsuUntil = -1;
                s.HyoshoDone = true;
                break;
            case AID.FleetingRaiju:
                weaponskill = true;
                potency = NinPotency.Of(AID.FleetingRaiju, level);
                s.Raiju = Math.Max(0, s.Raiju - 1);
                s.Ninki = Math.Min(100, s.Ninki + 5);
                break;
            case AID.PhantomKamaitachi:
                potency = NinPotency.Of(AID.PhantomKamaitachi, level);
                s.Phantom = false;
                s.Ninki = Math.Min(100, s.Ninki + 10);
                break;
            case AID.FumaTen:
                potency = NinPotency.Of(AID.FumaTen, level);
                s.TcjStep = 1;
                break;
            case AID.TCJRaiton:
                potency = NinPotency.Of(AID.TCJRaiton, level);
                if (ctx.RaijuUnlocked)
                    s.Raiju = Math.Min(3, s.Raiju + 1);
                s.TcjStep = 2;
                break;
            case AID.TCJSuiton:
                potency = NinPotency.Of(AID.TCJSuiton, level);
                s.ShadowWalkerUntil = MathF.Max(s.ShadowWalkerUntil, at + ShadowWalkerDuration);
                s.TcjStep = -1;
                break;
            case AID.TenChiJin:
                s.TcjStep = 0;
                s.TcjReadyAt = float.MaxValue;
                s.Tenri = ctx.TenriUnlocked;
                break;
            case AID.Dokumori:
                potency = NinPotency.Of(AID.Dokumori, level);
                s.DokumoriAt = at;
                s.Ninki = Math.Min(100, s.Ninki + 40);
                s.Higi = ctx.ZeshoUnlocked;
                break;
            case AID.KunaisBane:
                potency = NinPotency.Of(AID.KunaisBane, level);
                s.KunaiAt = at;
                s.ShadowWalkerUntil = 0;
                break;
            case AID.Kassatsu:
                s.KassatsuUntil = at + NinBurstTiming.KassatsuDuration;
                s.KassatsuReadyAt = float.MaxValue;
                s.KassatsuUsedInBurst = true;
                break;
            case AID.DreamWithinADream:
                potency = NinPotency.Of(AID.DreamWithinADream, level);
                s.DreamReadyAt = float.MaxValue;
                break;
            case AID.Bunshin:
                s.Ninki -= 50;
                s.Bunshin = 5;
                s.BunshinReadyAt = float.MaxValue;
                s.Phantom = ctx.PhantomUnlocked;
                break;
            case AID.Meisui:
                s.ShadowWalkerUntil = 0;
                s.MeisuiReadyAt = float.MaxValue;
                s.Ninki = Math.Min(100, s.Ninki + 50);
                s.MeisuiBuff = level >= 88;
                break;
            case AID.TenriJindo:
                potency = NinPotency.Of(AID.TenriJindo, level);
                s.Tenri = false;
                break;
            case AID.ZeshoMeppo:
            case AID.Bhavacakra:
                potency = NinPotency.Of(action, level, s.MeisuiBuff ? NinPotencyVariant.Meisui : NinPotencyVariant.Base);
                s.MeisuiBuff = false;
                if (action == AID.ZeshoMeppo)
                    s.Higi = false;
                s.Ninki -= 50;
                break;
        }

        if (weaponskill && s.Bunshin > 0)
        {
            potency += NinPotency.BunshinMelee;
            --s.Bunshin;
            s.Ninki = Math.Min(100, s.Ninki + 5);
        }

        if (potency <= 0 || at >= ctx.TargetLossIn && at < ctx.TargetReturnIn)
            return;
        var multiplier = (InKunai(s, at) && action != AID.KunaisBane ? NinPotency.KunaiBonus : 1f)
            * (InDokumori(s, at) && action != AID.Dokumori ? NinPotency.DokumoriBonus : 1f)
            * (at < ctx.PotionLeft ? PotionMultiplier : 1f);
        var value = potency * multiplier;
        s.Value += value;
        if (at < ctx.PartyBuffLeft || at >= ctx.PartyBuffIn && at < ctx.PartyBuffIn + 20)
            s.Party += value;
        trace?.Add(new(at, action, value, InKunai(s, at)));
    }

    private static void GainShukiho(ref State s, in NinBurstContext ctx, int amount)
    {
        if (ctx.ShukihoUnlocked)
            s.Ninki = Math.Min(100, s.Ninki + amount);
    }
}
