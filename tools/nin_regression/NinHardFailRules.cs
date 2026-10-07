namespace NinRegression;

public static class NinHardFailRules
{
    public static void CheckAfterAction(NinSimState s, NinAction action, string reason)
    {
        if (action is NinAction.Doton or NinAction.TCJDoton)
            s.AddHardFail(HardFailRule.DotonForbidden, "Doton/TCJDoton must never be selected by automatic rotation.", action, "TargetForNinjutsu/TCJ route");

        if (s.BasicComboOnly && action is not (NinAction.SpinningEdge or NinAction.GustSlash or NinAction.AeolianEdge or NinAction.ArmorCrush or NinAction.DeathBlossom or NinAction.HakkeMujinsatsu))
            s.AddHardFail(HardFailRule.BasicComboOnlyFailure, "BasicComboOnly must only use normal combo GCDs and no OGCD/ninjutsu/potion/True North.", action, "UseBasicComboOnly branch");

        if (action is NinAction.TenChiJin && s.TargetMugLeft <= s.GcdLength)
            s.AddHardFail(HardFailRule.TenChiJinMeisuiFailure, "Ten Chi Jin must start only in Mug/Dokumori window.", action, "ShouldUseTenChiJinNow");

        if (action is NinAction.Meisui && s.TargetMugLeft <= s.GcdLength)
            s.AddHardFail(HardFailRule.TenChiJinMeisuiFailure, "Meisui must be used only in Mug/Dokumori window.", action, "CanUseMeisuiNowIgnoringNinki");

        if (action is NinAction.Meisui && s.TenChiJinReadyIn <= 10)
            s.AddHardFail(HardFailRule.TenChiJinMeisuiFailure, "Meisui must not be used before Ten Chi Jin when TCJ is ready or returns within 10s.", action, "CanUseMeisuiNowIgnoringNinki");

        if (action is NinAction.KunaisBane or NinAction.TrickAttack)
        {
            var mugReadyIn = s.MugReadyIn;
            if (s.TargetMugLeft <= 0 && s.TargetTrickLeft <= 0 && mugReadyIn > s.GcdLength + NinSimState.AnimLock && mugReadyIn < NinSimState.KunaiOddSkipSafetyWindow)
                s.AddHardFail(HardFailRule.KunaiMugSyncFailure, "Odd Kunai must be skipped when next Mug/Dokumori is less than 55s away.", action, "ShouldSkipOddKunaiForMugSync");
        }

        if (s.KassatsuActiveOrQueued && s.Scenario.Level >= 76)
        {
            if (action is NinAction.Raiton or NinAction.Katon)
                s.AddHardFail(HardFailRule.KassatsuFailure, "KassatsuActiveOrQueued must not flow into normal Raiton/Katon.", action, "KassatsuActiveOrQueued GCD branch");
            if (action is NinAction.ForkedRaiju or NinAction.FleetingRaiju or NinAction.PhantomKamaitachi or NinAction.TenChiJin)
                s.AddHardFail(HardFailRule.KassatsuFailure, "Kassatsu attack ninjutsu must be prioritized over Raiju/Phantom/TCJ.", action, "Kassatsu GCD priority");
        }

        if (reason.Contains("Kassatsu", StringComparison.OrdinalIgnoreCase))
        {
            if (s.Scenario.Level >= 76 && s.NumRangedAoeTargets > 1 && action != NinAction.GokaMekkyaku && action != NinAction.Kassatsu)
                s.AddHardFail(HardFailRule.KassatsuFailure, "Kassatsu on 2+ targets must resolve to Goka Mekkyaku.", action, "KassatsuAttackNinjutsu");
            if (s.NumRangedAoeTargets <= 1 && s.Scenario.Level >= 76 && action is not (NinAction.HyoshoRanryu or NinAction.Kassatsu))
                s.AddHardFail(HardFailRule.KassatsuFailure, "Kassatsu on single target at Lv76+ must resolve to Hyosho Ranryu.", action, "KassatsuAttackNinjutsu");
        }

        if (action is NinAction.RabbitMedium)
            s.AddHardFail(HardFailRule.RabbitOrMudraFailure, "Rabbit Medium must never be selected.", action, "UseMudraDetailed");

        if (action is NinAction.Raiton or NinAction.Katon or NinAction.Suiton or NinAction.Huton or NinAction.HyoshoRanryu or NinAction.GokaMekkyaku)
        {
            if (s.PendingNinjutsu != PendingNinjutsu.None)
                s.AddHardFail(HardFailRule.RabbitOrMudraFailure, "Pending ninjutsu must clear after final Ninjutsu.", action, "UseMudraDetailed");
        }

        if ((action is NinAction.Bhavacakra or NinAction.HellfrogMedium) && s.HigiLeft > 0 && s.Scenario.Level >= 96)
            s.AddHardFail(HardFailRule.NinkiFailure, "Higi should replace Bhavacakra/Hellfrog with Zesho/Deathfrog when unlocked.", action, "BhavacakraAction/HellfrogAction");
    }

