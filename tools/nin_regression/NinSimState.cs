namespace NinRegression;

public sealed class NinSimState
{
    public const double AnimLock = 0.70;
    public const double MugDokumoriDuration = 20.0;
    public const double TrickKunaiDuration = 15.0;
    public const double KunaiStartMugLeftForEndAlign = TrickKunaiDuration;
    public const double KunaiEndAlignTolerance = 0.25;
    public const double KunaiEndAlignSoftDriftTolerance = 1.50;
    public const double KunaiOddSkipSafetyWindow = 55.0;
    public const double MaxAllowedMudraCapHold = 4.0;
    public const int HighNinkiBurstSpendThreshold = 95;

    public required NinScenario Scenario { get; init; }
    public double Time { get; set; }
    public double GcdLength { get; set; }
    public double NextGcdAt { get; set; }
    public double NextOgcdAt { get; set; }
    public NinAction ComboLastMove { get; set; } = NinAction.None;
    public bool TargetExists { get; set; }
    public bool Targetable { get; set; }
    public bool PrimaryTargetNull { get; set; }
    public bool BestRangedAoeTargetNull { get; set; }
    public int NumAoeTargets { get; set; }
    public int NumRangedAoeTargets { get; set; }
    public int Ninki { get; set; }
    public int Kazematoi { get; set; }
    public int MudraCharges { get; set; }
    public double MudraRecharge { get; set; }
    public double MudraLeft { get; set; }
    public int MudraParam { get; set; }
    public PendingNinjutsu PendingNinjutsu { get; set; }
    public double MudraCapStartedAt { get; set; } = -1;
    public double KassatsuLeft { get; set; }
    public bool KassatsuQueuedThisFrame { get; set; }
    public double ShadowWalker { get; set; }
    public bool Hidden { get; set; }
    public double TargetMugLeft { get; set; }
    public double TargetTrickLeft { get; set; }
    public double TenChiJinLeft { get; set; }
    public int TenChiJinParam { get; set; }
    public double MeisuiLeft { get; set; }
    public double HigiLeft { get; set; }
    public int RaijuStacks { get; set; }
    public double PhantomKamaitachiLeft { get; set; }
    public double TenriJindoLeft { get; set; }
    public double PotionLeft { get; set; }
    public double PotionReadyIn { get; set; }
    public double MugReadyIn { get; set; }
    public double TrickReadyIn { get; set; }
    public double TenReadyIn { get; set; }
    public double KassatsuReadyIn { get; set; }
    public double TenChiJinReadyIn { get; set; }
    public double MeisuiReadyIn { get; set; }
    public double BunshinReadyIn { get; set; }
    public double DreamReadyIn { get; set; }
    public double NinkiSpendReadyIn { get; set; }
    public double TenriJindoReadyIn { get; set; }
    public double TrueNorthReadyIn { get; set; }
    public bool ManualRaitonRequested { get; set; }
    public double LastGcdActionAt { get; set; }
    public double LastTargetableAt { get; set; }
    public double LastMugReadyAt { get; set; } = -1;
    public double LastMudraCapCheckAt { get; set; } = -1;
    public List<NinActionLog> Actions { get; } = [];
    public List<NinFailure> HardFailures { get; } = [];
    public List<NinFailure> SoftRegressions { get; } = [];

    public bool CanActOnEnemy => TargetExists && Targetable && !PrimaryTargetNull;
    public bool HasRangedTarget => TargetExists && Targetable && (!BestRangedAoeTargetNull || !PrimaryTargetNull);
    public bool UseNinjutsuAoe => NumRangedAoeTargets > 2;
    public bool UseKassatsuNinjutsuAoe => NumRangedAoeTargets > 1;
    public bool UseMeleeAoe => NumAoeTargets > 2;
    public bool KassatsuActiveOrQueued => KassatsuLeft > 0 || KassatsuQueuedThisFrame;
    public bool HiddenForTrick => Hidden || ShadowWalker > AnimLock;
    public bool BasicComboOnly => Scenario.RotationStrategy == RotationStrategy.BasicComboOnly;
    public bool UltimateZeroSecond => Scenario.BurstStyle == BurstStyle.UltimateZeroSecond;
    public bool InEvenBurstForTcjMeisui => TargetMugLeft > GcdLength;

