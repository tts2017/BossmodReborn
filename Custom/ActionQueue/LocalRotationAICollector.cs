using System.IO;
using System.Text.Json;

namespace BossMod;

public sealed class LocalRotationAICollector : IDisposable
{
    private const int CaptureSchemaVersion = 2;
    private const int FeatureSchemaVersion = 2;
    private const string CollectorVersion = "2.0.0";
    private const double RewardHorizonSeconds = 30;
    private const double MinimumRewardSeconds = 15;
    private const double PendingConfirmationSeconds = 15;

    private readonly record struct CapturedStatus(uint ID, ushort Extra, int RemainingTenths, bool SourceIsPlayer);

    private sealed class RewardWindow
    {
        public required string DecisionID;
        public required DateTime StartedAt;
        public required uint SourceSequence;
        public required LocalRotationAI.CapturedCandidate[] Candidates;
        public required uint ExecutedActionRaw;
        public required uint BaselineActionRaw;
        public required ushort TerritoryID;
        public required ushort ContentID;
        public required int PartySize;
        public required uint TargetOID;
        public required uint TargetMaxHP;
        public required uint ComboActionRaw;
        public required CapturedStatus[] PlayerStatuses;
        public required CapturedStatus[] TargetStatuses;
        public double Damage;
        public double Duration;
    }

    private sealed class Fight
    {
        public required string ID;
        public required DateTime StartedAt;
        public required string Job;
        public double TotalDamage;
        public readonly List<RewardWindow> Pending = [];
        public readonly List<RewardWindow> Active = [];
        public readonly List<RewardWindow> Completed = [];
        public int NextDecision;
    }

    private readonly WorldState _ws;
    private readonly AIHints _hints;
    private readonly string? _captureDirectory;
    private Fight? _fight;
    private ActionQueue? _preparedQueue;
    private ActionQueue.Entry _preparedBaseline;
    private ActionQueue.Entry _preparedSelected;
    private float _preparedAnimationLock;
    private float _preparedInstantAnimLockDelay;
    private bool _preparedAllowDismount;
    private uint _lastSourceSequence;

    public LocalRotationAICollector(WorldState ws, AIHints hints)
    {
        _ws = ws;
        _hints = hints;
        _captureDirectory = ResolveCaptureDirectory();
        if (_captureDirectory != null)
        {
            try
            {
                Directory.CreateDirectory(_captureDirectory);
            }
            catch
            {
                _captureDirectory = null;
            }
        }
    }

    public void Prepare(ActionQueue queue, ActionQueue.Entry baseline, ActionQueue.Entry selected, Actor player, float animationLock, float instantAnimLockDelay, bool allowDismount)
    {
        if (_captureDirectory == null)
            return;
        UpdateCombat(player);
        CompleteExpiredWindows(_ws.CurrentTime);
        _preparedQueue = selected.Action ? queue : null;
        _preparedBaseline = baseline;
        _preparedSelected = selected;
        _preparedAnimationLock = animationLock;
        _preparedInstantAnimLockDelay = instantAnimLockDelay;
        _preparedAllowDismount = allowDismount;
    }

    public void ActionRequested(ClientActionRequest request)
    {
        if (_captureDirectory == null || _fight == null || _preparedQueue == null || request.SourceSequence == _lastSourceSequence)
            return;
        var player = _ws.Party.Player();
        if (player == null || !player.InCombat)
            return;

        var candidates = LocalRotationAI.CaptureCandidates(_preparedQueue, _preparedBaseline, _preparedSelected, _ws, player, _ws.Client.Cooldowns, _preparedAnimationLock, _hints, _preparedInstantAnimLockDelay, _preparedAllowDismount);
        if (candidates.Length == 0)
            return;
        var baseline = candidates.FirstOrDefault(candidate => candidate.Baseline);
        var selected = candidates.FirstOrDefault(candidate => candidate.Selected);
        if (baseline.ActionRaw == 0 || selected.ActionRaw == 0)
            return;

        _lastSourceSequence = request.SourceSequence;
        var target = _preparedBaseline.Target;
        _fight.Pending.Add(new()
        {
            DecisionID = $"{_fight.ID}:{_fight.NextDecision++}",
            StartedAt = _ws.CurrentTime,
            SourceSequence = request.SourceSequence,
            Candidates = candidates,
            ExecutedActionRaw = selected.ActionRaw,
            BaselineActionRaw = baseline.ActionRaw,
            TerritoryID = _ws.CurrentZone,
            ContentID = _ws.CurrentCFCID,
            PartySize = _ws.Party.Members.Take(PartyState.MaxPartySize).Count(member => member.IsValid()),
            TargetOID = target?.OID ?? 0,
            TargetMaxHP = target?.HPMP.MaxHP ?? 0,
            ComboActionRaw = _ws.Client.ComboState.Action,
            PlayerStatuses = CaptureStatuses(player, player.InstanceID),
            TargetStatuses = target != null ? CaptureStatuses(target, player.InstanceID) : []
        });
    }

