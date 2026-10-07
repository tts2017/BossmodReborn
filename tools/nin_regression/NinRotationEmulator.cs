namespace NinRegression;

public sealed class NinRotationEmulator
{
    private const double Step = 0.1;

    public NinScenarioResult Run(NinScenario scenario)
    {
        var state = NinSimState.Create(scenario);
        var events = scenario.Events.OrderBy(e => e.Time).ToList();
        var nextEvent = 0;

        if (scenario.CountdownSuiton && scenario.Level >= 45 && state.MudraCharges > 0)
        {
            UseNinjutsu(state, NinAction.Suiton, "Countdown Suiton prep");
            state.ShadowWalker = 20;
        }

        while (state.Time <= scenario.Duration)
        {
            while (nextEvent < events.Count && events[nextEvent].Time <= state.Time + 0.0001)
            {
                state.ApplyEvent(events[nextEvent]);
                nextEvent++;
            }

            if (state.Time >= state.NextOgcdAt && state.CanActOnEnemy)
                ExecuteOgcd(state);

            if (state.Time >= state.NextGcdAt)
                ExecuteGcd(state);

            NinHardFailRules.CheckFrame(state);
            NinSoftRegressionRules.CheckFrame(state);
            state.Tick(Step);
        }

        return new NinScenarioResult
        {
            Scenario = scenario,
            Actions = state.Actions,
            HardFailures = state.HardFailures,
            SoftRegressions = state.SoftRegressions,
            CoverageGaps = ["CoverageGap: lightweight emulator mirrors NIN.cs decision logic but does not instantiate BossMod Autorotation module directly."]
        };
    }

    private static void ExecuteGcd(NinSimState s)
    {
        if (!s.CanActOnEnemy && !s.HasRangedTarget)
            return;

        if (s.BasicComboOnly)
        {
            ExecuteBasicComboGcd(s);
            return;
        }

        if (s.TenChiJinLeft > 0)
        {
            ExecuteTenChiJinGcd(s);
            return;
        }

        if (s.ManualRaitonRequested && !s.KassatsuActiveOrQueued && s.MudraCharges > 0)
        {
            UseNinjutsu(s, NinAction.Raiton, "Manual Raiton request");
            s.ManualRaitonRequested = false;
            return;
        }

        if (ShouldPrepareShadowWalkerForMugWindow(s) && s.MudraCharges > 0)
        {
            UseNinjutsu(s, s.UseNinjutsuAoe ? NinAction.Huton : NinAction.Suiton, "ShadowWalker prep for Mug/Kunai");
            return;
        }

        if (!s.KassatsuActiveOrQueued && s.PhantomKamaitachiLeft > s.GcdLength && s.HasRangedTarget && !ShouldHoldPhantomForMugWindow(s))
        {
            UseGcd(s, NinAction.PhantomKamaitachi, NinTargetKind.RangedAoe, "Phantom Kamaitachi filler");
            s.PhantomKamaitachiLeft = 0;
            GainNinki(s, 10);
            return;
        }

        if (!s.KassatsuActiveOrQueued && s.RaijuStacks > 0 && !ShouldHoldRaijuForRaitonFirstPreKunai(s) && !ShouldPrioritizeKassatsuNinjutsuOverRaiju(s))
        {
            UseGcd(s, NinAction.FleetingRaiju, NinTargetKind.Primary, "Raiju filler");
            s.RaijuStacks--;
            GainNinki(s, 5);
            return;
        }

        if (s.KassatsuActiveOrQueued)
        {
            UseNinjutsu(s, KassatsuAttackNinjutsu(s), "Kassatsu attack ninjutsu");
            return;
        }

        if (ShouldUseAttackNinjutsuNow(s))
        {
            UseNinjutsu(s, NormalAttackNinjutsu(s), "Normal attack ninjutsu");
            return;
        }

        ExecuteComboGcd(s);
    }