    public static void CheckFrame(NinSimState s)
    {
        if (s.CanActOnEnemy && s.Time >= s.NextGcdAt && s.TenChiJinLeft <= 0 && s.Time - s.LastGcdActionAt > 5)
            s.AddHardFail(HardFailRule.GcdFreeze, "Targetable and GCD ready must not go 5s without a GCD candidate.", NinAction.None, "GCD selection");

        if (s.Targetable && s.LastTargetableAt >= 0 && s.Time - s.LastTargetableAt > 5 && s.CanActOnEnemy && s.Time - s.LastGcdActionAt > 5)
            s.AddHardFail(HardFailRule.TargetNullSafetyFailure, "Rotation must resume within 5s after target returns.", NinAction.None, "target fallback");

        if (s.HasRangedTarget && s.Time >= s.NextGcdAt && s.MudraCapStartedAt >= 0 && s.Time - s.MudraCapStartedAt > NinSimState.MaxAllowedMudraCapHold + 0.25 && s.MugReadyIn > 20 && s.TrickReadyIn > 20 && s.TargetMugLeft <= 0 && s.TargetTrickLeft <= 0)
            s.AddHardFail(HardFailRule.MudraStackFailure, "Two mudra stacks must not be held beyond MaxAllowedMudraCapHold when burst is not near.", NinAction.None, "ShouldSpendNormalNinjutsuToAvoidMudraOvercap");

        if (s.Ninki > 100)
            s.AddHardFail(HardFailRule.NinkiFailure, "Ninki must not exceed 100.", NinAction.None, "Ninki overcap prevention");
    }
}

public static class NinSoftRegressionRules
{
    public static void CheckAfterAction(NinSimState s, NinAction action)
    {
        if (action is NinAction.KunaisBane or NinAction.TrickAttack)
        {
            var drift = Math.Abs(s.TargetMugLeft - NinSimState.KunaiStartMugLeftForEndAlign);
            if (s.TargetMugLeft > 0 && drift > NinSimState.KunaiEndAlignSoftDriftTolerance && s.Scenario.BurstStyle == BurstStyle.Normal && !TargetWasLostDuringCurrentMugWindow(s) && TrickActionCouldHaveAlignedToMugEnd(s))
                s.AddSoft(SoftRegressionRule.KunaiEndAlignDrift, "Normal burst Kunai should stay within +/-1.0s of Dokumori end alignment.", action, "TrickActionDelay");
        }

        if (ShouldReportNinkiNearOvercap(s, action))
            s.AddSoft(SoftRegressionRule.NinkiNearOvercap, "Ninki at 95+ risks next Shukiho overcap.", action, "ShouldBhava");

        if (action is NinAction.TrueNorth && s.NextGcdAt - s.Time > 0.75)
            s.AddSoft(SoftRegressionRule.TrueNorthEarly, "True North should be delayed until the late weave window.", action, "ShouldUseTrueNorthNow");
    }

