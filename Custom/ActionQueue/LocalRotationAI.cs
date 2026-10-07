using System.IO;
using System.Text.Json;

namespace BossMod;

// Single in-process entry point for the supported FFXIV combat policies.
// Rotation modules remain authoritative for legality, targeting, strategy modes, and safety-critical priorities.
public static class FFXIVInProcessRotationAI
{
    public static ActionQueue.Entry Select(ActionQueue queue, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, float instantAnimLockDelay, bool allowDismount)
    {
        if (!SupportedJob(player.Class))
            return baseline;

        var criticSelection = LocalRotationAI.Select(queue, baseline, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount);
        var plannerSelection = MchRealtimeValuePlanner.Select(queue, criticSelection, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount);
        return FunctionGemmaRotationSupervisor.Select(queue, plannerSelection, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount);
    }

    public static bool SupportedJob(Class job)
        => job is Class.MNK or Class.RPR or Class.MCH or Class.BLM or Class.SAM or Class.VPR or Class.GNB or Class.PLD or Class.NIN;
}

// Optional in-process expected-DPS critic for choosing between near-equivalent rotation candidates.
// Existing rotation modules remain responsible for legal candidates and all safety-critical priorities.
public sealed class LocalRotationAI
{
    public const int FeatureCount = 64;
    public const int FeatureSchemaVersion = 2;
    private const float HardMaxPriorityLoss = 1f;
    private const int MaxHiddenSize = 128;
    private const int MaxEnsembleSize = 16;
    private static readonly Lazy<LocalRotationAI?> Instance = new(LoadBaseModel);
    private static readonly Lazy<Dictionary<Class, LocalRotationAI>> JobInstances = new(LoadJobModels);

    private sealed class ModelHead
    {
        public float[] InputWeights { get; set; } = [];
        public float[] HiddenBias { get; set; } = [];
        public float[] OutputWeights { get; set; } = [];
        public float OutputBias { get; set; }
        public Dictionary<uint, float> ActionBias { get; set; } = [];
    }

    private sealed class ModelFile
    {
        public int Version { get; set; }
        public bool Enabled { get; set; }
        public string Objective { get; set; } = "";
        public int FeatureSchemaVersion { get; set; }
        public int FeatureCount { get; set; }
        public int HiddenSize { get; set; }
        public float MaxPriorityLoss { get; set; }
        public float MinimumExpectedDPSGainRatio { get; set; }
        public float ConfidenceMultiplier { get; set; }
        public float ValidationError { get; set; }
        public Dictionary<string, float> ValidationErrorByJob { get; set; } = [];
        public int MinimumActionSamples { get; set; }
        public float[] FeatureMean { get; set; } = [];
        public float[] FeatureScale { get; set; } = [];
        public ModelHead[] Heads { get; set; } = [];
        public Dictionary<uint, int> ActionSupport { get; set; } = [];
        public TrainingMetadata? TrainingMetadata { get; set; }
    }

    private sealed class LegacyModelFile
    {
        public int Version { get; set; }
        public bool Enabled { get; set; }
        public int FeatureCount { get; set; }
        public int HiddenSize { get; set; }
        public float MaxPriorityLoss { get; set; }
        public float MinimumScoreMargin { get; set; }
        public float[] FeatureMean { get; set; } = [];
        public float[] FeatureScale { get; set; } = [];
        public float[] InputWeights { get; set; } = [];
        public float[] HiddenBias { get; set; } = [];
        public float[] OutputWeights { get; set; } = [];
        public float OutputBias { get; set; }
        public Dictionary<uint, float> ActionBias { get; set; } = [];
        public Dictionary<uint, int> ActionSupport { get; set; } = [];
        public TrainingMetadata? TrainingMetadata { get; set; }
    }

    private sealed class TrainingMetadata
    {
        public string[] Jobs { get; set; } = [];
        public int MinimumActionSamples { get; set; }
    }