    private static void ExecuteBasicComboGcd(NinSimState s)
    {
        if (!s.CanActOnEnemy)
            return;

        if (s.UseMeleeAoe && s.Scenario.Level >= 38)
        {
            var action = s.ComboLastMove == NinAction.DeathBlossom && s.Scenario.Level >= 52 ? NinAction.HakkeMujinsatsu : NinAction.DeathBlossom;
            UseGcd(s, action, NinTargetKind.Primary, "BasicComboOnly AOE combo");
            s.ComboLastMove = action;
            return;
        }

        ExecuteComboGcd(s, "BasicComboOnly single-target combo");
    }

    private static void ExecuteComboGcd(NinSimState s, string reason = "Normal combo")
    {
        if (!s.CanActOnEnemy)
            return;

        NinAction action;
        if (s.ComboLastMove == NinAction.SpinningEdge && s.Scenario.Level >= 4)
            action = NinAction.GustSlash;
        else if (s.ComboLastMove == NinAction.GustSlash && s.Scenario.Level >= 26)
            action = s.Kazematoi < 3 && s.Scenario.Level >= 54 ? NinAction.ArmorCrush : NinAction.AeolianEdge;
        else
            action = NinAction.SpinningEdge;

        UseGcd(s, action, NinTargetKind.Primary, reason);
        s.ComboLastMove = action;
        GainNinki(s, s.Scenario.Level >= 62 ? 5 : 0);
        if (action == NinAction.ArmorCrush)
            s.Kazematoi = Math.Min(5, s.Kazematoi + 2);
    }

    private static void ExecuteTenChiJinGcd(NinSimState s)
    {
        if (!s.HasRangedTarget)
            return;

        NinAction action;
        if (s.UseNinjutsuAoe)
        {
            action = s.TenChiJinParam switch
            {
                0 => NinAction.TCJFuma,
                1 => NinAction.TCJHyoton,
                2 or 3 => NinAction.TCJKaton,
                _ => NinAction.TCJKaton
            };
        }
        else
        {
            action = s.TenChiJinParam switch
            {
                0 => NinAction.TCJFuma,
                1 => NinAction.TCJRaiton,
                _ => NinAction.TCJSuiton
            };
        }

        UseGcd(s, action, s.UseNinjutsuAoe ? NinTargetKind.RangedAoe : NinTargetKind.Primary, "Ten Chi Jin progression");
        s.TenChiJinParam++;
        if (action == NinAction.TCJSuiton)
            s.ShadowWalker = 20;
        if (s.TenChiJinParam >= 3)
        {
            s.TenChiJinLeft = 0;
            s.TenriJindoLeft = s.Scenario.Level >= 100 ? 30 : 0;
        }
    }

