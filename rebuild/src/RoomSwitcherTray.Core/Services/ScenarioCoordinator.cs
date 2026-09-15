namespace RoomSwitcherTray.Core.Services;

public interface IScenarioDevices
{
    Task<DeviceSnapshot> CaptureAsync();
    Task ApplyDisplaysAsync(IReadOnlyCollection<string> ids);
    Task ApplyResolutionsAsync(ScenarioDefinition scenario);
    void ApplyAudio(AudioDevice device, int? volume);
}

/// <summary>UI-independent state machine. Call from one dispatcher; reads may run in the backend worker.</summary>
public class ScenarioCoordinator : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly Action _save;
    private readonly Action<Exception> _log;
    private readonly IScenarioDevices _devices;
    private readonly TimeSpan _audioTimeout;
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private TaskCompletionSource _snapshotChanged = NewSignal();
    private CancellationTokenSource? _audioWait;
    private ScenarioDefinition? _pendingAudioScenario;
    private int _generation;
    private bool _disposed;
    private string? _failure;
    private Guid? _desiredId;
    private bool _hasAttempt;
    private bool _audioError;
    private bool _readError;
    public bool IsApplying { get; private set; }
    public bool IsWaitingForAudio => _audioWait is not null;
    public DeviceSnapshot Snapshot { get; private set; } = DeviceSnapshot.Empty;
    public bool HasSnapshot { get; private set; }
    public bool HasReliableSnapshot => HasSnapshot && !_readError;
    public event EventHandler? Changed;

    public ScenarioCoordinator(Func<AppSettings> settings, Action save, Action<Exception> log,
        IScenarioDevices devices, TimeSpan? audioTimeout = null)
    {
        _settings = settings; _save = save; _log = log; _devices = devices;
        _audioTimeout = audioTimeout ?? TimeSpan.FromSeconds(18);
    }

    public ScenarioDefinition? DesiredScenario => _settings().Scenarios.FirstOrDefault(item =>
        item.Id == (_hasAttempt ? _desiredId : _settings().ActiveScenarioId));

    public ScenarioStatus Status
    {
        get
        {
            bool english = _settings().Language == AppLanguage.English;
            if (DesiredScenario is null) return new(ScenarioHealth.None, "");
            if (IsApplying || IsWaitingForAudio)
                return new(ScenarioHealth.Checking, "");
            if (_failure is not null) return new(ScenarioHealth.Failed, _failure);
            if (_readError) return new(ScenarioHealth.Partial,
                english ? "Could not check devices" : "Не удалось проверить устройства", ScenarioIssue.DevicesCheckFailed);
            if (!HasSnapshot) return new(ScenarioHealth.Checking, "");
            ScenarioStatus result = ScenarioPolicy.Evaluate(DesiredScenario, Snapshot, _settings());
            if (_audioError && result.Health == ScenarioHealth.Full)
                return new(ScenarioHealth.Partial, english ? "Audio could not be applied" : "Не удалось настроить звук");
            return result;
        }
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        await _captureLock.WaitAsync();
        try
        {
            DeviceSnapshot fresh = await _devices.CaptureAsync();
            if (_disposed) return;
            Snapshot = fresh;
            HasSnapshot = true;
            _readError = false;
            AppSettings settings = _settings();
            bool namesChanged = AudioEndpointMigration.Reconcile(settings, fresh);
            foreach ((string id, string name) in fresh.Displays.Where(item => item.IsAvailable).Select(item => (item.Id, item.Name))
                .Concat(fresh.Audio.Where(item => item.State != AudioDeviceState.NotPresent ||
                    settings.Scenarios.Any(scenario => ScenarioPolicy.Same(scenario.AudioDeviceId, item.Id)))
                    .Select(item => (item.Id, item.Name))))
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (settings.KnownDeviceNames.GetValueOrDefault(id) == name) continue;
                settings.KnownDeviceNames[id] = name;
                namesChanged = true;
            }
            if (namesChanged) SaveSafely();
            TaskCompletionSource signal = _snapshotChanged;
            _snapshotChanged = NewSignal();
            signal.TrySetResult();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            _readError = true;
            if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }
        finally { _captureLock.Release(); }
    }

    public async Task<ApplyResult> ApplyAsync(Guid scenarioId)
    {
        if (_disposed) return new(false, "");
        ScenarioDefinition? original = _settings().Scenarios.FirstOrDefault(item => item.Id == scenarioId);
        bool english = _settings().Language == AppLanguage.English;
        if (original?.IsComplete != true) return new(false, english ? "Scenario is not configured." : "Сценарий не настроен.");
        if (IsApplying) return new(false, english ? "Switching is in progress." : "Переключение уже выполняется.");
        CancelAudioWait();
        int generation = ++_generation;
        ScenarioDefinition scenario = original.Clone();
        _hasAttempt = true; _desiredId = scenario.Id; _failure = null; _audioError = false;
        IsApplying = true;
        Changed?.Invoke(this, EventArgs.Empty);
        string[] previous = [];
        bool touchedDisplays = false;
        try
        {
            await RefreshAsync();
            if (_disposed) return new(false, "");
            string[] available = ScenarioPolicy.AvailableDisplays(scenario, Snapshot);
            if (available.Length == 0)
            {
                _failure = english ? "No scenario displays are connected" : "Экраны сценария не подключены";
                return new(false, _failure);
            }
            previous = Snapshot.Displays.Where(item => item.IsActive && item.IsAvailable).Select(item => item.Id).ToArray();
            touchedDisplays = true;
            await _devices.ApplyDisplaysAsync(available);
            await RefreshAsync();
            // A driver may publish its new active paths shortly after SetDisplayConfig returns.
            // Retry only this bounded transition; there is no idle display polling.
            for (int attempt = 0; attempt < 4 && !HasActiveRequestedDisplay(available) && !_disposed; attempt++)
            {
                await Task.Delay(150);
                await RefreshAsync();
            }
            if (!HasActiveRequestedDisplay(available))
                throw new InvalidOperationException(english ? "No scenario display was activated" : "Не удалось включить экран сценария");
            await _devices.ApplyResolutionsAsync(scenario);
            await RefreshAsync();
            if (_disposed) return new(false, "");
            _settings().ActiveScenarioId = scenario.Id;
            SaveSafely();

            AudioDevice? audio = ScenarioPolicy.FindAudio(scenario, Snapshot);
            if (audio is not null)
            {
                ApplyAudioSafely(scenario, audio);
                try { await RefreshAsync(); }
                catch (Exception audioRefreshError) { _log(audioRefreshError); _audioError = true; }
            }
            else if (!string.IsNullOrWhiteSpace(scenario.AudioDeviceId) && ScenarioPolicy.MayAwaitHdmi(scenario, Snapshot))
            {
                var wait = new CancellationTokenSource(_audioTimeout);
                _audioWait = wait;
                _pendingAudioScenario = scenario;
                _ = AwaitAudioAsync(scenario, generation, wait);
            }
            return new(true, english ? $"Scenario “{scenario.Name}” selected." : $"Выбран сценарий «{scenario.Name}».");
        }
        catch (Exception ex)
        {
            _log(ex);
            _failure = english ? "Could not apply display configuration" : "Не удалось применить конфигурацию экранов";
            // Best-effort rollback only to previously active displays that are still connected.
            if (touchedDisplays && !_disposed)
            {
                try
                {
                    await RefreshAsync();
                    string[] restore = previous.Where(id => Snapshot.Displays.Any(item =>
                        item.IsAvailable && ScenarioPolicy.Same(item.Id, id))).ToArray();
                    if (restore.Length > 0) await _devices.ApplyDisplaysAsync(restore);
                    await RefreshAsync();
                }
                catch (Exception rollbackError) { _log(rollbackError); }
            }
            return new(false, _failure);
        }
        finally
        {
            IsApplying = false;
            if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyAudioSafely(ScenarioDefinition scenario, AudioDevice device)
    {
        try
        {
            // Backend sets volume on this exact endpoint, never on a Windows fallback.
            _devices.ApplyAudio(device, scenario.VolumePercent);
            ScenarioDefinition? saved = _settings().Scenarios.FirstOrDefault(item => item.Id == scenario.Id);
            string containerId = device.ContainerId?.ToString("D") ?? scenario.AudioDeviceContainerId;
            if (saved is not null && (saved.AudioDeviceId != device.Id ||
                !ScenarioPolicy.Same(saved.AudioDeviceContainerId, containerId)))
            {
                string previousAudioId = saved.AudioDeviceId;
                if (_settings().DeviceAliases.TryGetValue(saved.AudioDeviceId, out string? alias) &&
                    !_settings().DeviceAliases.ContainsKey(device.Id))
                    _settings().DeviceAliases[device.Id] = alias;
                saved.AudioDeviceId = device.Id;
                saved.AudioDeviceContainerId = containerId;
                if (!ScenarioPolicy.Same(previousAudioId, device.Id) &&
                    !_settings().RetiredAudioDeviceIds.Contains(previousAudioId, StringComparer.OrdinalIgnoreCase))
                    _settings().RetiredAudioDeviceIds.Add(previousAudioId);
                if (_pendingAudioScenario?.Id == scenario.Id) _pendingAudioScenario = saved.Clone();
                SaveSafely();
            }
        }
        catch (Exception ex) { _log(ex); _audioError = true; }
    }

    private bool HasActiveRequestedDisplay(IEnumerable<string> ids) => ids.Any(id => Snapshot.Displays.Any(item =>
        item.IsAvailable && item.IsActive && ScenarioPolicy.Same(id, item.Id)));

    private async Task AwaitAudioAsync(ScenarioDefinition scenario, int generation, CancellationTokenSource wait)
    {
        try
        {
            while (!wait.IsCancellationRequested)
            {
                Task nextSnapshot = _snapshotChanged.Task; // Subscribe before checking; no lost wake-up.
                AudioDevice? device = ScenarioPolicy.FindAudio(scenario, Snapshot);
                if (device is not null)
                {
                    if (_disposed || generation != _generation || wait.IsCancellationRequested) return;
                    if (!PendingIntentStillMatches(scenario)) return;
                    ApplyAudioSafely(scenario, device);
                    await RefreshAsync();
                    return;
                }
                // Device events wake this task; there is no recurring hardware poll.
                await nextSnapshot.WaitAsync(wait.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log(ex); _audioError = true; }
        finally
        {
            if (ReferenceEquals(_audioWait, wait))
            {
                _audioWait = null;
                _pendingAudioScenario = null;
                if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
            }
            wait.Dispose();
        }
    }

    public void CancelAudioWait()
    {
        CancellationTokenSource? wait = _audioWait;
        _audioWait = null;
        _pendingAudioScenario = null;
        wait?.Cancel();
    }

    public void SettingsChanged()
    {
        if (_pendingAudioScenario is not null && !PendingIntentStillMatches(_pendingAudioScenario))
            CancelAudioWait();
        if (_hasAttempt && !_settings().Scenarios.Any(item => item.Id == _desiredId))
        {
            _hasAttempt = false;
            _failure = null;
            _audioError = false;
        }
    }

    private bool PendingIntentStillMatches(ScenarioDefinition pending)
    {
        ScenarioDefinition? current = _settings().Scenarios.FirstOrDefault(item => item.Id == pending.Id);
        return current is not null && current.DisplayIds.SequenceEqual(pending.DisplayIds, StringComparer.OrdinalIgnoreCase) &&
            (ScenarioPolicy.Same(current.AudioDeviceId, pending.AudioDeviceId) ||
                ScenarioPolicy.Same(current.AudioDeviceId, AudioEndpointMigration.FindReplacement(pending, Snapshot)?.Id)) &&
            ScenarioPolicy.Same(current.AudioDeviceContainerId, pending.AudioDeviceContainerId) &&
            current.VolumePercent == pending.VolumePercent;
    }

    private void SaveSafely()
    {
        try { _save(); }
        catch (Exception ex) { _log(ex); }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose()
    {
        _disposed = true;
        ++_generation;
        CancelAudioWait();
    }
}
