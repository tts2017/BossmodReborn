using BossMod.Autorotation;
using Dalamud.Plugin;

namespace BossMod;

// Owns everything the local fork adds on top of upstream and receives the per-frame calls from the hook lines in
// Plugin.cs, AIHintsBuilder.cs and ActionManagerEx.cs. Created once by Plugin after the core services exist.
public sealed class CustomPlugin : IDisposable
{
    public static CustomPlugin? Instance { get; private set; }

    private static readonly CustomConfig _config = Service.Config.Get<CustomConfig>();

    private readonly IDalamudPluginInterface _dalamud;
    private readonly WorldState _ws;
    private readonly AIHints _hints;
    private readonly BossModuleManager _bossmod;
    private readonly RotationModuleManager _rotation;
    public ActionManagerEx ActionManager { get; }
    private readonly EventSubscriptions _subscriptions;
    private readonly SplatoonSafeImport _splatoonSafeImport;
    private readonly SplatoonLiveZones _splatoonLiveZones;
    private readonly ExternalTimelineHints _externalTimelineHints;
    private readonly AutoTimelineExtractor _autoTimelines;
    private readonly LocalRotationAICollector _localRotationAICollector;
    private readonly GazeActionBlock _gazeBlock;
    private readonly List<Action> _ipcUnregister = [];

    // fight-time estimate (formerly inside AIHintsBuilder)
    private readonly FightTimeEstimator _fightTime = new();
    private readonly FightPriorStore _priorStore = new(() => TimelineStore.UserDirectory);
    private readonly FightTargetSample[] _fightFeed = new FightTargetSample[AIHints.NumEnemies];
    private BossModule? _priorModule;
    private ushort _priorZone;
    private int _priorGeneration = -1;

    // written every frame on the framework thread, read by the automatic-timeline worker before each job: no replay parse starts mid-fight
    private volatile bool _playerInCombat;
    private ActionQueue.Entry _lastSelected;

    public CustomPlugin(IDalamudPluginInterface dalamud, WorldState ws, AIHints hints, BossModuleManager bossmod, RotationModuleManager rotation, ActionManagerEx amex)
    {
        _dalamud = dalamud;
        _ws = ws;
        _hints = hints;
        _bossmod = bossmod;
        _rotation = rotation;
        ActionManager = amex;

        _splatoonSafeImport = new(dalamud.ConfigDirectory.FullName + "/SplatoonImports");
        ApplyTimelineDirectory(background: false); // the first load stays synchronous, so no consumer ever finds the store empty
        _externalTimelineHints = new(ws);
        _splatoonLiveZones = new(dalamud);
        _autoTimelines = new(() => TimelineStore.UserDirectory, ReplayDirectory, shouldPause: () => _playerInCombat);
        AutoTimelineExtractor.Instance = _autoTimelines;
        _localRotationAICollector = new(ws, hints);
        _gazeBlock = new(ws, hints);

        _subscriptions = new
        (
            _config.Modified.Subscribe(() => ApplyTimelineDirectory(background: true)),
            ws.CurrentZoneChanged.Subscribe(_ => _priorStore.RefreshIfStale()),
            amex.ActionEffectReceived.Subscribe((caster, info) => _localRotationAICollector.ActionEffect((uint)caster, info)),
            amex.ActionRequestExecuted.Subscribe(OnActionRequestExecuted)
        );
        _priorStore.RefreshIfStale();
        RegisterIpc();
        Autorotation.akechi.Custom.AkechiGNBPlanner74IpcBridge.Register(dalamud);
        Instance = this;
    }

    public void Dispose()
    {
        if (Instance == this)
            Instance = null;
        foreach (var unregister in _ipcUnregister)
            unregister();
        Autorotation.akechi.Custom.AkechiGNBPlanner74IpcBridge.Unregister();
        _subscriptions.Dispose();
        AutoTimelineExtractor.Instance = null;
        _autoTimelines.Dispose();
        DisengageForecaster.Reset();
        _externalTimelineHints.Dispose();
        _splatoonLiveZones.Dispose();
        _localRotationAICollector.Dispose();
        FunctionGemmaRotationSupervisor.Shutdown();
    }