    private static void ExecuteOgcd(NinSimState s)
    {
        if (s.BasicComboOnly)
            return;

        if (ShouldSpendNinkiBeforeDokumori(s) || ShouldSpendNinkiBeforeMeisui(s))
        {
            UseNinkiSpender(s, "Ninki overcap prevention before Dokumori/Meisui");
            return;
        }

        if (s.MugReadyIn <= 0)
        {
            var action = s.Scenario.Level >= 66 ? NinAction.Dokumori : NinAction.Mug;
            UseOgcd(s, action, NinTargetKind.Primary, "Mug/Dokumori on cooldown");
            s.MugReadyIn = 120;
            s.TargetMugLeft = 20;
            GainNinki(s, s.Scenario.Level >= 66 ? 40 : 0);
            return;
        }

        if (ShouldUseTrickActionNow(s))
        {
            var action = s.Scenario.Level >= 92 ? NinAction.KunaisBane : NinAction.TrickAttack;
            UseOgcd(s, action, NinTargetKind.Primary, "Trick/Kunai synchronized to Mug");
            s.TrickReadyIn = 60;
            s.TargetTrickLeft = 15;
            s.ShadowWalker = 0;
            return;
        }

        if (s.Scenario.PotionStrategy == PotionStrategy.EvenBurst && s.PotionReadyIn <= 0 && s.TargetMugLeft > s.GcdLength)
        {
            UseOgcd(s, NinAction.Potion, NinTargetKind.Player, "Potion in even burst");
            s.PotionReadyIn = 270;
            s.PotionLeft = 30;
            return;
        }

        if (ShouldSpendHighNinkiDuringBurst(s))
        {
            UseNinkiSpender(s, "High ninki burst spender before lower-priority damage ogcds");
            return;
        }

        if (s.DreamReadyIn <= 0 && (s.TargetMugLeft > s.GcdLength || s.TargetTrickLeft > s.GcdLength || s.MugReadyIn > 10 && s.TrickReadyIn > 10))
        {
            UseOgcd(s, s.Scenario.Level >= 56 ? NinAction.DreamWithinADream : NinAction.Assassinate, NinTargetKind.Primary, "DWaD/Assassinate damage weave");
            s.DreamReadyIn = 60;
            return;
        }

        if (ShouldUseKassatsuNow(s))
        {
            UseOgcd(s, NinAction.Kassatsu, NinTargetKind.Player, "Kassatsu for burst ninjutsu");
            s.KassatsuReadyIn = 60;
            s.KassatsuLeft = 15;
            s.KassatsuQueuedThisFrame = true;
            return;
        }

        if (ShouldUseTenChiJinNow(s))
        {
            UseOgcd(s, NinAction.TenChiJin, NinTargetKind.Player, "Ten Chi Jin in Dokumori window");
            s.TenChiJinReadyIn = 120;
            s.TenChiJinLeft = 6;
            s.TenChiJinParam = 0;
            return;
        }

        if (ShouldUseMeisuiNow(s))
        {
            UseOgcd(s, NinAction.Meisui, NinTargetKind.Player, "Meisui after ShadowWalker/TCJ Suiton");
            s.MeisuiReadyIn = 120;
            s.MeisuiLeft = 30;
            s.ShadowWalker = 0;
            GainNinki(s, 50);
            return;
        }

        if (s.TenriJindoLeft > 0 && s.TenriJindoReadyIn <= 0)
        {
            UseOgcd(s, NinAction.TenriJindo, s.UseNinjutsuAoe ? NinTargetKind.RangedAoe : NinTargetKind.Primary, "Tenri Jindo Ready");
            s.TenriJindoReadyIn = 1;
            s.TenriJindoLeft = 0;
            return;
        }

        if (s.BunshinReadyIn <= 0 && s.Ninki >= 50 && (s.TargetMugLeft > s.GcdLength || s.TargetTrickLeft > s.GcdLength || s.MugReadyIn > 15))
        {
            UseOgcd(s, NinAction.Bunshin, NinTargetKind.Player, "Bunshin ninki spender");
            s.BunshinReadyIn = 90;
            SpendNinki(s, 50);
            s.PhantomKamaitachiLeft = 45;
            return;
        }

        if (ShouldBhava(s))
            UseNinkiSpender(s, "Ninki spender");
    }

    private static bool ShouldUseTrickActionNow(NinSimState s)
    {
        if (!s.HiddenForTrick || s.TargetTrickLeft > 0)
            return false;
        if (s.TrickReadyIn > s.GcdLength + NinSimState.AnimLock)
            return false;
        if (s.PendingNinjutsu != PendingNinjutsu.None || s.MudraLeft > 0)
            return false;
        if (ShouldSkipOddKunaiForMugSync(s))
            return false;
        if (s.UltimateZeroSecond)
        {
            if (s.TargetMugLeft > 0)
                return true;
            if (s.MugReadyIn > 10)
                return KunaiWouldBeReadyForNextMugIfUsedNow(s);
            return false;
        }
        if (s.TargetMugLeft > 0)
            return ShouldUseTrickActionForMugEndAlign(s);
        if (s.MugReadyIn > 10)
            return KunaiWouldBeReadyForNextMugIfUsedNow(s);
        return false;
    }

    private static bool ShouldUseTrickActionForMugEndAlign(NinSimState s)
    {
        if (s.TargetMugLeft <= 0 || s.TargetTrickLeft > 0 || !s.HiddenForTrick)
            return false;
        var atOrPastAlignedStart = s.TargetMugLeft <= NinSimState.KunaiStartMugLeftForEndAlign + NinSimState.KunaiEndAlignTolerance;
        var emergencyLate = s.TargetMugLeft <= s.GcdLength + NinSimState.AnimLock;
        return atOrPastAlignedStart || emergencyLate;
    }