    private readonly record struct Estimate(float Mean, float Uncertainty)
    {
        public float Lower(float multiplier) => Mean - Uncertainty * multiplier;
        public float Upper(float multiplier) => Mean + Uncertainty * multiplier;
    }

    public readonly record struct CapturedCandidate(uint ActionRaw, bool Baseline, bool Selected, float[] Features);

    private readonly ModelFile? _model;
    private readonly LegacyModelFile? _legacyModel;
    private readonly HashSet<Class> _trainedJobs;

    private LocalRotationAI(ModelFile model)
    {
        _model = model;
        _trainedJobs = TrainedSupportedJobs(model.TrainingMetadata!.Jobs);
    }

    private LocalRotationAI(LegacyModelFile model)
    {
        _legacyModel = model;
        _trainedJobs = TrainedSupportedJobs(model.TrainingMetadata!.Jobs);
    }

    public static ActionQueue.Entry Select(ActionQueue queue, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, float instantAnimLockDelay, bool allowDismount)
    {
        if (!FFXIVInProcessRotationAI.SupportedJob(player.Class))
            return baseline;

        var critic = JobInstances.Value.GetValueOrDefault(player.Class) ?? Instance.Value;
        if (critic == null || !critic._trainedJobs.Contains(player.Class))
            return SelectEmbedded(queue, baseline, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount);
        if (!baseline.Action || baseline.Target == null || baseline.Target.IsAlly)
            return baseline;
        if (!critic.IsEligible(baseline, baseline, ws, player, cooldowns, animationLock, hints, queue, allowDismount))
            return SelectEmbedded(queue, baseline, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount);

        var baselineDef = ActionDefinitions.Instance[baseline.Action]!;
        var baselineStart = StartDelay(baseline, baselineDef, ws, cooldowns, animationLock);
        var baselineDuration = Duration(baselineDef, instantAnimLockDelay);
        if (critic._legacyModel != null)
            return critic.SelectLegacy(queue, baseline, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount);

        var baselineEstimate = critic.EstimateDPS(baseline, baseline, ws, player, cooldowns, animationLock, hints);
        var best = baseline;
        var bestMean = baselineEstimate.Mean;

        foreach (var candidate in queue.Entries)
        {
            if (!critic.IsEligible(candidate, baseline, ws, player, cooldowns, animationLock, hints, queue, allowDismount))
                continue;

            var def = ActionDefinitions.Instance[candidate.Action]!;
            if (def.IsGCD != baselineDef.IsGCD || !CompatibleTarget(candidate.Target, baseline.Target, player.Class))
                continue;

            var start = StartDelay(candidate, def, ws, cooldowns, animationLock);
            if (start > baselineStart + 0.01f || Duration(def, instantAnimLockDelay) > baselineDuration + 0.01f)
                continue;

            var estimate = critic.EstimateDPS(candidate, baseline, ws, player, cooldowns, animationLock, hints);
            var expectedGainRatio = (estimate.Mean - baselineEstimate.Mean) / Math.Max(Math.Abs(baselineEstimate.Mean), 0.0001f);
            var confidenceSeparated = estimate.Lower(critic._model!.ConfidenceMultiplier) > baselineEstimate.Upper(critic._model.ConfidenceMultiplier);
            if (expectedGainRatio >= critic._model.MinimumExpectedDPSGainRatio && confidenceSeparated && estimate.Mean > bestMean)
            {
                best = candidate;
                bestMean = estimate.Mean;
            }
        }

        return best.Action != baseline.Action
            ? best
            : SelectEmbedded(queue, baseline, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount);
    }