    // Holding the movement escape hatch says "I am about to move", which the rotation needs to know as much as actually moving:
    // prefer an instant, do not start a hardcast. Only meaningful while movement is blocked during casts.
    public static bool MoveKeyHeld => ActionManagerEx.Config.PreventMovingWhileCasting && (MovementOverride.Instance?.IsForceUnblocked() ?? false);

    // called by AIHintsBuilder before hints.Normalize()
    public void HintsGathered(AIHints hints, WorldState ws)
    {
        var player = ws.Party.Player();
        _playerInCombat = player?.InCombat ?? false;
        _splatoonSafeImport.Update(ws);
        _splatoonLiveZones.Update(ws);
        _externalTimelineHints.Update(_bossmod.ActiveModule);
        if (player != null)
        {
            ExternalAOEProvider.ApplyToHints(hints, ws.CurrentZone, ws.CurrentTime);
            ExternalMechanicHintProvider.ApplyToHints(hints, ws.CurrentZone, ws.CurrentCFCID, ws.CurrentTime);
        }
    }

    // called by Plugin right after AIHintsBuilder.Update (hints are normalized)
    public void AfterHintsBuilt()
    {
        var player = _ws.Party.Player();
        _hints.ResetCustomData();
        UpdateFightTime(_hints, player);
        if (_config.PredictDisengage)
            DisengageForecaster.Apply(_ws, _hints, player, capCastTime: _rotation.Preset != null);
        else
            DisengageForecaster.Reset();
    }

    // called by ActionManagerEx right after the queue picked its best action
    public ActionQueue.Entry SelectAutoQueue(ActionQueue.Entry baseline, WorldState ws, Actor player, AIHints hints, float animationLock, float instantAnimLockDelay, bool allowDismount)
    {
        var queue = hints.ActionsToExecute;
        var selected = _config.EnableLocalRotationAI
            ? FFXIVInProcessRotationAI.Select(queue, baseline, ws, player, ws.Client.Cooldowns, animationLock, hints, instantAnimLockDelay, allowDismount)
            : baseline;
        _localRotationAICollector.Prepare(queue, baseline, selected, player, animationLock, instantAnimLockDelay, allowDismount);
        _lastSelected = selected;
        if (_gazeBlock.ShouldBlock(ActionManager, selected, player))
            return default; // executing it would require looking into an imminent gaze
        return selected;
    }

    // The old fork only recorded requests made while executing the auto queue; without a hook inside ExecuteAction this compares
    // against the action the queue selected this frame (an action adjusted by the game, e.g. a combo replacement, is not matched).
    private void OnActionRequestExecuted(ClientActionRequest request)
    {
        if (_lastSelected.Action && request.Action == _lastSelected.Action)
            _localRotationAICollector.ActionRequested(request);
    }

    private string? ReplayDirectory()
    {
        var folder = Service.Config.Get<ReplayManagementConfig>().ReplayFolder;
        return string.IsNullOrEmpty(folder) ? _dalamud.ConfigDirectory.FullName + "/replays" : folder;
    }

    // Called again on every CustomConfig change (for the folder field that means every keystroke) on the framework thread.
    private void ApplyTimelineDirectory(bool background)
    {
        var configured = _config.TimelineUserDirectory;
        var directory = string.IsNullOrWhiteSpace(configured) ? System.IO.Path.Combine(_dalamud.ConfigDirectory.FullName, "timelines") : configured;
        if (TimelineStore.UserDirectory == directory)
            return;
        TimelineStore.UserDirectory = directory;
        if (background)
            TimelineStore.ReloadInBackground();
        else
            TimelineStore.Reload();
    }