    private static bool ShouldSkipOddKunaiForMugSync(NinSimState s)
    {
        if (s.TargetMugLeft > 0 || s.TargetTrickLeft > 0)
            return false;
        if (s.TrickReadyIn > s.GcdLength + NinSimState.AnimLock)
            return false;
        if (s.MugReadyIn <= s.GcdLength + NinSimState.AnimLock)
            return false;
        return !KunaiWouldBeReadyForNextMugIfUsedNow(s);
    }

    private static bool KunaiWouldBeReadyForNextMugIfUsedNow(NinSimState s)
        => KunaiWouldBeReadyForNextMugIfUsedIn(s, 0);

    private static bool KunaiWouldBeReadyForNextMugIfUsedIn(NinSimState s, double useIn)
    {
        if (s.MugReadyIn <= useIn + s.GcdLength + NinSimState.AnimLock)
            return true;
        return s.MugReadyIn - useIn >= NinSimState.KunaiOddSkipSafetyWindow;
    }

    private static double ExpectedTrickActionIn(NinSimState s)
    {
        if (s.TargetTrickLeft > 0)
            return 0;
        if (s.TargetMugLeft > 0)
        {
            if (s.TrickReadyIn > s.TargetMugLeft + 0.1)
                return double.MaxValue;
            if (s.UltimateZeroSecond)
                return s.TrickReadyIn;
            return Math.Max(s.TrickReadyIn, Math.Max(0, s.TargetMugLeft - NinSimState.KunaiStartMugLeftForEndAlign));
        }
        if (!KunaiWouldBeReadyForNextMugIfUsedIn(s, s.TrickReadyIn))
            return s.UltimateZeroSecond ? s.MugReadyIn : s.MugReadyIn + (NinSimState.MugDokumoriDuration - NinSimState.TrickKunaiDuration);
        if (s.UltimateZeroSecond)
            return s.MugReadyIn <= 20 ? s.MugReadyIn : s.TrickReadyIn;
        if (s.MugReadyIn <= 20)
            return s.MugReadyIn + (NinSimState.MugDokumoriDuration - NinSimState.TrickKunaiDuration);
        return s.TrickReadyIn;
    }

    private static bool ShouldPrepareShadowWalkerForMugWindow(NinSimState s)
    {
        if (s.Scenario.Level < 45 || s.ShadowWalker > s.GcdLength || s.PendingNinjutsu != PendingNinjutsu.None || s.KassatsuActiveOrQueued || s.TargetTrickLeft > 0)
            return false;
        var expected = ExpectedTrickActionIn(s);
        return expected != double.MaxValue && expected <= Math.Min(12, Math.Max(10, 20 - s.GcdLength * 4));
    }

    private static bool ShouldUseAttackNinjutsuNow(NinSimState s)
    {
        if (s.MudraCharges <= 0)
            return false;
        if (ShouldPrepareShadowWalkerForMugWindow(s))
            return false;
        if (s.TargetMugLeft > s.GcdLength || s.TargetTrickLeft > s.GcdLength)
            return true;
        return ShouldSpendNormalNinjutsuToAvoidMudraOvercap(s);
    }

    private static bool ShouldSpendNormalNinjutsuToAvoidMudraOvercap(NinSimState s)
    {
        if (s.Scenario.Level < 30 || s.MudraCharges <= 0)
            return false;
        if (s.MudraCharges < 2 && s.TenReadyIn > s.GcdLength + 0.1)
            return false;
        if (ShouldHoldMudraForBurstByDps(s))
            return false;
        return s.MudraCharges >= 2 || s.TenReadyIn <= s.GcdLength + 0.1;
    }