    private static ActionQueue.Entry SelectEmbedded(ActionQueue queue, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, float instantAnimLockDelay, bool allowDismount)
    {
        if (!baseline.Action || baseline.Action.Type != ActionType.Spell || baseline.Target == null || baseline.Target.IsAlly || baseline.Manual || baseline.Force || baseline.Expire != float.MaxValue)
            return baseline;

        var baselineDefinition = ActionDefinitions.Instance[baseline.Action];
        if (baselineDefinition == null)
            return baseline;

        var baselineStart = StartDelay(baseline, baselineDefinition, ws, cooldowns, animationLock);
        var baselineDuration = Duration(baselineDefinition, instantAnimLockDelay);
        var baselineChargeUrgent = ChargeUrgent(baselineDefinition, ws, cooldowns, player.Level);
        var best = baseline;
        var bestCapIn = float.MaxValue;
        foreach (var candidate in queue.Entries)
        {
            if (candidate.Action.Type != ActionType.Spell || candidate.Target != baseline.Target || candidate.Manual || candidate.Force || candidate.Expire != float.MaxValue || candidate.Priority != baseline.Priority)
                continue;

            var definition = ActionDefinitions.Instance[candidate.Action];
            if (definition == null || definition.IsGCD != baselineDefinition.IsGCD || !definition.IsUnlocked(ws, player) || candidate.CastTime > hints.MaxCastTime)
                continue;
            if (StartDelay(candidate, definition, ws, cooldowns, animationLock) > baselineStart + 0.01f || Duration(definition, instantAnimLockDelay) > baselineDuration + 0.01f)
                continue;
            if (!queue.CanExecuteEx(in candidate, definition, ws, player, hints, allowDismount))
                continue;

            if (candidate.Action == baseline.Action)
            {
                if (candidate.CastTime + 0.01f < best.CastTime)
                    best = candidate;
                continue;
            }

            if (baselineChargeUrgent || !ChargeUrgent(definition, ws, cooldowns, player.Level))
                continue;

            var capIn = definition.ChargeCapIn(cooldowns, ws.Client.DutyActions, player.Level);
            if (capIn < bestCapIn)
            {
                best = candidate;
                bestCapIn = capIn;
            }
        }

        return best;
    }

    private static bool ChargeUrgent(ActionDefinition definition, WorldState ws, ReadOnlySpan<Cooldown> cooldowns, int level)
        => definition.Cooldown >= 10 && definition.MaxChargesAtLevel(level) > 1 && definition.ChargeCapIn(cooldowns, ws.Client.DutyActions, level) <= 2.5f;

    private bool IsEligible(ActionQueue.Entry candidate, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, ActionQueue queue, bool allowDismount)
    {
        var support = _model != null
            ? _model.ActionSupport.GetValueOrDefault(candidate.Action.Raw)
            : _legacyModel!.ActionSupport.GetValueOrDefault(candidate.Action.Raw);
        var minimumActionSamples = _model?.MinimumActionSamples ?? _legacyModel!.TrainingMetadata!.MinimumActionSamples;
        if (support < minimumActionSamples)
            return false;
        var maxPriorityLoss = _model?.MaxPriorityLoss ?? _legacyModel!.MaxPriorityLoss;
        return IsStructurallyEligible(candidate, baseline, ws, player, hints, queue, allowDismount, Math.Min(maxPriorityLoss, HardMaxPriorityLoss));
    }