    public static NinSimState Create(NinScenario scenario)
    {
        return new NinSimState
        {
            Scenario = scenario,
            GcdLength = scenario.GcdLength,
            TargetExists = scenario.TargetExists,
            Targetable = scenario.Targetable,
            PrimaryTargetNull = scenario.PrimaryTargetNull,
            BestRangedAoeTargetNull = scenario.BestRangedAoeTargetNull,
            NumAoeTargets = scenario.NumAoeTargets,
            NumRangedAoeTargets = scenario.NumRangedAoeTargets,
            Ninki = scenario.InitialNinki,
            Kazematoi = scenario.InitialKazematoi,
            MudraCharges = Math.Clamp(scenario.InitialMudraCharges, 0, 2),
            PendingNinjutsu = scenario.InitialPendingNinjutsu,
            MugReadyIn = scenario.InitialMugReadyIn,
            TrickReadyIn = scenario.InitialKunaiReadyIn,
            KassatsuReadyIn = scenario.InitialKassatsuReadyIn,
            TenChiJinReadyIn = scenario.InitialTenChiJinReadyIn,
            MeisuiReadyIn = scenario.InitialMeisuiReadyIn,
            TargetMugLeft = scenario.InitialTargetMugLeft,
            TargetTrickLeft = scenario.InitialTargetTrickLeft,
            ShadowWalker = scenario.InitialShadowWalker,
            Hidden = scenario.Hidden,
            RaijuStacks = scenario.InitialRaiju,
            PhantomKamaitachiLeft = scenario.InitialPhantomKamaitachi,
            TenriJindoLeft = scenario.InitialTenriJindo,
            PotionReadyIn = scenario.PotionStrategy == PotionStrategy.EvenBurst ? 0 : 9999,
            BunshinReadyIn = 0,
            DreamReadyIn = 0,
            NinkiSpendReadyIn = 0,
            TenReadyIn = scenario.InitialMudraCharges > 0 ? 0 : 20,
            LastGcdActionAt = 0,
            LastTargetableAt = scenario.Targetable ? 0 : -1
        };
    }

    public void Tick(double dt)
    {
        KassatsuQueuedThisFrame = false;
        Time = Math.Round(Time + dt, 4);
        TargetMugLeft = Dec(TargetMugLeft, dt);
        TargetTrickLeft = Dec(TargetTrickLeft, dt);
        ShadowWalker = Dec(ShadowWalker, dt);
        KassatsuLeft = Dec(KassatsuLeft, dt);
        TenChiJinLeft = Dec(TenChiJinLeft, dt);
        MeisuiLeft = Dec(MeisuiLeft, dt);
        HigiLeft = Dec(HigiLeft, dt);
        PhantomKamaitachiLeft = Dec(PhantomKamaitachiLeft, dt);
        TenriJindoLeft = Dec(TenriJindoLeft, dt);
        PotionLeft = Dec(PotionLeft, dt);
        MugReadyIn = Dec(MugReadyIn, dt);
        TrickReadyIn = Dec(TrickReadyIn, dt);
        TenReadyIn = Dec(TenReadyIn, dt);
        KassatsuReadyIn = Dec(KassatsuReadyIn, dt);
        TenChiJinReadyIn = Dec(TenChiJinReadyIn, dt);
        MeisuiReadyIn = Dec(MeisuiReadyIn, dt);
        BunshinReadyIn = Dec(BunshinReadyIn, dt);
        DreamReadyIn = Dec(DreamReadyIn, dt);
        NinkiSpendReadyIn = Dec(NinkiSpendReadyIn, dt);
        TenriJindoReadyIn = Dec(TenriJindoReadyIn, dt);
        TrueNorthReadyIn = Dec(TrueNorthReadyIn, dt);
        PotionReadyIn = Dec(PotionReadyIn, dt);

        if (MudraCharges < 2)
        {
            MudraRecharge = Dec(MudraRecharge, dt);
            if (MudraRecharge <= 0)
            {
                MudraCharges++;
                if (MudraCharges < 2)
                    MudraRecharge = 20;
            }
        }

        if (MudraCharges >= 2 && MudraLeft <= 0)
        {
            if (MudraCapStartedAt < 0)
                MudraCapStartedAt = Time;
        }
        else
        {
            MudraCapStartedAt = -1;
        }

        if (Targetable)
            LastTargetableAt = Time;
    }