    private static bool ShouldHoldMudraForBurstByDps(NinSimState s)
    {
        var burstIn = Math.Min(Math.Min(s.MugReadyIn, s.TrickReadyIn), ExpectedTrickActionIn(s));
        if (burstIn > 10)
            return false;
        if (s.MudraCapStartedAt < 0)
            return true;
        return s.Time - s.MudraCapStartedAt < NinSimState.MaxAllowedMudraCapHold;
    }

    private static bool ShouldHoldPhantomForMugWindow(NinSimState s)
        => s.TargetMugLeft <= s.GcdLength && s.TargetTrickLeft <= s.GcdLength && ShouldPrepareShadowWalkerForMugWindow(s);

    private static bool ShouldHoldRaijuForRaitonFirstPreKunai(NinSimState s)
        => s.TargetMugLeft > 0 && s.TargetTrickLeft == 0 && s.TargetMugLeft > NinSimState.KunaiStartMugLeftForEndAlign;

    private static bool ShouldPrioritizeKassatsuNinjutsuOverRaiju(NinSimState s)
        => s.KassatsuActiveOrQueued && KassatsuAttackNinjutsuUnlocked(s) && (s.TargetTrickLeft > s.GcdLength || s.TrickReadyIn <= s.GcdLength);

    private static bool ShouldUseKassatsuNow(NinSimState s)
    {
        if (s.Scenario.Level < 50 || s.KassatsuActiveOrQueued || s.KassatsuReadyIn > NinSimState.AnimLock || s.PendingNinjutsu != PendingNinjutsu.None)
            return false;
        if (ShouldPrepareShadowWalkerForMugWindow(s) || ShouldSkipOddKunaiForMugSync(s))
            return false;
        if (s.TargetMugLeft > s.GcdLength || s.TargetTrickLeft > s.GcdLength)
            return true;
        if (s.MugReadyIn <= 15 || s.TrickReadyIn <= 15)
            return false;
        return s.ShadowWalker > s.GcdLength;
    }

    private static bool ShouldUseTenChiJinNow(NinSimState s)
    {
        if (s.Scenario.Level < 70 || !s.InEvenBurstForTcjMeisui || s.TenChiJinReadyIn > NinSimState.AnimLock)
            return false;
        if (s.MudraCharges > 0 || s.PendingNinjutsu != PendingNinjutsu.None || s.KassatsuActiveOrQueued || s.TenChiJinLeft > 0)
            return false;
        if (s.KassatsuReadyIn <= s.GcdLength && (s.TargetMugLeft > s.GcdLength || s.TargetTrickLeft > s.GcdLength))
            return false;
        if (ShouldPrepareShadowWalkerForMugWindow(s))
            return false;
        if (s.RaijuStacks > 0 && s.TargetTrickLeft > s.GcdLength)
            return false;
        return true;
    }

    private static bool CanUseMeisuiNowIgnoringNinki(NinSimState s)
    {
        if (s.Scenario.Level < 72 || !s.InEvenBurstForTcjMeisui || s.TenChiJinLeft > 0 || s.MeisuiReadyIn > NinSimState.AnimLock)
            return false;
        if (s.Scenario.Level >= 70 && s.TenChiJinReadyIn <= 10)
            return false;
        if (s.ShadowWalker <= 0 || s.TrickReadyIn <= s.ShadowWalker)
            return false;
        if (s.TargetMugLeft > s.GcdLength && ShouldUseTenChiJinNow(s))
            return false;
        return true;
    }

    private static bool ShouldUseMeisuiNow(NinSimState s)
        => CanUseMeisuiNowIgnoringNinki(s) && !ShouldSpendNinkiBeforeMeisui(s);

    private static bool ShouldSpendNinkiBeforeMeisui(NinSimState s)
        => s.Ninki >= 50 && CanUseMeisuiNowIgnoringNinki(s) && s.Ninki + EstimateNinkiGainBeforeNextPostMeisuiSpend(s) > 100;

    private static bool ShouldSpendNinkiBeforeDokumori(NinSimState s)
        => s.Ninki >= 50 && s.TargetMugLeft <= 0 && s.MugReadyIn <= s.GcdLength + NinSimState.AnimLock && s.Ninki + EstimateNinkiGainBeforeNextPostDokumoriSpend(s) > 100;