    public void ActionEffect(uint casterID, ActorCastEvent action)
    {
        if (_captureDirectory == null || _fight == null)
            return;
        var player = _ws.Party.Player();
        var caster = _ws.Actors.Find(casterID);
        if (player == null || casterID != player.InstanceID && caster?.OwnerID != player.InstanceID)
            return;

        if (casterID == player.InstanceID && action.SourceSequence != 0)
        {
            var pendingIndex = _fight.Pending.FindIndex(window => window.SourceSequence == action.SourceSequence);
            if (pendingIndex >= 0)
            {
                var confirmed = _fight.Pending[pendingIndex];
                _fight.Pending.RemoveAt(pendingIndex);
                _fight.Active.Add(confirmed);
            }
        }

        var damage = 0L;
        foreach (var target in action.Targets)
        {
            foreach (var effect in target.Effects.ValidEffects())
            {
                if (effect.Type is ActionEffectType.Damage or ActionEffectType.BlockedDamage or ActionEffectType.ParriedDamage)
                    damage += effect.DamageHealValue;
            }
        }
        if (damage <= 0)
            return;

        _fight.TotalDamage += damage;
        foreach (var window in _fight.Active)
        {
            if (_ws.CurrentTime >= window.StartedAt && _ws.CurrentTime <= window.StartedAt.AddSeconds(RewardHorizonSeconds))
                window.Damage += damage;
        }
    }

    public void Dispose()
    {
        // on unload the file is written before returning, so the fight in progress is not lost with the plugin
        if (_fight != null)
            FinishFight(_ws.CurrentTime, writeInBackground: false);
    }

    private void UpdateCombat(Actor player)
    {
        if (player.InCombat && _fight == null)
        {
            var id = $"{_ws.CurrentTime:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
            _fight = new() { ID = id, StartedAt = _ws.CurrentTime, Job = player.Class.ToString() };
            _lastSourceSequence = 0;
        }
        else if (!player.InCombat && _fight != null)
        {
            FinishFight(_ws.CurrentTime);
        }
    }

    private void CompleteExpiredWindows(DateTime now)
    {
        if (_fight == null)
            return;
        for (var index = _fight.Pending.Count - 1; index >= 0; --index)
        {
            if (now >= _fight.Pending[index].StartedAt.AddSeconds(PendingConfirmationSeconds))
                _fight.Pending.RemoveAt(index);
        }
        for (var index = _fight.Active.Count - 1; index >= 0; --index)
        {
            var window = _fight.Active[index];
            if (now < window.StartedAt.AddSeconds(RewardHorizonSeconds))
                continue;
            window.Duration = RewardHorizonSeconds;
            _fight.Completed.Add(window);
            _fight.Active.RemoveAt(index);
        }
    }