    public void ApplyEvent(NinScenarioEvent ev)
    {
        switch (ev.Type)
        {
            case NinScenarioEventType.TargetLost:
                Targetable = false;
                TargetExists = false;
                break;
            case NinScenarioEventType.TargetReturned:
                TargetExists = true;
                Targetable = true;
                PrimaryTargetNull = false;
                break;
            case NinScenarioEventType.PrimaryTargetNull:
                PrimaryTargetNull = ev.Value > 0;
                break;
            case NinScenarioEventType.BestRangedAoeNull:
                BestRangedAoeTargetNull = ev.Value > 0;
                break;
            case NinScenarioEventType.AoeTargets:
                NumAoeTargets = Math.Max(1, (int)ev.Value);
                break;
            case NinScenarioEventType.RangedAoeTargets:
                NumRangedAoeTargets = Math.Max(1, (int)ev.Value);
                break;
            case NinScenarioEventType.Ninki:
                Ninki = Math.Clamp((int)ev.Value, 0, 100);
                break;
            case NinScenarioEventType.MudraCharges:
                MudraCharges = Math.Clamp((int)ev.Value, 0, 2);
                break;
            case NinScenarioEventType.MugDrift:
                MugReadyIn = Math.Max(0, ev.Value);
                break;
            case NinScenarioEventType.KunaiDrift:
                TrickReadyIn = Math.Max(0, ev.Value);
                break;
            case NinScenarioEventType.KassatsuReady:
                KassatsuReadyIn = Math.Max(0, ev.Value);
                break;
            case NinScenarioEventType.TenChiJinReady:
                TenChiJinReadyIn = Math.Max(0, ev.Value);
                break;
            case NinScenarioEventType.MeisuiReady:
                MeisuiReadyIn = Math.Max(0, ev.Value);
                break;
            case NinScenarioEventType.ManualRaiton:
                ManualRaitonRequested = true;
                break;
            case NinScenarioEventType.PendingNinjutsu:
                PendingNinjutsu = ev.PendingNinjutsu;
                break;
            case NinScenarioEventType.Hidden:
                Hidden = ev.Value > 0;
                break;
            case NinScenarioEventType.ShadowWalker:
                ShadowWalker = Math.Max(0, ev.Value);
                break;
            case NinScenarioEventType.Raiju:
                RaijuStacks = Math.Max(0, (int)ev.Value);
                break;
            case NinScenarioEventType.PhantomKamaitachi:
                PhantomKamaitachiLeft = Math.Max(0, ev.Value);
                break;
            case NinScenarioEventType.TenriJindo:
                TenriJindoLeft = Math.Max(0, ev.Value);
                break;
            case NinScenarioEventType.RotationMode:
                break;
        }
    }

    public void Log(NinAction action, NinTargetKind targetKind, NinActionKind kind, string reason)
    {
        Actions.Add(new NinActionLog
        {
            Time = Math.Round(Time, 3),
            Action = action,
            TargetKind = targetKind,
            Kind = kind,
            Reason = reason,
            ComboState = ComboLastMove,
            Ninki = Ninki,
            MudraCharges = MudraCharges,
            PendingNinjutsu = PendingNinjutsu,
            Kassatsu = Math.Round(KassatsuLeft, 3),
            KassatsuQueuedThisFrame = KassatsuQueuedThisFrame,
            ShadowWalker = Math.Round(ShadowWalker, 3),
            TargetMugLeft = Math.Round(TargetMugLeft, 3),
            TargetTrickLeft = Math.Round(TargetTrickLeft, 3),
            TenChiJin = Math.Round(TenChiJinLeft, 3),
            Meisui = Math.Round(MeisuiLeft, 3),
            Raiju = RaijuStacks,
            Phantom = Math.Round(PhantomKamaitachiLeft, 3),
            Targetable = Targetable,
            TargetExists = TargetExists,
            AoeTargetCount = NumAoeTargets,
            RangedAoeTargetCount = NumRangedAoeTargets
        });
    }

    public string Summary()
        => $"t={Time:0.0}, gcdReady={Time >= NextGcdAt}, target={CanActOnEnemy}, aoe={NumRangedAoeTargets}, ninki={Ninki}, mudra={MudraCharges}, pending={PendingNinjutsu}, kassatsu={KassatsuLeft:0.0}, mugLeft={TargetMugLeft:0.0}, trickLeft={TargetTrickLeft:0.0}, mugCd={MugReadyIn:0.0}, trickCd={TrickReadyIn:0.0}, tcj={TenChiJinLeft:0.0}, meisui={MeisuiLeft:0.0}";

    public IReadOnlyList<NinActionLog> Last20Actions()
        => Actions.Count <= 20 ? Actions.ToList() : Actions.Skip(Actions.Count - 20).ToList();

    public void AddHardFail(HardFailRule rule, string expected, NinAction actual, string suspected)
    {
        if (HardFailures.Any(f => f.Rule == rule.ToString() && f.ExpectedInvariant == expected))
            return;

        HardFailures.Add(new NinFailure
        {
            ScenarioName = Scenario.Name,
            Seed = Scenario.Seed,
            Time = Math.Round(Time, 3),
            StateSummary = Summary(),
            Last20Actions = Last20Actions(),
            ExpectedInvariant = expected,
            ActualAction = actual.ToString(),
            SuspectedFunctionName = suspected,
            Severity = "HardFail",
            Rule = rule.ToString()
        });
    }

    public void AddSoft(SoftRegressionRule rule, string expected, NinAction actual, string suspected)
    {
        if (SoftRegressions.Any(f => f.Rule == rule.ToString() && f.ExpectedInvariant == expected))
            return;

        SoftRegressions.Add(new NinFailure
        {
            ScenarioName = Scenario.Name,
            Seed = Scenario.Seed,
            Time = Math.Round(Time, 3),
            StateSummary = Summary(),
            Last20Actions = Last20Actions(),
            ExpectedInvariant = expected,
            ActualAction = actual.ToString(),
            SuspectedFunctionName = suspected,
            Severity = "SoftRegression",
            Rule = rule.ToString()
        });
    }

    public static double Dec(double value, double dt)
        => value <= 0 ? 0 : Math.Max(0, value - dt);
}