    private static int EstimateNinkiGainBefore(NinSimState s, double seconds)
    {
        var gain = 0;
        if (seconds <= 0)
            return gain;
        if (s.PhantomKamaitachiLeft > s.GcdLength && seconds >= s.GcdLength)
            gain += 10;
        if (s.RaijuStacks > 0 && seconds >= s.GcdLength)
            gain += 5;
        if (s.Scenario.Level >= 62)
            gain += Math.Max(0, (int)Math.Floor(seconds / Math.Max(s.GcdLength, 0.1))) * 5;
        return gain;
    }

    private static int EstimateNinkiGainBeforeNextPostDokumoriSpend(NinSimState s)
    {
        var gain = 40 + EstimateNinkiGainBefore(s, s.MugReadyIn);
        var firstPostDokumoriSpendIn = s.MugReadyIn + NinSimState.AnimLock;

        if (s.NextGcdAt - s.Time > firstPostDokumoriSpendIn)
            return gain;

        if (s.PhantomKamaitachiLeft > s.GcdLength)
            return gain + 10;

        if (s.RaijuStacks > 0)
            return gain + 5;

        if (s.Scenario.Level >= 62 && s.MudraCharges == 0 && s.MudraLeft <= 0 && s.TenChiJinLeft <= 0 && !s.KassatsuActiveOrQueued && s.PendingNinjutsu == PendingNinjutsu.None)
            return gain + 5;

        return gain;
    }

    private static int EstimateNinkiGainBeforeNextPostMeisuiSpend(NinSimState s)
    {
        var gain = 50 + EstimateNinkiGainBefore(s, s.MeisuiReadyIn);
        var firstPostMeisuiSpendIn = s.MeisuiReadyIn + NinSimState.AnimLock;

        if (s.NextGcdAt - s.Time > firstPostMeisuiSpendIn)
            return gain;

        if (s.PhantomKamaitachiLeft > s.GcdLength)
            return gain + 10;

        if (s.RaijuStacks > 0)
            return gain + 5;

        if (s.Scenario.Level >= 62 && s.MudraCharges == 0 && s.MudraLeft <= 0 && s.TenChiJinLeft <= 0 && !s.KassatsuActiveOrQueued && s.PendingNinjutsu == PendingNinjutsu.None)
            return gain + 5;

        return gain;
    }

    private static bool ShouldSpendHighNinkiDuringBurst(NinSimState s)
        => s.Ninki >= NinSimState.HighNinkiBurstSpendThreshold && (s.TargetMugLeft > NinSimState.AnimLock || s.TargetTrickLeft > NinSimState.AnimLock);

    private static bool ShouldReserveNinkiForBunshinBeforeBurst(NinSimState s)
    {
        if (s.Scenario.Level < 80 || s.Ninki >= 100)
            return false;
        if (s.BunshinReadyIn <= NinSimState.AnimLock)
            return false;
        if (s.TargetMugLeft > NinSimState.AnimLock || s.TargetTrickLeft > NinSimState.AnimLock)
            return false;
        var burstIn = Math.Min(s.MugReadyIn, s.TrickReadyIn);
        if (burstIn > 15)
            return false;
        if (s.BunshinReadyIn > burstIn + s.GcdLength + NinSimState.AnimLock)
            return false;
        var projectedNinkiAtBunshinAfterSpend = Math.Max(0, s.Ninki - 50) + EstimateNinkiGainBefore(s, s.BunshinReadyIn);
        return projectedNinkiAtBunshinAfterSpend < 50;
    }

    private static bool ShouldBhava(NinSimState s)
    {
        if (s.Ninki < 50 || s.NinkiSpendReadyIn > NinSimState.AnimLock)
            return false;
        if (s.MeisuiLeft > 0)
            return true;
        if (s.TargetMugLeft > NinSimState.AnimLock || s.TargetTrickLeft > NinSimState.AnimLock)
            return true;
        if (ShouldSpendNinkiBeforeDokumori(s) || ShouldSpendNinkiBeforeMeisui(s))
            return true;
        if (ShouldReserveNinkiForBunshinBeforeBurst(s))
            return false;
        if (s.Ninki > 85)
            return true;
        if (s.MugReadyIn <= 15 || s.TrickReadyIn <= 15)
            return false;
        return false;
    }