    private static bool ShouldReportNinkiNearOvercap(NinSimState s, NinAction action)
    {
        var isComboWeaponskill = action is NinAction.SpinningEdge or NinAction.GustSlash or NinAction.AeolianEdge or NinAction.ArmorCrush or NinAction.DeathBlossom or NinAction.HakkeMujinsatsu;
        if (s.BasicComboOnly || !isComboWeaponskill || s.Ninki <= 95)
            return false;

        if (s.Time <= 0.001)
            return false;

        return NinkiSpenderCouldWeaveBeforeCurrentGcd(s) && !RecentActionWithin(s, NinSimState.AnimLock + 0.05, IsMandatoryWeaveBeforeNinkiSpend);
    }

    private static bool NinkiSpenderCouldWeaveBeforeCurrentGcd(NinSimState s)
    {
        var previousGcd = s.Actions.LastOrDefault(a => a.Kind == NinActionKind.Gcd && a.Time < s.Time - 0.001);
        if (previousGcd == null)
            return false;

        var targetReturnedAt = s.Scenario.Events
            .Where(e => e.Type == NinScenarioEventType.TargetReturned && e.Time > previousGcd.Time && e.Time <= s.Time)
            .Select(e => e.Time)
            .DefaultIfEmpty(double.MinValue)
            .Max();
        var nextPossibleWeaveAt = Math.Max(previousGcd.Time + NinSimState.AnimLock, targetReturnedAt);
        return nextPossibleWeaveAt + NinSimState.AnimLock + 0.05 <= s.Time;
    }

    private static bool RecentActionWithin(NinSimState s, double seconds, Func<NinActionLog, bool> predicate)
        => s.Actions.Any(a => a.Time <= s.Time && s.Time - a.Time <= seconds && predicate(a));

    private static bool IsMandatoryWeaveBeforeNinkiSpend(NinActionLog action)
        => action.Kind is NinActionKind.Ogcd or NinActionKind.Item
            && action.Action is NinAction.Mug
                or NinAction.Dokumori
                or NinAction.TrickAttack
                or NinAction.KunaisBane
                or NinAction.Meisui
                or NinAction.Kassatsu
                or NinAction.TenChiJin
                or NinAction.Potion;

    private static bool TrickActionCouldHaveAlignedToMugEnd(NinSimState s)
    {
        if (s.TargetMugLeft <= 0)
            return false;

        if (s.TargetMugLeft >= NinSimState.KunaiStartMugLeftForEndAlign - 1.0)
            return true;

        var secondsSinceAlignedStart = NinSimState.KunaiStartMugLeftForEndAlign - s.TargetMugLeft;
        return s.TrickReadyIn + secondsSinceAlignedStart <= s.GcdLength + NinSimState.AnimLock;
    }

    private static bool TargetWasLostDuringCurrentMugWindow(NinSimState s)
    {
        if (s.TargetMugLeft <= 0)
            return false;

        var mugWindowStartedAt = s.Time - (NinSimState.MugDokumoriDuration - s.TargetMugLeft);
        return s.Scenario.Events.Any(e => e.Type == NinScenarioEventType.TargetLost && e.Time >= mugWindowStartedAt - 0.001 && e.Time <= s.Time + 0.001);
    }

    public static void CheckFrame(NinSimState s)
    {
        if (s.PhantomKamaitachiLeft <= 0 && s.Actions.Any(a => a.Action == NinAction.PhantomKamaitachi) == false && s.Time > 45 && s.Scenario.InitialPhantomKamaitachi > 0)
            s.AddSoft(SoftRegressionRule.PhantomExpired, "Phantom Kamaitachi should not expire unused.", NinAction.None, "ShouldPK");
        if (s.TenriJindoLeft <= 0 && s.Actions.Any(a => a.Action == NinAction.TenriJindo) == false && s.Time > 30 && s.Scenario.InitialTenriJindo > 0)
            s.AddSoft(SoftRegressionRule.TenriExpired, "Tenri Jindo Ready should not expire unused.", NinAction.None, "ShouldUseTenriJindoNow");
    }
}