    private void FinishFight(DateTime endedAt, bool writeInBackground = true)
    {
        var fight = _fight;
        _fight = null;
        _preparedQueue = null;
        if (fight == null)
            return;

        fight.Pending.Clear();
        foreach (var window in fight.Active)
        {
            window.Duration = Math.Min(RewardHorizonSeconds, Math.Max(0, (endedAt - window.StartedAt).TotalSeconds));
            if (window.Duration >= MinimumRewardSeconds)
                fight.Completed.Add(window);
        }
        fight.Active.Clear();
        var fightDuration = Math.Max(0, (endedAt - fight.StartedAt).TotalSeconds);
        if (_captureDirectory == null || fightDuration < MinimumRewardSeconds || fight.TotalDamage <= 0 || fight.Completed.Count == 0)
            return;

        var fightDPS = fight.TotalDamage / fightDuration;
        var lines = fight.Completed
            .Where(window => window.Duration >= MinimumRewardSeconds && window.Damage > 0)
            .Select(window => JsonSerializer.Serialize(new
            {
                schema_version = CaptureSchemaVersion,
                feature_schema_version = FeatureSchemaVersion,
                collector_version = CollectorVersion,
                auto_execution_verified = true,
                execution_confirmed = true,
                fight_id = fight.ID,
                decision_id = window.DecisionID,
                timestamp_utc = window.StartedAt,
                job = fight.Job,
                territory_id = window.TerritoryID,
                content_id = window.ContentID,
                party_size = window.PartySize,
                target_oid = window.TargetOID,
                target_max_hp = window.TargetMaxHP,
                combo_action_raw = window.ComboActionRaw,
                player_statuses = window.PlayerStatuses.Select(status => new
                {
                    id = status.ID,
                    extra = status.Extra,
                    remaining_tenths = status.RemainingTenths,
                    source_is_player = status.SourceIsPlayer
                }),
                target_statuses = window.TargetStatuses.Select(status => new
                {
                    id = status.ID,
                    extra = status.Extra,
                    remaining_tenths = status.RemainingTenths,
                    source_is_player = status.SourceIsPlayer
                }),
                fight_duration_seconds = fightDuration,
                duration_seconds = window.Duration,
                full_reward_horizon = window.Duration >= RewardHorizonSeconds,
                damage = window.Damage,
                dps = window.Damage / window.Duration,
                dps_ratio = window.Damage / window.Duration / fightDPS,
                executed_action_raw = window.ExecutedActionRaw,
                baseline_action_raw = window.BaselineActionRaw,
                candidates = window.Candidates.Select(candidate => new
                {
                    action_raw = candidate.ActionRaw,
                    baseline = candidate.Baseline,
                    selected = candidate.Selected,
                    features = candidate.Features
                })
            }))
            .ToArray();
        if (lines.Length == 0)
            return;

        // the lines are serialized here (from data this thread owns); the disk write happens at combat end, so it goes to a
        // background task instead of stalling the frame
        var capturePath = Path.Combine(_captureDirectory, $"{fight.ID}.v2.jsonl");
        if (writeInBackground)
            _ = System.Threading.Tasks.Task.Run(() => WriteCapture(capturePath, lines));
        else
            WriteCapture(capturePath, lines);
    }

    private static void WriteCapture(string capturePath, string[] lines)
    {
        var temporaryPath = capturePath + ".new";
        try
        {
            File.WriteAllLines(temporaryPath, lines);
            File.Move(temporaryPath, capturePath, true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private CapturedStatus[] CaptureStatuses(Actor actor, ulong playerInstanceID)
        => [.. actor.Statuses
            .Where(status => status.ID != 0 && status.ExpireAt > _ws.CurrentTime)
            .Select(status => new CapturedStatus(
                status.ID,
                status.Extra,
                (int)Math.Clamp(Math.Round((status.ExpireAt - _ws.CurrentTime).TotalSeconds * 10), 1, int.MaxValue),
                status.SourceID == playerInstanceID))
            .OrderBy(status => status.ID)
            .ThenBy(status => status.Extra)
            .ThenBy(status => status.RemainingTenths)
            .ThenBy(status => status.SourceIsPlayer)];

    private static string? ResolveCaptureDirectory()
    {
        var modelPath = Environment.GetEnvironmentVariable("BOSSMOD_LOCAL_AI_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath))
            return null;
        var modelDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath));
        var root = modelDirectory == null ? null : Directory.GetParent(modelDirectory)?.FullName;
        return root == null ? null : Path.Combine(root, "captures");
    }
}