    private static NinAction NormalAttackNinjutsu(NinSimState s)
        => s.UseNinjutsuAoe ? NinAction.Katon : NinAction.Raiton;

    private static NinAction KassatsuAttackNinjutsu(NinSimState s)
    {
        if (s.UseKassatsuNinjutsuAoe && s.Scenario.Level >= 76)
            return NinAction.GokaMekkyaku;
        if (s.Scenario.Level >= 76)
            return NinAction.HyoshoRanryu;
        return NormalAttackNinjutsu(s);
    }

    private static bool KassatsuAttackNinjutsuUnlocked(NinSimState s)
        => s.Scenario.Level >= 76;

    private static void UseNinjutsu(NinSimState s, NinAction action, string reason)
    {
        if (!s.KassatsuActiveOrQueued)
        {
            if (s.MudraCharges <= 0)
            {
                s.AddHardFail(HardFailRule.MudraStackFailure, "Mudra charge is required for normal ninjutsu.", action, "UseMudraDetailed");
                return;
            }
            s.MudraCharges--;
            if (s.MudraCharges < 2 && s.MudraRecharge <= 0)
                s.MudraRecharge = 20;
        }

        s.PendingNinjutsu = PendingNinjutsu.None;
        if (action is NinAction.Suiton or NinAction.Huton)
            s.ShadowWalker = 20;
        if (action is NinAction.Raiton && s.Scenario.Level >= 90)
            s.RaijuStacks = Math.Min(3, s.RaijuStacks + 1);
        if (action is NinAction.HyoshoRanryu or NinAction.GokaMekkyaku)
            s.KassatsuLeft = 0;
        UseGcd(s, action, action is NinAction.Katon or NinAction.GokaMekkyaku or NinAction.Huton ? NinTargetKind.RangedAoe : NinTargetKind.Primary, reason);
    }

    private static void UseNinkiSpender(NinSimState s, string reason)
    {
        var action = s.NumRangedAoeTargets > 2 || s.Scenario.Level < 68
            ? s.HigiLeft > 0 && s.Scenario.Level >= 96 ? NinAction.DeathfrogMedium : NinAction.HellfrogMedium
            : s.HigiLeft > 0 && s.Scenario.Level >= 96 ? NinAction.ZeshoMeppo : NinAction.Bhavacakra;
        UseOgcd(s, action, action is NinAction.HellfrogMedium or NinAction.DeathfrogMedium ? NinTargetKind.RangedAoe : NinTargetKind.Primary, reason);
        SpendNinki(s, 50);
    }

    private static void UseGcd(NinSimState s, NinAction action, NinTargetKind target, string reason)
    {
        s.Log(action, target, NinActionKind.Gcd, reason);
        NinHardFailRules.CheckAfterAction(s, action, reason);
        NinSoftRegressionRules.CheckAfterAction(s, action);
        s.NextGcdAt = s.Time + s.GcdLength;
        s.LastGcdActionAt = s.Time;
    }

    private static void UseOgcd(NinSimState s, NinAction action, NinTargetKind target, string reason)
    {
        s.Log(action, target, action == NinAction.Potion ? NinActionKind.Item : NinActionKind.Ogcd, reason);
        NinHardFailRules.CheckAfterAction(s, action, reason);
        NinSoftRegressionRules.CheckAfterAction(s, action);
        s.NextOgcdAt = s.Time + NinSimState.AnimLock;
    }

    private static void GainNinki(NinSimState s, int gain)
        => s.Ninki = Math.Min(100, s.Ninki + Math.Max(0, gain));

    private static void SpendNinki(NinSimState s, int amount)
    {
        s.Ninki = Math.Max(0, s.Ninki - amount);
        s.NinkiSpendReadyIn = 1;
    }
}
