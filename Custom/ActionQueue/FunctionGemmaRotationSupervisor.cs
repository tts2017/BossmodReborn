using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace BossMod;

// Optional RPR/BLM/VPR local supervisor and offline MCH teacher endpoint. Live MCH selection uses MchRealtimeValuePlanner.
public sealed class FunctionGemmaRotationSupervisor
{
    private const float HardMaxPriorityLoss = 10f;
    private const int MaximumModelCandidates = 4;
    private const int DefaultTimeoutMilliseconds = 150;
    private const int DefaultMinimumConfidence = 95;
    private const float DefaultMchMaxPriorityLoss = 10f;
    private const int FailureRetryDelayMilliseconds = 1000;
    private static readonly Lazy<FunctionGemmaRotationSupervisor?> Instance = new(Load);

    private sealed record CandidateRequest(string Action, float PriorityDelta, bool Baseline);
    private sealed record RprStateRequest(
        byte SoulGauge,
        byte ShroudGauge,
        byte LemureShroud,
        byte VoidShroud,
        float EnshroudLeft,
        uint ComboActionRaw,
        string ComboAction,
        float ComboLeft,
        float TargetHP,
        float DeathsDesignLeft,
        float ArcaneCircleReadyIn,
        float ArcaneCircleLeft,
        float GluttonyReadyIn,
        float SoulSliceChargeCapIn,
        float IdealHostLeft,
        float PerfectioParataLeft,
        float ImmortalSacrificeLeft,
        float BloodsownCircleLeft,
        float ExecutionerLeft,
        float SoulReaverLeft,
        int EnemyCount);
    private sealed record BlmStateRequest(
        uint MP,
        sbyte Element,
        short NextPolyglotMilliseconds,
        byte UmbralHearts,
        byte PolyglotStacks,
        bool ParadoxActive,
        int AstralSoulStacks,
        float TriplecastLeft,
        int TriplecastStacks,
        float SwiftcastLeft,
        bool Firestarter,
        bool Thunderhead,
        bool InLeyLines,
        float LeyLinesLeft,
        float TargetThunderLeft,
        float ManafontReadyIn,
        float TriplecastChargeCapIn,
        float SwiftcastReadyIn,
        float AmplifierReadyIn,
        float TargetHP,
        int EnemyCount,
        bool Moving,
        float MaxCastTime);
    private sealed record MchStateRequest(
        byte Heat,
        byte Battery,
        bool Overheated,
        float OverheatLeft,
        bool HasMinion,
        uint ComboActionRaw,
        string ComboAction,
        float ComboLeft,
        float TargetHP,
        float ReassembleLeft,
        float WildfireLeft,
        float TargetWildfireLeft,
        float HyperchargedLeft,
        float ExcavatorLeft,
        float FullMetalFieldLeft,
        float TargetBioblasterLeft,
        float DrillReadyIn,
        float AirAnchorReadyIn,
        float ChainSawReadyIn,
        float WildfireReadyIn,
        float BarrelStabilizerReadyIn,
        float DrillChargeCapIn,
        float ReassembleChargeCapIn,
        float GaussChargeCapIn,
        float RicochetChargeCapIn,
        int EnemyCount);
    private sealed record VprStateRequest(
        int DreadCombo,
        int Coil,
        int Offering,
        int Anguine,
        byte SerpentCombo,
        uint ComboActionRaw,
        string ComboAction,
        float ComboLeft,
        float TargetHP,
        float TargetDistance,
        float HuntersInstinctLeft,
        float SwiftscaledLeft,
        float HonedSteelLeft,
        float HonedReaversLeft,
        float FlankstungVenomLeft,
        float FlanksbaneVenomLeft,
        float HindstungVenomLeft,
        float HindsbaneVenomLeft,
        float GrimhuntersVenomLeft,
        float GrimskinsVenomLeft,
        float ReawakenReadyLeft,
        float ReawakenedLeft,
        float VicewinderReadyIn,
        float VicewinderChargeCapIn,
        float SerpentsIreReadyIn,
        int EnemyCount);
    private sealed record DecisionRequest(string RequestID, string Job, string Baseline, object State, CandidateRequest[] Candidates);
    private sealed record DecisionResponse(
        [property: JsonPropertyName("request_id")] string RequestID,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("confidence")] int Confidence);
    private readonly record struct Candidate(ActionQueue.Entry Entry, string Name);
    private readonly record struct CompletedDecision(ulong Key, string Action, int Confidence);

    private readonly object _sync = new();
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly int _timeoutMilliseconds;
    private readonly int _minimumConfidence;
    private readonly float _maxPriorityLoss;
    private readonly float _mchMaxPriorityLoss;
    private CancellationTokenSource? _pendingCancellation;
    private ulong _pendingKey;
    private ulong _retryKey;
    private long _retryAfterMilliseconds;
    private CompletedDecision? _completed;

    private FunctionGemmaRotationSupervisor(Uri endpoint, int timeoutMilliseconds, int minimumConfidence, float maxPriorityLoss, float mchMaxPriorityLoss)
    {
        _endpoint = endpoint;
        _timeoutMilliseconds = timeoutMilliseconds;
        _minimumConfidence = minimumConfidence;
        _maxPriorityLoss = maxPriorityLoss;
        _mchMaxPriorityLoss = mchMaxPriorityLoss;
        _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static ActionQueue.Entry Select(ActionQueue queue, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, float instantAnimLockDelay, bool allowDismount)
        => player.Class == Class.SAM ? baseline : Instance.Value?.SelectInternal(queue, baseline, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount) ?? baseline;

    private ActionQueue.Entry SelectInternal(ActionQueue queue, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, float instantAnimLockDelay, bool allowDismount)
    {
        if (!SupportedJob(player.Class) || !player.InCombat || !baseline.Action || baseline.Action.Type != ActionType.Spell)
            return baseline;
        if (player.Class == Class.MCH && !MchCandidateAllowed(baseline.Action))
            return baseline;
        if (player.Class == Class.VPR && !VprCandidateAllowed(baseline.Action))
            return baseline;

        var candidates = EligibleCandidates(queue, baseline, ws, player, cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount);
        if (candidates.Length < 2 || !candidates.Any(candidate => candidate.Entry.Action == baseline.Action))
            return baseline;

        var request = BuildRequest(candidates, baseline, ws, player, cooldowns, hints);
        var key = ParseRequestID(request.RequestID);
        lock (_sync)
        {
            if (_completed is { } completed && completed.Key == key && completed.Confidence >= _minimumConfidence)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.Name == completed.Action)
                        return candidate.Entry;
                }
            }

            var retryBlocked = _retryKey == key && Environment.TickCount64 < _retryAfterMilliseconds;
            if (_pendingKey != key && !retryBlocked)
            {
                _pendingCancellation?.Cancel();
                _pendingCancellation?.Dispose();
                var cancellation = new CancellationTokenSource();
                var cancellationToken = cancellation.Token;
                _pendingCancellation = cancellation;
                _pendingKey = key;
                _completed = null;
                _ = Task.Run(() => RequestDecision(request, key, cancellationToken));
            }
        }

        return baseline;
    }

    private Candidate[] EligibleCandidates(ActionQueue queue, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, float animationLock, AIHints hints, float instantAnimLockDelay, bool allowDismount)
    {
        var baselineDefinition = ActionDefinitions.Instance[baseline.Action];
        if (baselineDefinition == null)
            return [];

        var baselineStart = StartDelay(baseline, baselineDefinition, ws, cooldowns, animationLock);
        var baselineDuration = Duration(baselineDefinition, instantAnimLockDelay);
        var maxPriorityLoss = player.Class == Class.MCH ? _mchMaxPriorityLoss : _maxPriorityLoss;
        var unique = new Dictionary<uint, Candidate>();
        foreach (var entry in queue.Entries)
        {
            if (entry.Action.Type != ActionType.Spell || entry.Manual || entry.Force || entry.Expire != float.MaxValue)
                continue;
            if (player.Class == Class.MCH && !MchCandidateAllowed(entry.Action))
                continue;
            if (player.Class == Class.VPR && !VprCandidateAllowed(entry.Action))
                continue;
            if (entry.Priority < baseline.Priority - maxPriorityLoss || entry.Priority > baseline.Priority || !CompatibleTarget(entry.Target, baseline.Target, player.Class))
                continue;

            var definition = ActionDefinitions.Instance[entry.Action];
            if (definition == null || definition.IsGCD != baselineDefinition.IsGCD || !definition.IsUnlocked(ws, player) || entry.CastTime > hints.MaxCastTime)
                continue;
            if (StartDelay(entry, definition, ws, cooldowns, animationLock) > baselineStart + 0.01f || Duration(definition, instantAnimLockDelay) > baselineDuration + 0.01f)
                continue;
            if (!queue.CanExecuteEx(entry, definition, ws, player, hints, allowDismount))
                continue;

            var name = ActionName(entry.Action, player.Class);
            if (!unique.ContainsKey(entry.Action.Raw))
                unique.Add(entry.Action.Raw, new(entry, name));
        }

        return unique.Values.OrderByDescending(candidate => candidate.Entry.Priority).ThenBy(candidate => candidate.Entry.Action.Raw).ToArray();
    }

    private static DecisionRequest BuildRequest(Candidate[] candidates, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, AIHints hints)
        => player.Class switch
        {
            Class.RPR => BuildRprRequest(candidates, baseline, ws, player, cooldowns, hints),
            Class.MCH => BuildMchRequest(candidates, baseline, ws, player, cooldowns, hints),
            Class.VPR => BuildVprRequest(candidates, baseline, ws, player, cooldowns, hints),
            _ => BuildBlmRequest(candidates, baseline, ws, player, cooldowns, hints)
        };

    private static DecisionRequest BuildRprRequest(Candidate[] candidates, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, AIHints hints)
    {
        var gauge = ws.Client.GetGauge<ReaperGauge>();
        var target = baseline.Target;
        var baselineName = RprActionName(baseline.Action);
        var state = new RprStateRequest(
            gauge.Soul,
            gauge.Shroud,
            gauge.LemureShroud,
            gauge.VoidShroud,
            gauge.EnshroudedTimeRemaining * 0.001f,
            ws.Client.ComboState.Action,
            RprActionName(new(ActionType.Spell, ws.Client.ComboState.Action)),
            ws.Client.ComboState.Remaining,
            target != null && target.HPMP.MaxHP > 0 ? (float)target.HPMP.CurHP / target.HPMP.MaxHP : 0,
            StatusLeft(target, RPR.SID.DeathsDesign, ws, player.InstanceID),
            ReadyIn(RPR.AID.ArcaneCircle, ws, cooldowns),
            StatusLeft(player, RPR.SID.ArcaneCircle, ws),
            ReadyIn(RPR.AID.Gluttony, ws, cooldowns),
            ChargeCapIn(RPR.AID.SoulSlice, ws, player, cooldowns),
            StatusLeft(player, RPR.SID.IdealHost, ws),
            StatusLeft(player, RPR.SID.PerfectioParata, ws),
            StatusLeft(player, RPR.SID.ImmortalSacrifice, ws),
            StatusLeft(player, RPR.SID.BloodsownCircle, ws),
            StatusLeft(player, RPR.SID.Executioner, ws),
            StatusLeft(player, RPR.SID.SoulReaver, ws),
            hints.PriorityTargetsSpan.Length);
        var candidateRequests = candidates.Select(candidate => new CandidateRequest(candidate.Name, candidate.Entry.Priority - baseline.Priority, candidate.Entry.Action == baseline.Action)).ToArray();
        var key = StateKey(candidates, baseline, state, target);
        return new(key.ToString("X16"), "RPR", baselineName, state, candidateRequests);
    }

    private static DecisionRequest BuildBlmRequest(Candidate[] candidates, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, AIHints hints)
    {
        candidates = LimitCandidates(candidates, baseline);
        var gauge = ws.Client.GetGauge<BlackMageGauge>();
        var target = baseline.Target;
        var baselineName = BlmActionName(baseline.Action);
        var triplecast = player.FindStatus(BLM.SID.Triplecast);
        var state = new BlmStateRequest(
            player.HPMP.CurMP,
            gauge.ElementStance,
            gauge.EnochianTimer,
            gauge.UmbralHearts,
            gauge.PolyglotStacks,
            gauge.ParadoxActive,
            gauge.AstralSoulStacks,
            StatusLeft(triplecast, ws),
            triplecast is { } triplecastStatus ? triplecastStatus.Extra & 0xFF : 0,
            StatusLeft(player, BLM.SID.Swiftcast, ws),
            player.FindStatus(BLM.SID.Firestarter) != null,
            player.FindStatus(BLM.SID.Thunderhead) != null,
            player.FindStatus(BLM.SID.CircleOfPower) != null,
            StatusLeft(player, BLM.SID.LeyLines, ws),
            ThunderLeft(target, ws, player.InstanceID),
            ReadyIn(BLM.AID.Manafont, ws, cooldowns),
            ChargeCapIn(BLM.AID.Triplecast, ws, player, cooldowns),
            ReadyIn(BLM.AID.Swiftcast, ws, cooldowns),
            ReadyIn(BLM.AID.Amplifier, ws, cooldowns),
            target != null && target.HPMP.MaxHP > 0 ? (float)target.HPMP.CurHP / target.HPMP.MaxHP : 0,
            hints.PriorityTargetsSpan.Length,
            player.LastFrameMovement.LengthSq() > 0.0001f,
            hints.MaxCastTime);
        var candidateRequests = candidates.Select(candidate => new CandidateRequest(candidate.Name, candidate.Entry.Priority - baseline.Priority, candidate.Entry.Action == baseline.Action)).ToArray();
        var key = StateKey(candidates, baseline, state, target);
        return new(key.ToString("X16"), "BLM", baselineName, state, candidateRequests);
    }

    private static DecisionRequest BuildMchRequest(Candidate[] candidates, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, AIHints hints)
    {
        candidates = LimitCandidates(candidates, baseline);
        var gauge = ws.Client.GetGauge<MachinistGauge>();
        var target = baseline.Target;
        var baselineName = MchActionName(baseline.Action);
        var gaussAction = ActionDefinitions.Instance[ActionID.MakeSpell(MCH.AID.DoubleCheck)]?.IsUnlocked(ws, player) == true ? MCH.AID.DoubleCheck : MCH.AID.GaussRound;
        var ricochetAction = ActionDefinitions.Instance[ActionID.MakeSpell(MCH.AID.Checkmate)]?.IsUnlocked(ws, player) == true ? MCH.AID.Checkmate : MCH.AID.Ricochet;
        var state = new MchStateRequest(
            gauge.Heat,
            gauge.Battery,
            (gauge.TimerActive & 1) != 0,
            gauge.OverheatTimeRemaining * 0.001f,
            (gauge.TimerActive & 2) != 0,
            ws.Client.ComboState.Action,
            MchActionName(new(ActionType.Spell, ws.Client.ComboState.Action)),
            ws.Client.ComboState.Remaining,
            target != null && target.HPMP.MaxHP > 0 ? (float)target.HPMP.CurHP / target.HPMP.MaxHP : 0,
            StatusLeft(player, MCH.SID.Reassembled, ws),
            StatusLeft(player, MCH.SID.WildfirePlayer, ws),
            StatusLeft(target, MCH.SID.WildfireTarget, ws, player.InstanceID),
            StatusLeft(player, MCH.SID.Hypercharged, ws),
            StatusLeft(player, MCH.SID.ExcavatorReady, ws),
            StatusLeft(player, MCH.SID.FullMetalMachinist, ws),
            StatusLeft(target, MCH.SID.Bioblaster, ws, player.InstanceID),
            ReadyIn(MCH.AID.Drill, ws, cooldowns),
            ReadyIn(MCH.AID.AirAnchor, ws, cooldowns),
            ReadyIn(MCH.AID.ChainSaw, ws, cooldowns),
            ReadyIn(MCH.AID.Wildfire, ws, cooldowns),
            ReadyIn(MCH.AID.BarrelStabilizer, ws, cooldowns),
            ChargeCapIn(MCH.AID.Drill, ws, player, cooldowns),
            ChargeCapIn(MCH.AID.Reassemble, ws, player, cooldowns),
            ChargeCapIn(gaussAction, ws, player, cooldowns),
            ChargeCapIn(ricochetAction, ws, player, cooldowns),
            hints.PriorityTargetsSpan.Length);
        var candidateRequests = candidates.Select(candidate => new CandidateRequest(candidate.Name, candidate.Entry.Priority - baseline.Priority, candidate.Entry.Action == baseline.Action)).ToArray();
        var key = StateKey(candidates, baseline, state, target);
        return new(key.ToString("X16"), "MCH", baselineName, state, candidateRequests);
    }

    private static DecisionRequest BuildVprRequest(Candidate[] candidates, ActionQueue.Entry baseline, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns, AIHints hints)
    {
        candidates = LimitCandidates(candidates, baseline);
        var gauge = ws.Client.GetGauge<ViperGauge>();
        var target = baseline.Target;
        var baselineName = VprActionName(baseline.Action);
        var targetDistance = target != null
            ? Math.Max(0, MathF.Sqrt((target.Position - player.Position).LengthSq()) - player.HitboxRadius - target.HitboxRadius)
            : 0;
        var state = new VprStateRequest(
            (int)gauge.DreadCombo,
            gauge.RattlingCoilStacks,
            gauge.SerpentOffering,
            gauge.AnguineTribute,
            (byte)gauge.SerpentCombo,
            ws.Client.ComboState.Action,
            VprActionName(new(ActionType.Spell, ws.Client.ComboState.Action)),
            ws.Client.ComboState.Remaining,
            target != null && target.HPMP.MaxHP > 0 ? (float)target.HPMP.CurHP / target.HPMP.MaxHP : 0,
            targetDistance,
            StatusLeft(player, VPR.SID.HuntersInstinct, ws),
            StatusLeft(player, VPR.SID.Swiftscaled, ws),
            StatusLeft(player, VPR.SID.HonedSteel, ws),
            StatusLeft(player, VPR.SID.HonedReavers, ws),
            StatusLeft(player, VPR.SID.FlankstungVenom, ws),
            StatusLeft(player, VPR.SID.FlanksbaneVenom, ws),
            StatusLeft(player, VPR.SID.HindstungVenom, ws),
            StatusLeft(player, VPR.SID.HindsbaneVenom, ws),
            StatusLeft(player, VPR.SID.GrimhuntersVenom, ws),
            StatusLeft(player, VPR.SID.GrimskinsVenom, ws),
            StatusLeft(player, VPR.SID.ReawakenReady, ws),
            StatusLeft(player, VPR.SID.Reawakened, ws),
            ReadyIn(VPR.AID.Vicewinder, ws, cooldowns),
            ChargeCapIn(VPR.AID.Vicewinder, ws, player, cooldowns),
            ReadyIn(VPR.AID.SerpentsIre, ws, cooldowns),
            hints.PriorityTargetsSpan.Length);
        var candidateRequests = candidates.Select(candidate => new CandidateRequest(candidate.Name, candidate.Entry.Priority - baseline.Priority, candidate.Entry.Action == baseline.Action)).ToArray();
        var key = StateKey(candidates, baseline, state, target);
        return new(key.ToString("X16"), "VPR", baselineName, state, candidateRequests);
    }

    private static Candidate[] LimitCandidates(Candidate[] candidates, ActionQueue.Entry baseline)
    {
        if (candidates.Length <= MaximumModelCandidates)
            return candidates;

        var limited = new List<Candidate>(MaximumModelCandidates);
        var baselineCandidate = candidates.First(candidate => candidate.Entry.Action == baseline.Action);
        limited.Add(baselineCandidate);
        foreach (var candidate in candidates)
        {
            if (candidate.Entry.Action == baseline.Action)
                continue;
            limited.Add(candidate);
            if (limited.Count == MaximumModelCandidates)
                break;
        }
        return [.. limited];
    }

    private async Task RequestDecision(DecisionRequest request, ulong key, CancellationToken cancellationToken)
    {
        var completed = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeoutMilliseconds);
            using var response = await _http.PostAsJsonAsync(_endpoint, request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;
            var decision = await response.Content.ReadFromJsonAsync<DecisionResponse>(cancellationToken: timeout.Token).ConfigureAwait(false);
            if (decision == null || decision.RequestID != request.RequestID || decision.Confidence is < 0 or > 100)
                return;

            lock (_sync)
            {
                if (_pendingKey == key && !cancellationToken.IsCancellationRequested)
                {
                    _completed = new(key, decision.Action, decision.Confidence);
                    _retryKey = 0;
                    completed = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (JsonException)
        {
        }
        catch (Exception)
        {
            // anything else (a non-JSON content type, a disposed client during shutdown) is a failed request like the ones above;
            // this runs fire-and-forget, so an escaping exception would only surface as an unobserved task exception
        }
        finally
        {
            if (!completed)
            {
                lock (_sync)
                {
                    if (_pendingKey == key && !cancellationToken.IsCancellationRequested)
                    {
                        _pendingKey = 0;
                        _retryKey = key;
                        _retryAfterMilliseconds = Environment.TickCount64 + FailureRetryDelayMilliseconds;
                    }
                }
            }
        }
    }

    private static FunctionGemmaRotationSupervisor? Load()
    {
        var endpointText = EnvironmentValue("BOSSMOD_FUNCTIONGEMMA_ENDPOINT");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp || !IsLoopback(endpoint.Host))
            return null;

        // Load runs on the game thread (the first Select in combat); probing a closed loopback port can block for about two
        // seconds on Windows, so the probe and the optional autostart run in the background
        _ = Task.Run(() => StartLocalServer(endpoint));
        var timeout = ParseIntEnvironment("BOSSMOD_FUNCTIONGEMMA_TIMEOUT_MS", DefaultTimeoutMilliseconds, 50, 500);
        var confidence = ParseIntEnvironment("BOSSMOD_FUNCTIONGEMMA_MIN_CONFIDENCE", DefaultMinimumConfidence, 50, 100);
        var priorityLoss = ParseFloatEnvironment("BOSSMOD_FUNCTIONGEMMA_MAX_PRIORITY_LOSS", 0, 0, HardMaxPriorityLoss);
        var mchPriorityLoss = ParseFloatEnvironment("BOSSMOD_FUNCTIONGEMMA_MCH_MAX_PRIORITY_LOSS", DefaultMchMaxPriorityLoss, 0, HardMaxPriorityLoss);
        return new(endpoint, timeout, confidence, priorityLoss, mchPriorityLoss);
    }

    // Plugin unload: stop the pending request and release the HTTP client (the instance is process-wide and otherwise outlives it).
    public static void Shutdown()
    {
        if (!Instance.IsValueCreated || Instance.Value is not { } supervisor)
            return;
        lock (supervisor._sync)
        {
            supervisor._pendingCancellation?.Cancel();
            supervisor._pendingCancellation?.Dispose();
            supervisor._pendingCancellation = null;
            supervisor._pendingKey = 0;
        }
        supervisor._http.Dispose();
    }

    private static bool IsLoopback(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static void StartLocalServer(Uri endpoint)
    {
        if (EnvironmentValue("BOSSMOD_FUNCTIONGEMMA_AUTOSTART") != "1" || LocalServerAvailable(endpoint))
            return;

        var script = EnvironmentValue("BOSSMOD_FUNCTIONGEMMA_START_SCRIPT");
        if (script == null || script.Length == 0 || !File.Exists(script))
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("-Port");
            startInfo.ArgumentList.Add(endpoint.Port.ToString());
            Process.Start(startInfo);
        }
        catch
        {
        }
    }

    private static bool LocalServerAvailable(Uri endpoint)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(endpoint.Host, endpoint.Port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string? EnvironmentValue(string name)
        => Environment.GetEnvironmentVariable(name);

    private static int ParseIntEnvironment(string name, int fallback, int minimum, int maximum)
        => int.TryParse(EnvironmentValue(name), out var value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static float ParseFloatEnvironment(string name, float fallback, float minimum, float maximum)
        => float.TryParse(EnvironmentValue(name), out var value) && float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static float ReadyIn(RPR.AID action, WorldState ws, ReadOnlySpan<Cooldown> cooldowns)
        => ActionDefinitions.Instance[ActionID.MakeSpell(action)]?.ReadyIn(cooldowns, ws.Client.DutyActions) ?? float.MaxValue;

    private static float ReadyIn(BLM.AID action, WorldState ws, ReadOnlySpan<Cooldown> cooldowns)
        => ActionDefinitions.Instance[ActionID.MakeSpell(action)]?.ReadyIn(cooldowns, ws.Client.DutyActions) ?? float.MaxValue;

    private static float ReadyIn(MCH.AID action, WorldState ws, ReadOnlySpan<Cooldown> cooldowns)
        => ActionDefinitions.Instance[ActionID.MakeSpell(action)]?.ReadyIn(cooldowns, ws.Client.DutyActions) ?? float.MaxValue;

    private static float ReadyIn(VPR.AID action, WorldState ws, ReadOnlySpan<Cooldown> cooldowns)
        => ActionDefinitions.Instance[ActionID.MakeSpell(action)]?.ReadyIn(cooldowns, ws.Client.DutyActions) ?? float.MaxValue;

    private static float ChargeCapIn(RPR.AID action, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns)
        => ActionDefinitions.Instance[ActionID.MakeSpell(action)]?.ChargeCapIn(cooldowns, ws.Client.DutyActions, player.Level) ?? float.MaxValue;

    private static float ChargeCapIn(BLM.AID action, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns)
        => ActionDefinitions.Instance[ActionID.MakeSpell(action)]?.ChargeCapIn(cooldowns, ws.Client.DutyActions, player.Level) ?? float.MaxValue;

    private static float ChargeCapIn(MCH.AID action, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns)
        => ActionDefinitions.Instance[ActionID.MakeSpell(action)]?.ChargeCapIn(cooldowns, ws.Client.DutyActions, player.Level) ?? float.MaxValue;

    private static float ChargeCapIn(VPR.AID action, WorldState ws, Actor player, ReadOnlySpan<Cooldown> cooldowns)
        => ActionDefinitions.Instance[ActionID.MakeSpell(action)]?.ChargeCapIn(cooldowns, ws.Client.DutyActions, player.Level) ?? float.MaxValue;

    private static float StatusLeft(Actor? actor, RPR.SID status, WorldState ws, ulong sourceID = 0)
    {
        var details = sourceID != 0 ? actor?.FindStatus((uint)status, sourceID) : actor?.FindStatus(status);
        return details is { } found ? Math.Max(0, (float)(found.ExpireAt - ws.CurrentTime).TotalSeconds) : 0;
    }

    private static float StatusLeft(Actor? actor, BLM.SID status, WorldState ws, ulong sourceID = 0)
    {
        var details = sourceID != 0 ? actor?.FindStatus((uint)status, sourceID) : actor?.FindStatus(status);
        return StatusLeft(details, ws);
    }

    private static float StatusLeft(Actor? actor, MCH.SID status, WorldState ws, ulong sourceID = 0)
    {
        var details = sourceID != 0 ? actor?.FindStatus((uint)status, sourceID) : actor?.FindStatus(status);
        return StatusLeft(details, ws);
    }

    private static float StatusLeft(Actor? actor, VPR.SID status, WorldState ws, ulong sourceID = 0)
    {
        var details = sourceID != 0 ? actor?.FindStatus((uint)status, sourceID) : actor?.FindStatus(status);
        return StatusLeft(details, ws);
    }

    private static float StatusLeft(ActorStatus? status, WorldState ws)
        => status is { } found ? Math.Max(0, (float)(found.ExpireAt - ws.CurrentTime).TotalSeconds) : 0;

    private static float ThunderLeft(Actor? target, WorldState ws, ulong sourceID)
        => Math.Max(
            Math.Max(StatusLeft(target, BLM.SID.Thunder, ws, sourceID), StatusLeft(target, BLM.SID.ThunderII, ws, sourceID)),
            Math.Max(
                Math.Max(StatusLeft(target, BLM.SID.ThunderIII, ws, sourceID), StatusLeft(target, BLM.SID.ThunderIV, ws, sourceID)),
                Math.Max(StatusLeft(target, BLM.SID.HighThunder, ws, sourceID), StatusLeft(target, BLM.SID.HighThunderII, ws, sourceID))));

    private static string RprActionName(ActionID action)
        => action.Type == ActionType.Spell && Enum.IsDefined((RPR.AID)action.ID) ? ((RPR.AID)action.ID).ToString() : $"Action{action.ID}";

    private static string BlmActionName(ActionID action)
        => action.Type == ActionType.Spell && Enum.IsDefined((BLM.AID)action.ID) ? ((BLM.AID)action.ID).ToString() : $"Action{action.ID}";

    private static string MchActionName(ActionID action)
        => action.Type == ActionType.Spell && Enum.IsDefined((MCH.AID)action.ID) ? ((MCH.AID)action.ID).ToString() : $"Action{action.ID}";

    private static string VprActionName(ActionID action)
        => action.Type == ActionType.Spell && Enum.IsDefined((VPR.AID)action.ID) ? ((VPR.AID)action.ID).ToString() : $"Action{action.ID}";

    private static string ActionName(ActionID action, Class playerClass)
        => playerClass switch
        {
            Class.RPR => RprActionName(action),
            Class.MCH => MchActionName(action),
            Class.VPR => VprActionName(action),
            _ => BlmActionName(action)
        };

    private static bool SupportedJob(Class playerClass)
        => playerClass is Class.RPR or Class.BLM or Class.THM or Class.MCH or Class.VPR;

    private static bool MchCandidateAllowed(ActionID action)
        => action.Type == ActionType.Spell && (MCH.AID)action.ID is
            MCH.AID.SplitShot or MCH.AID.HeatedSplitShot
            or MCH.AID.SlugShot or MCH.AID.HeatedSlugShot
            or MCH.AID.CleanShot or MCH.AID.HeatedCleanShot
            or MCH.AID.SpreadShot or MCH.AID.Scattergun
            or MCH.AID.HotShot or MCH.AID.AirAnchor
            or MCH.AID.Drill or MCH.AID.Bioblaster or MCH.AID.ChainSaw
            or MCH.AID.HeatBlast or MCH.AID.BlazingShot or MCH.AID.AutoCrossbow
            or MCH.AID.GaussRound or MCH.AID.DoubleCheck
            or MCH.AID.Ricochet or MCH.AID.Checkmate;

    private static bool VprCandidateAllowed(ActionID action)
        => action.Type == ActionType.Spell && (VPR.AID)action.ID is
            VPR.AID.SteelFangs or VPR.AID.ReavingFangs
            or VPR.AID.HuntersSting or VPR.AID.SwiftskinsSting
            or VPR.AID.FlankstingStrike or VPR.AID.FlanksbaneFang
            or VPR.AID.HindstingStrike or VPR.AID.HindsbaneFang
            or VPR.AID.SteelMaw or VPR.AID.ReavingMaw
            or VPR.AID.HuntersBite or VPR.AID.SwiftskinsBite
            or VPR.AID.JaggedMaw or VPR.AID.BloodiedMaw;

    private static bool CompatibleTarget(Actor? candidate, Actor? baseline, Class playerClass)
        => candidate == baseline
            || playerClass == Class.MCH && candidate != null && baseline != null && !candidate.IsAlly && !baseline.IsAlly;

    private static float StartDelay(ActionQueue.Entry entry, ActionDefinition definition, WorldState ws, ReadOnlySpan<Cooldown> cooldowns, float animationLock)
        => Math.Max(Math.Max(entry.Delay, animationLock), definition.ReadyIn(cooldowns, ws.Client.DutyActions));

    private static float Duration(ActionDefinition definition, float instantAnimLockDelay)
        => definition.CastTime > 0 ? definition.CastTime + definition.CastAnimLock : definition.InstantAnimLock + instantAnimLockDelay;

    private static ulong StateKey(Candidate[] candidates, ActionQueue.Entry baseline, RprStateRequest state, Actor? target)
    {
        var hash = 14695981039346656037UL;
        Add(ref hash, baseline.Action.Raw);
        Add(ref hash, target?.InstanceID ?? 0);
        Add(ref hash, state.SoulGauge);
        Add(ref hash, state.ShroudGauge);
        Add(ref hash, state.LemureShroud);
        Add(ref hash, state.VoidShroud);
        Add(ref hash, Quantize(state.EnshroudLeft));
        Add(ref hash, state.ComboActionRaw);
        Add(ref hash, Quantize(state.ComboLeft));
        Add(ref hash, (uint)Math.Clamp(MathF.Round(state.TargetHP * 100), 0, 100));
        Add(ref hash, Quantize(state.DeathsDesignLeft));
        Add(ref hash, Quantize(state.ArcaneCircleReadyIn));
        Add(ref hash, Quantize(state.ArcaneCircleLeft));
        Add(ref hash, Quantize(state.GluttonyReadyIn));
        Add(ref hash, Quantize(state.SoulSliceChargeCapIn));
        Add(ref hash, Quantize(state.IdealHostLeft));
        Add(ref hash, Quantize(state.PerfectioParataLeft));
        Add(ref hash, Quantize(state.ImmortalSacrificeLeft));
        Add(ref hash, Quantize(state.BloodsownCircleLeft));
        Add(ref hash, Quantize(state.ExecutionerLeft));
        Add(ref hash, Quantize(state.SoulReaverLeft));
        Add(ref hash, (uint)state.EnemyCount);
        foreach (var candidate in candidates)
        {
            Add(ref hash, candidate.Entry.Action.Raw);
            Add(ref hash, BitConverter.SingleToUInt32Bits(candidate.Entry.Priority - baseline.Priority));
        }
        return hash;
    }

    private static ulong StateKey(Candidate[] candidates, ActionQueue.Entry baseline, BlmStateRequest state, Actor? target)
    {
        var hash = 14695981039346656037UL;
        Add(ref hash, (uint)Class.BLM);
        Add(ref hash, baseline.Action.Raw);
        Add(ref hash, target?.InstanceID ?? 0);
        Add(ref hash, state.MP);
        Add(ref hash, unchecked((byte)state.Element));
        Add(ref hash, Quantize(state.NextPolyglotMilliseconds * 0.001f));
        Add(ref hash, state.UmbralHearts);
        Add(ref hash, state.PolyglotStacks);
        Add(ref hash, state.ParadoxActive ? 1u : 0u);
        Add(ref hash, (uint)state.AstralSoulStacks);
        Add(ref hash, Quantize(state.TriplecastLeft));
        Add(ref hash, (uint)state.TriplecastStacks);
        Add(ref hash, Quantize(state.SwiftcastLeft));
        Add(ref hash, state.Firestarter ? 1u : 0u);
        Add(ref hash, state.Thunderhead ? 1u : 0u);
        Add(ref hash, state.InLeyLines ? 1u : 0u);
        Add(ref hash, Quantize(state.LeyLinesLeft));
        Add(ref hash, Quantize(state.TargetThunderLeft));
        Add(ref hash, Quantize(state.ManafontReadyIn));
        Add(ref hash, Quantize(state.TriplecastChargeCapIn));
        Add(ref hash, Quantize(state.SwiftcastReadyIn));
        Add(ref hash, Quantize(state.AmplifierReadyIn));
        Add(ref hash, (uint)Math.Clamp(MathF.Round(state.TargetHP * 100), 0, 100));
        Add(ref hash, (uint)state.EnemyCount);
        Add(ref hash, state.Moving ? 1u : 0u);
        Add(ref hash, Quantize(state.MaxCastTime));
        foreach (var candidate in candidates)
        {
            Add(ref hash, candidate.Entry.Action.Raw);
            Add(ref hash, BitConverter.SingleToUInt32Bits(candidate.Entry.Priority - baseline.Priority));
        }
        return hash;
    }

    private static ulong StateKey(Candidate[] candidates, ActionQueue.Entry baseline, MchStateRequest state, Actor? target)
    {
        var hash = 14695981039346656037UL;
        Add(ref hash, (uint)Class.MCH);
        Add(ref hash, baseline.Action.Raw);
        Add(ref hash, target?.InstanceID ?? 0);
        Add(ref hash, state.Heat);
        Add(ref hash, state.Battery);
        Add(ref hash, state.Overheated ? 1u : 0u);
        Add(ref hash, Quantize(state.OverheatLeft));
        Add(ref hash, state.HasMinion ? 1u : 0u);
        Add(ref hash, state.ComboActionRaw);
        Add(ref hash, Quantize(state.ComboLeft));
        Add(ref hash, (uint)Math.Clamp(MathF.Round(state.TargetHP * 100), 0, 100));
        Add(ref hash, Quantize(state.ReassembleLeft));
        Add(ref hash, Quantize(state.WildfireLeft));
        Add(ref hash, Quantize(state.TargetWildfireLeft));
        Add(ref hash, Quantize(state.HyperchargedLeft));
        Add(ref hash, Quantize(state.ExcavatorLeft));
        Add(ref hash, Quantize(state.FullMetalFieldLeft));
        Add(ref hash, Quantize(state.TargetBioblasterLeft));
        Add(ref hash, Quantize(state.DrillReadyIn));
        Add(ref hash, Quantize(state.AirAnchorReadyIn));
        Add(ref hash, Quantize(state.ChainSawReadyIn));
        Add(ref hash, Quantize(state.WildfireReadyIn));
        Add(ref hash, Quantize(state.BarrelStabilizerReadyIn));
        Add(ref hash, Quantize(state.DrillChargeCapIn));
        Add(ref hash, Quantize(state.ReassembleChargeCapIn));
        Add(ref hash, Quantize(state.GaussChargeCapIn));
        Add(ref hash, Quantize(state.RicochetChargeCapIn));
        Add(ref hash, (uint)state.EnemyCount);
        foreach (var candidate in candidates)
        {
            Add(ref hash, candidate.Entry.Action.Raw);
            Add(ref hash, BitConverter.SingleToUInt32Bits(candidate.Entry.Priority - baseline.Priority));
        }
        return hash;
    }

    private static ulong StateKey(Candidate[] candidates, ActionQueue.Entry baseline, VprStateRequest state, Actor? target)
    {
        var hash = 14695981039346656037UL;
        Add(ref hash, (uint)Class.VPR);
        Add(ref hash, baseline.Action.Raw);
        Add(ref hash, target?.InstanceID ?? 0);
        Add(ref hash, (uint)state.DreadCombo);
        Add(ref hash, (uint)state.Coil);
        Add(ref hash, (uint)state.Offering);
        Add(ref hash, (uint)state.Anguine);
        Add(ref hash, state.SerpentCombo);
        Add(ref hash, state.ComboActionRaw);
        Add(ref hash, Quantize(state.ComboLeft));
        Add(ref hash, (uint)Math.Clamp(MathF.Round(state.TargetHP * 100), 0, 100));
        Add(ref hash, Quantize(state.TargetDistance));
        Add(ref hash, Quantize(state.HuntersInstinctLeft));
        Add(ref hash, Quantize(state.SwiftscaledLeft));
        Add(ref hash, Quantize(state.HonedSteelLeft));
        Add(ref hash, Quantize(state.HonedReaversLeft));
        Add(ref hash, Quantize(state.FlankstungVenomLeft));
        Add(ref hash, Quantize(state.FlanksbaneVenomLeft));
        Add(ref hash, Quantize(state.HindstungVenomLeft));
        Add(ref hash, Quantize(state.HindsbaneVenomLeft));
        Add(ref hash, Quantize(state.GrimhuntersVenomLeft));
        Add(ref hash, Quantize(state.GrimskinsVenomLeft));
        Add(ref hash, Quantize(state.ReawakenReadyLeft));
        Add(ref hash, Quantize(state.ReawakenedLeft));
        Add(ref hash, Quantize(state.VicewinderReadyIn));
        Add(ref hash, Quantize(state.VicewinderChargeCapIn));
        Add(ref hash, Quantize(state.SerpentsIreReadyIn));
        Add(ref hash, (uint)state.EnemyCount);
        foreach (var candidate in candidates)
        {
            Add(ref hash, candidate.Entry.Action.Raw);
            Add(ref hash, BitConverter.SingleToUInt32Bits(candidate.Entry.Priority - baseline.Priority));
        }
        return hash;
    }

    private static uint Quantize(float value)
        => !float.IsFinite(value) ? uint.MaxValue : (uint)Math.Clamp(MathF.Round(Math.Max(0, value) * 2), 0, uint.MaxValue - 1);

    private static void Add(ref ulong hash, ulong value)
    {
        for (var byteIndex = 0; byteIndex < sizeof(ulong); ++byteIndex)
        {
            hash ^= (byte)value;
            hash *= 1099511628211UL;
            value >>= 8;
        }
    }

    private static ulong ParseRequestID(string requestID)
        => ulong.TryParse(requestID, System.Globalization.NumberStyles.HexNumber, null, out var value) ? value : 0;
}