    // Feeds the fight-time estimator with the priority targets once per frame and publishes the result. Allocation-free.
    private void UpdateFightTime(AIHints hints, Actor? player)
    {
        var priority = hints.PriorityTargetsSpan;
        var n = 0;
        for (var i = 0; i < priority.Length && n < _fightFeed.Length; ++i)
        {
            var actor = priority[i].Actor;
            var hp = actor.HPMP;
            if (hp.MaxHP > 0 && hp.CurHP > 0)
                _fightFeed[n++] = new(actor.InstanceID, actor.OID, hp.CurHP, hp.MaxHP);
        }
        UpdateFightPrior();
        _fightTime.Update(_ws.CurrentTime.Ticks / (double)TimeSpan.TicksPerSecond, player?.InCombat ?? false, _fightFeed.AsSpan(0, n));
        var estimate = _fightTime.Estimate;
        // a downtime announced by the timeline inside the remaining time (the disengage forecast adds its own, see DisengageForecaster.Apply)
        if (estimate.Known && ExternalMechanicHintProvider.TryGetSnapshot(_ws.CurrentZone, _ws.CurrentCFCID, _ws.CurrentTime, out var snapshot))
            estimate = estimate.WithDowntime(snapshot.TargetLossIn, snapshot.TargetReturnIn);
        hints.FightRemaining = estimate;
    }

    // Hands the estimator the earlier kills of the running boss module (zone + module OID), only when the module, the zone or the prior table changed.
    private void UpdateFightPrior()
    {
        var module = _bossmod.ActiveModule?.StateMachine.ActivePhase != null ? _bossmod.ActiveModule : null;
        if (ReferenceEquals(module, _priorModule) && _ws.CurrentZone == _priorZone && _priorStore.Generation == _priorGeneration)
            return;
        _priorModule = module;
        _priorZone = _ws.CurrentZone;
        _priorGeneration = _priorStore.Generation;
        _fightTime.Prior = module != null ? _priorStore.Find(_ws.CurrentZone, module.PrimaryActor.OID) : null;
    }

    private void RegisterIpc()
    {
        Register("ExternalAOE.PushJson", (string payload) => ExternalAOEProvider.PushJson(payload, _ws.CurrentZone, _ws.CurrentTime));
        Register("ExternalAOE.ClearNamespace", (string ns) => ExternalAOEProvider.ClearNamespace(ns));
        Register("ExternalAOE.ClearAll", ExternalAOEProvider.ClearAll);
        Register("ExternalEncounterHint.PushJson", (string payload) => ExternalEncounterHintProvider.PushJson(payload, _ws.CurrentZone, _ws.CurrentCFCID, _ws.CurrentTime));
        Register("ExternalEncounterHint.ClearNamespace", (string ns) => ExternalEncounterHintProvider.ClearNamespace(ns));
        Register("ExternalEncounterHint.ClearAll", ExternalEncounterHintProvider.ClearAll);
        Register("ExternalMechanicHint.PushJson", (string payload) => ExternalMechanicHintProvider.PushJson(payload, _ws.CurrentZone, _ws.CurrentCFCID, _ws.CurrentTime));
        Register("ExternalMechanicHint.ClearNamespace", (string ns) => ExternalMechanicHintProvider.ClearNamespace(ns));
        Register("ExternalMechanicHint.ClearAll", ExternalMechanicHintProvider.ClearAll);
        Register("AI.SetEnabled", (bool enabled) =>
        {
            var ai = AI.AIManager.Instance;
            if (ai == null)
                return;
            if (enabled && ai.Beh == null)
                ai.SwitchToFollow(PartyState.PlayerSlot);
            else if (!enabled && ai.Beh != null)
                ai.SwitchToIdle();
        });
    }

    private void Register<TRet>(string name, Func<TRet> func)
    {
        var p = _dalamud.GetIpcProvider<TRet>("BossMod." + name);
        p.RegisterFunc(func);
        _ipcUnregister.Add(p.UnregisterFunc);
    }

    private void Register<T1, TRet>(string name, Func<T1, TRet> func)
    {
        var p = _dalamud.GetIpcProvider<T1, TRet>("BossMod." + name);
        p.RegisterFunc(func);
        _ipcUnregister.Add(p.UnregisterFunc);
    }

    private void Register<T1>(string name, Action<T1> func)
    {
        var p = _dalamud.GetIpcProvider<T1, object>("BossMod." + name);
        p.RegisterAction(func);
        _ipcUnregister.Add(p.UnregisterAction);
    }
}