    private ActionQueue.Entry SelectLegacy(ActionQueue queue, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, float instantAnimLockDelay, bool allowDismount)
    {
        var baselineDefinition = ActionDefinitions.Instance[baseline.Action]!;
        var baselineStart = StartDelay(baseline, baselineDefinition, ws, cooldowns, animationLock);
        var baselineDuration = Duration(baselineDefinition, instantAnimLockDelay);
        var baselineScore = EstimateLegacy(baseline, baseline, ws, player, cooldowns, animationLock, hints);
        var best = baseline;
        var bestScore = baselineScore;

        foreach (var candidate in queue.Entries)
        {
            if (!IsEligible(candidate, baseline, ws, player, cooldowns, animationLock, hints, queue, allowDismount))
                continue;

            var definition = ActionDefinitions.Instance[candidate.Action]!;
            if (definition.IsGCD != baselineDefinition.IsGCD || !CompatibleTarget(candidate.Target, baseline.Target, player.Class))
                continue;
            if (StartDelay(candidate, definition, ws, cooldowns, animationLock) > baselineStart + 0.01f || Duration(definition, instantAnimLockDelay) > baselineDuration + 0.01f)
                continue;

            var score = EstimateLegacy(candidate, baseline, ws, player, cooldowns, animationLock, hints);
            if (score >= baselineScore + _legacyModel!.MinimumScoreMargin && score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best.Action != baseline.Action
            ? best
            : SelectEmbedded(queue, baseline, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount);
    }

    private float EstimateLegacy(ActionQueue.Entry candidate, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints)
    {
        Span<float> features = stackalloc float[_legacyModel!.FeatureCount];
        FillFeatures(features, candidate, baseline, ws, player, cooldowns, animationLock, hints);
        var score = _legacyModel.OutputBias;
        for (var hiddenIndex = 0; hiddenIndex < _legacyModel.HiddenSize; ++hiddenIndex)
        {
            var hidden = _legacyModel.HiddenBias[hiddenIndex];
            var offset = hiddenIndex * _legacyModel.FeatureCount;
            for (var featureIndex = 0; featureIndex < _legacyModel.FeatureCount; ++featureIndex)
            {
                var normalized = (features[featureIndex] - _legacyModel.FeatureMean[featureIndex]) / _legacyModel.FeatureScale[featureIndex];
                hidden += normalized * _legacyModel.InputWeights[offset + featureIndex];
            }
            score += Math.Max(hidden, 0) * _legacyModel.OutputWeights[hiddenIndex];
        }

        if (_legacyModel.ActionBias.TryGetValue(candidate.Action.Raw, out var actionBias))
            score += actionBias;
        return score;
    }

    public static CapturedCandidate[] CaptureCandidates(ActionQueue queue, ActionQueue.Entry baseline, ActionQueue.Entry selected, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, float instantAnimLockDelay, bool allowDismount)
    {
        if (!FFXIVInProcessRotationAI.SupportedJob(player.Class) || !baseline.Action || baseline.Target == null || baseline.Target.IsAlly || !IsStructurallyEligible(baseline, baseline, ws, player, hints, queue, allowDismount, HardMaxPriorityLoss))
            return [];

        var baselineDef = ActionDefinitions.Instance[baseline.Action]!;
        var baselineStart = StartDelay(baseline, baselineDef, ws, cooldowns, animationLock);
        var baselineDuration = Duration(baselineDef, instantAnimLockDelay);
        var result = new Dictionary<uint, CapturedCandidate>();
        foreach (var candidate in queue.Entries)
        {
            if (candidate.Action == baseline.Action && !SameEntry(candidate, baseline))
                continue;
            if (candidate.Action == selected.Action && candidate.Action != baseline.Action && !SameEntry(candidate, selected))
                continue;
            if (!IsStructurallyEligible(candidate, baseline, ws, player, hints, queue, allowDismount, HardMaxPriorityLoss))
                continue;
            var def = ActionDefinitions.Instance[candidate.Action]!;
            if (def.IsGCD != baselineDef.IsGCD || !CompatibleTarget(candidate.Target, baseline.Target, player.Class))
                continue;
            if (StartDelay(candidate, def, ws, cooldowns, animationLock) > baselineStart + 0.01f || Duration(def, instantAnimLockDelay) > baselineDuration + 0.01f)
                continue;
            if (result.ContainsKey(candidate.Action.Raw))
                continue;

            var features = new float[FeatureCount];
            FillFeatures(features, candidate, baseline, ws, player, cooldowns, animationLock, hints);
            result.Add(candidate.Action.Raw, new(candidate.Action.Raw, candidate.Action == baseline.Action, candidate.Action == selected.Action, features));
        }
        return result.ContainsKey(selected.Action.Raw) ? [.. result.Values] : [];
    }

    private static bool SameEntry(ActionQueue.Entry left, ActionQueue.Entry right)
        => left.Action == right.Action
            && left.Target == right.Target
            && left.Priority == right.Priority
            && left.Expire == right.Expire
            && left.Delay == right.Delay
            && left.CastTime == right.CastTime
            && left.TargetPos == right.TargetPos
            && left.FacingAngle == right.FacingAngle
            && left.Manual == right.Manual
            && left.Force == right.Force;

    private static bool IsStructurallyEligible(ActionQueue.Entry candidate, ActionQueue.Entry baseline, WorldState ws, Actor player, AIHints hints, ActionQueue queue, bool allowDismount, float maxPriorityLoss)
    {
        if (candidate.Action.Type != ActionType.Spell || candidate.Manual || candidate.Force || candidate.Expire != float.MaxValue)
            return false;
        if (candidate.Priority < ActionQueue.Priority.VeryLow || candidate.Priority >= ActionQueue.Priority.ManualGCD)
            return false;
        if (candidate.Priority < baseline.Priority - maxPriorityLoss || (int)(candidate.Priority / 1000) != (int)(baseline.Priority / 1000))
            return false;
        var def = ActionDefinitions.Instance[candidate.Action];
        if (def == null || !def.IsUnlocked(ws, player) || candidate.CastTime > hints.MaxCastTime)
            return false;
        if (hints.FindEnemy(candidate.Target)?.Priority == AIHints.Enemy.PriorityForbidden)
            return false;
        return queue.CanExecuteEx(in candidate, def, ws, player, hints, allowDismount);
    }

    private Estimate EstimateDPS(ActionQueue.Entry candidate, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints)
    {
        Span<float> features = stackalloc float[FeatureCount];
        FillFeatures(features, candidate, baseline, ws, player, cooldowns, animationLock, hints);
        Span<float> predictions = stackalloc float[_model!.Heads.Length];
        var mean = 0f;

        for (var index = 0; index < _model.Heads.Length; ++index)
        {
            var head = _model.Heads[index];
            var score = head.OutputBias;
            for (var h = 0; h < _model.HiddenSize; ++h)
            {
                var hidden = head.HiddenBias[h];
                var offset = h * FeatureCount;
                for (var f = 0; f < FeatureCount; ++f)
                {
                    var normalized = (features[f] - _model.FeatureMean[f]) / _model.FeatureScale[f];
                    hidden += normalized * head.InputWeights[offset + f];
                }
                score += Math.Max(hidden, 0) * head.OutputWeights[h];
            }

            if (head.ActionBias.TryGetValue(candidate.Action.Raw, out var actionBias))
                score += actionBias;
            predictions[index] = score;
            mean += score;
        }

        mean /= predictions.Length;
        var variance = 0f;
        foreach (var prediction in predictions)
            variance += (prediction - mean) * (prediction - mean);
        var ensembleDeviation = MathF.Sqrt(variance / predictions.Length);
        var validationError = _model.ValidationErrorByJob.GetValueOrDefault(player.Class.ToString(), _model.ValidationError);
        return new(mean, Math.Max(ensembleDeviation, validationError));
    }

    private static void FillFeatures(Span<float> result, ActionQueue.Entry candidate, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints)
    {
        var def = ActionDefinitions.Instance[candidate.Action]!;
        var readyIn = def.ReadyIn(cooldowns, ws.Client.DutyActions);
        var capIn = def.ChargeCapIn(cooldowns, ws.Client.DutyActions, player.Level);
        var target = candidate.Target;
        var distance = target != null ? Math.Max(0, MathF.Sqrt((target.Position - player.Position).LengthSq()) - player.HitboxRadius - target.HitboxRadius) : 0;
        var targetEnemy = hints.FindEnemy(target);
        var actionHash = unchecked(candidate.Action.Raw * 2654435761u);
        var actionHashAngle = actionHash * (MathF.Tau / uint.MaxValue);

        result[0] = Math.Clamp(candidate.Priority - baseline.Priority, -HardMaxPriorityLoss, HardMaxPriorityLoss);
        result[1] = candidate.Priority == baseline.Priority ? 1 : 0;
        result[2] = Math.Clamp(candidate.Delay / 2.5f, 0, 1);
        result[3] = Math.Clamp(readyIn / 10, 0, 1);
        result[4] = Math.Clamp(candidate.CastTime / 5, 0, 1);
        result[5] = Math.Clamp(def.CastTime / 5, 0, 1);
        result[6] = Math.Clamp(animationLock / 1.5f, 0, 1);
        result[7] = Math.Clamp(def.Cooldown / 120, 0, 1);
        result[8] = Math.Clamp(capIn / 120, 0, 1);
        result[9] = def.IsGCD ? 1 : 0;
        result[10] = def.Category == ActionCategory.Ability ? 1 : 0;
        result[11] = Ratio(player.HPMP.CurHP, player.HPMP.MaxHP);
        result[12] = Ratio(player.HPMP.CurMP, player.HPMP.MaxMP);
        result[13] = target != null ? Ratio(target.HPMP.CurHP, target.HPMP.MaxHP) : 0;
        result[14] = Math.Clamp(distance / 25, 0, 1);
        result[15] = target?.IsAlly == true ? 1 : 0;
        result[16] = player.InCombat ? 1 : 0;
        result[17] = player.LastFrameMovement.LengthSq() > 0.0001f ? 1 : 0;
        result[18] = Math.Clamp(hints.PriorityTargetsSpan.Length / 8f, 0, 1);
        result[19] = MathF.Sin(actionHashAngle);
        result[20] = MathF.Cos(actionHashAngle);
        result[21] = (float)player.Class / (float)Class.PCT;
        result[22] = Math.Clamp(player.Level / 100f, 0, 1);
        result[23] = targetEnemy?.Priority == AIHints.Enemy.PriorityPointless ? 1 : 0;
        result[24] = target == null ? 1 : 0;
        if (result.Length < FeatureCount)
            return;

        var (gaugeLowMask, gaugeHighMask) = GaugeMasks(player.Class);
        var gaugeLow = ws.Client.GaugePayload.Low & gaugeLowMask;
        var gaugeHigh = ws.Client.GaugePayload.High & gaugeHighMask;
        for (var index = 0; index < 8; ++index)
        {
            result[25 + index] = ((gaugeLow >> (index * 8)) & 0xFF) / 255f;
            result[33 + index] = ((gaugeHigh >> (index * 8)) & 0xFF) / 255f;
        }

        var comboAction = ws.Client.ComboState.Action;
        var comboHash = unchecked(comboAction * 2654435761u);
        var comboHashAngle = comboHash * (MathF.Tau / uint.MaxValue);
        result[41] = Math.Clamp(ws.Client.ComboState.Remaining / 30, 0, 1);
        result[42] = MathF.Sin(comboHashAngle);
        result[43] = MathF.Cos(comboHashAngle);
        result[44] = cooldowns.Length > ActionDefinitions.GCDGroup ? Math.Clamp(cooldowns[ActionDefinitions.GCDGroup].Remaining / 2.5f, 0, 1) : 0;
        result[45] = Math.Clamp((ws.Client.CountdownRemaining ?? 0) / 30, 0, 1);
        result[46] = Math.Clamp(hints.PotentialTargets.Count / 8f, 0, 1);
        result[47] = target?.CastInfo is { } cast ? Math.Clamp((cast.TotalTime - cast.ElapsedTime) / 10, 0, 1) : 0;
        var activeStatusCount = 0;
        foreach (var status in player.Statuses)
            activeStatusCount += status.ID != 0 ? 1 : 0;
        result[48] = Math.Clamp(activeStatusCount / 30f, 0, 1);
        FillStatusBuckets(result[49..57], player, ws);
        if (target != null)
            FillStatusBuckets(result[57..64], target, ws);
    }

    private static (ulong Low, ulong High) GaugeMasks(Class job)
        => job switch
        {
            Class.MNK => (ulong.MaxValue, 0),
            Class.RPR => (0x0000FFFFFFFFFFFF, 0),
            Class.MCH => (ulong.MaxValue, 0),
            Class.BLM => (0x0000FFFFFFFFFFFF, 0),
            Class.SAM => (0x0000FFFFFFFF0000, 0),
            Class.VPR => (0xFFFF0000FFFFFFFF, 0x00000000000000FF),
            Class.GNB => (0x000000FFFFFF00FF, 0),
            Class.PLD => (0x0000FFFFFFFF00FF, 0),
            Class.NIN => (0x0000000000FF00FF, 0),
            _ => (0, 0),
        };

    private static void FillStatusBuckets(Span<float> buckets, Actor actor, WorldState ws)
    {
        foreach (var status in actor.Statuses)
        {
            if (status.ID == 0)
                continue;
            var remaining = (float)(status.ExpireAt - ws.CurrentTime).TotalSeconds;
            if (remaining <= 0)
                continue;
            var index = (int)(status.ID % (uint)buckets.Length);
            buckets[index] = Math.Max(buckets[index], Math.Clamp(remaining / 60, 0, 1));
        }
    }

    private static bool CompatibleTarget(Actor? candidate, Actor? baseline, Class job)
        => candidate == baseline
            || job == Class.MCH && candidate != null && baseline != null && !candidate.IsAlly && !baseline.IsAlly;

    private static float Ratio(uint current, uint maximum) => maximum > 0 ? Math.Clamp((float)current / maximum, 0, 1) : 0;
    private static float StartDelay(ActionQueue.Entry entry, ActionDefinition def, WorldState ws, ReadOnlySpan<Cooldown> cooldowns, float animationLock)
        => Math.Max(Math.Max(entry.Delay, animationLock), def.ReadyIn(cooldowns, ws.Client.DutyActions));
    private static float Duration(ActionDefinition def, float instantAnimLockDelay)
        => def.CastTime > 0 ? def.CastTime + def.CastAnimLock : def.InstantAnimLock + instantAnimLockDelay;

    private static LocalRotationAI? LoadBaseModel()
    {
        var path = Environment.GetEnvironmentVariable("BOSSMOD_LOCAL_AI_MODEL");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        return LoadModel(path);
    }

    private static Dictionary<Class, LocalRotationAI> LoadJobModels()
    {
        Dictionary<Class, LocalRotationAI> result = [];
        var basePath = Environment.GetEnvironmentVariable("BOSSMOD_LOCAL_AI_MODEL");
        if (string.IsNullOrWhiteSpace(basePath))
            return result;
        var modelDirectory = Path.GetDirectoryName(Path.GetFullPath(basePath));
        if (modelDirectory == null)
            return result;

        foreach (var job in Enum.GetValues<Class>())
        {
            if (!FFXIVInProcessRotationAI.SupportedJob(job))
                continue;
            var model = LoadModel(Path.Combine(modelDirectory, "jobs", $"{job}.json"));
            if (model != null && model._trainedJobs.SetEquals([job]))
                result.Add(job, model);
        }
        return result;
    }

    private static LocalRotationAI? LoadModel(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("version", out var versionElement))
                return null;

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return versionElement.GetInt32() switch
            {
                1 => LoadLegacy(json, options),
                2 => LoadExpectedDPS(json, options),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static LocalRotationAI? LoadLegacy(string json, JsonSerializerOptions options)
    {
        var model = JsonSerializer.Deserialize<LegacyModelFile>(json, options);
        return model != null && ValidateLegacy(model) ? new(model) : null;
    }

    private static LocalRotationAI? LoadExpectedDPS(string json, JsonSerializerOptions options)
    {
        var model = JsonSerializer.Deserialize<ModelFile>(json, options);
        return model != null && Validate(model) ? new(model) : null;
    }

    private static HashSet<Class> TrainedSupportedJobs(IEnumerable<string> jobs)
    {
        HashSet<Class> result = [];
        foreach (var job in jobs)
        {
            if (Enum.TryParse<Class>(job, out var parsed) && FFXIVInProcessRotationAI.SupportedJob(parsed))
                result.Add(parsed);
        }
        return result;
    }

    private static bool ValidateLegacy(LegacyModelFile model)
    {
        if (!model.Enabled || model.Version != 1 || model.FeatureCount != 25 || model.HiddenSize is <= 0 or > MaxHiddenSize)
            return false;
        if (model.MaxPriorityLoss is < 0 or > HardMaxPriorityLoss || !float.IsFinite(model.MinimumScoreMargin) || model.MinimumScoreMargin <= 0)
            return false;
        if (model.FeatureMean.Length != model.FeatureCount || model.FeatureScale.Length != model.FeatureCount || model.InputWeights.Length != model.HiddenSize * model.FeatureCount || model.HiddenBias.Length != model.HiddenSize || model.OutputWeights.Length != model.HiddenSize)
            return false;
        if (model.FeatureScale.Any(value => !float.IsFinite(value) || value <= 0) || model.FeatureMean.Any(value => !float.IsFinite(value)) || model.InputWeights.Any(value => !float.IsFinite(value)) || model.HiddenBias.Any(value => !float.IsFinite(value)) || model.OutputWeights.Any(value => !float.IsFinite(value)) || !float.IsFinite(model.OutputBias))
            return false;
        if (model.ActionBias.Any(pair => !float.IsFinite(pair.Value)) || model.ActionSupport.Any(pair => pair.Value < 0))
            return false;
        if (model.TrainingMetadata == null || model.TrainingMetadata.MinimumActionSamples <= 0 || TrainedSupportedJobs(model.TrainingMetadata.Jobs).Count == 0)
            return false;
        return true;
    }

    private static bool Validate(ModelFile model)
    {
        if (!model.Enabled || model.Version != 2 || model.Objective != "expected_dps_ratio" || model.FeatureSchemaVersion != FeatureSchemaVersion || model.FeatureCount != FeatureCount || model.HiddenSize is <= 0 or > MaxHiddenSize)
            return false;
        if (model.Heads.Length is <= 1 or > MaxEnsembleSize || model.MaxPriorityLoss is < 0 or > HardMaxPriorityLoss)
            return false;
        if (!float.IsFinite(model.MinimumExpectedDPSGainRatio) || model.MinimumExpectedDPSGainRatio <= 0 || !float.IsFinite(model.ConfidenceMultiplier) || model.ConfidenceMultiplier <= 0)
            return false;
        if (!float.IsFinite(model.ValidationError) || model.ValidationError <= 0 || model.MinimumActionSamples <= 0)
            return false;
        if (model.ValidationErrorByJob.Count == 0 || model.ValidationErrorByJob.Any(pair => !float.IsFinite(pair.Value) || pair.Value <= 0))
            return false;
        if (model.FeatureMean.Length != FeatureCount || model.FeatureScale.Length != FeatureCount)
            return false;
        if (model.FeatureScale.Any(value => !float.IsFinite(value) || value <= 0) || model.FeatureMean.Any(value => !float.IsFinite(value)))
            return false;
        if (model.ActionSupport.Any(pair => pair.Value < 0))
            return false;
        if (model.TrainingMetadata == null || TrainedSupportedJobs(model.TrainingMetadata.Jobs).Count == 0 || model.TrainingMetadata.Jobs.Any(job => !model.ValidationErrorByJob.ContainsKey(job)))
            return false;

        foreach (var head in model.Heads)
        {
            if (head.InputWeights.Length != model.HiddenSize * FeatureCount || head.HiddenBias.Length != model.HiddenSize || head.OutputWeights.Length != model.HiddenSize)
                return false;
            if (head.InputWeights.Any(value => !float.IsFinite(value)) || head.HiddenBias.Any(value => !float.IsFinite(value)) || head.OutputWeights.Any(value => !float.IsFinite(value)) || !float.IsFinite(head.OutputBias))
                return false;
            if (head.ActionBias.Any(pair => !float.IsFinite(pair.Value)))
                return false;
        }
        return true;
    }
}
