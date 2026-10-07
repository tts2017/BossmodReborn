using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BossMod.Autorotation.Engine;

// The small set of tunable numbers a job supplies (weights/<JOB>.json). Everything else comes from the skill definitions.
public sealed class EngineWeights
{
    public float OverCap { get; set; } = 1.0f;        // multiplier on the value of wasted gauge / idle cooldown time
    public float Combo { get; set; } = 1.0f;          // leaf credit for an open combo (x the next step's bonus potency)
    public float LambdaScale { get; set; } = 1.0f;    // multiplier on the upper tier's shadow prices at the leaf
    public float TargetPull { get; set; } = 0.0f;     // weak pull towards the upper tier's holding target, per unit of difference
    public float SwitchMargin { get; set; } = 20f;    // hysteresis: keep the previous plan unless the new one is this much better
    public float FillerScale { get; set; } = 1.0f;    // multiplier on the filler rate used to fill/charge the horizon
    public float BurstBias { get; set; } = 1.0f;      // exaggerates (>1) or flattens (<1) raid-buff multipliers in the upper tier
    public float StatusRemainder { get; set; } = 1.0f; // multiplier on the leaf value of remaining damage-multiplier statuses
    public float CycleScale { get; set; } = 1.0f;      // multiplier on the CycleModel leaf value (jobs that declare a cycle state)
    public float CooldownLambdaScale { get; set; } = -1;  // multiplier on the shadow prices of cooldown charges; negative = same as LambdaScale
    public float ForecastSelfBuffs { get; set; }          // >0: the upper tier plans around the job's own damage-buff cooldowns as if they were raid buffs
    public float UnlockScale { get; set; }                // multiplier on JobAnalysis.CdUnlockValue (cooldowns that unlock other skills)
    public Dictionary<string, float> StatusValue { get; set; } = []; // extra per-second leaf value of a status (upkeep debuffs, modes)
    public Dictionary<string, float> CooldownValue { get; set; } = []; // extra value of one charge (potency) on top of what the definition shows (e.g. follow-up GCDs it unlocks)
    public Dictionary<string, float> GaugeValue { get; set; } = [];    // extra value of one spend unit of a gauge

    // search settings (not tuned)
    public int HorizonGcds { get; set; } = 4;
    public float BudgetMs { get; set; } = 0.5f;
    public int MinNodes { get; set; }  // the total budget does not stop a search before this many nodes (0: time only)
    public int SliceNodes { get; set; } // >0: a frame slice ends after this many nodes instead of after the frame budget (with MinNodes: play depends on the work done only)
    public int BoundOgcdsPerSlot { get; set; } = 2; // oGCDs per GCD slot counted by the lower search's pruning bound

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static EngineWeights Load(string path) => Parse(File.ReadAllText(path));
    public static EngineWeights Parse(string json) => JsonSerializer.Deserialize<EngineWeights>(json, _json) ?? new();
    public string ToJson() => JsonSerializer.Serialize(this, _json);
    public EngineWeights Clone() => Parse(ToJson());

    // The tunable scalars as a vector, for the tuner. Order is stable; StatusValue entries follow in key order.
    public static readonly string[] ScalarNames = [nameof(OverCap), nameof(Combo), nameof(LambdaScale), nameof(TargetPull), nameof(SwitchMargin), nameof(FillerScale), nameof(BurstBias), nameof(StatusRemainder), nameof(CycleScale)];

    public float Get(string name) => name switch
    {
        nameof(OverCap) => OverCap,
        nameof(Combo) => Combo,
        nameof(LambdaScale) => LambdaScale,
        nameof(TargetPull) => TargetPull,
        nameof(SwitchMargin) => SwitchMargin,
        nameof(FillerScale) => FillerScale,
        nameof(BurstBias) => BurstBias,
        nameof(StatusRemainder) => StatusRemainder,
        nameof(CycleScale) => CycleScale,
        nameof(CooldownLambdaScale) => CooldownLambdaScale,
        nameof(UnlockScale) => UnlockScale,
        nameof(ForecastSelfBuffs) => ForecastSelfBuffs,
        _ when name.StartsWith("CooldownValue.") => CooldownValue.TryGetValue(name[14..], out var c) ? c : 0,
        _ when name.StartsWith("GaugeValue.") => GaugeValue.TryGetValue(name[11..], out var g) ? g : 0,
        _ when name.StartsWith("StatusValue.") => StatusValue.TryGetValue(name[12..], out var s) ? s : 0,
        _ => StatusValue.TryGetValue(name, out var v) ? v : throw new ArgumentException(name)
    };

    public void Set(string name, float value)
    {
        switch (name)
        {
            case nameof(OverCap): OverCap = value; break;
            case nameof(Combo): Combo = value; break;
            case nameof(LambdaScale): LambdaScale = value; break;
            case nameof(TargetPull): TargetPull = value; break;
            case nameof(SwitchMargin): SwitchMargin = value; break;
            case nameof(FillerScale): FillerScale = value; break;
            case nameof(BurstBias): BurstBias = value; break;
            case nameof(StatusRemainder): StatusRemainder = value; break;
            case nameof(CycleScale): CycleScale = value; break;
            case nameof(CooldownLambdaScale): CooldownLambdaScale = value; break;
            case nameof(UnlockScale): UnlockScale = value; break;
            case nameof(ForecastSelfBuffs): ForecastSelfBuffs = value; break;
            default:
                if (name.StartsWith("CooldownValue."))
                    CooldownValue[name[14..]] = value;
                else if (name.StartsWith("GaugeValue."))
                    GaugeValue[name[11..]] = value;
                else
                    StatusValue[name.StartsWith("StatusValue.") ? name[12..] : name] = value;
                break;
        }
    }
}
