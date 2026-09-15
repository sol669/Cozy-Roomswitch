using RoomSwitcherTray.Core.Interop;
using System.Runtime.InteropServices;

namespace RoomSwitcherTray.Core.Services;

public sealed class TrayService : IDisposable
{
    private const uint TrayMessage = TrayNative.WM_APP + 1;
    private const int SwitchHotKeyId = 1;
    private const uint ScenarioCommandBase = 2000;
    private const uint SettingsCommand = 3000;
    private const uint ExitCommand = 3001;
    private const uint TurnOffMonitorsCommand = 3002;
    private const uint HdrCommandBase = 4000;
    private const uint ResolutionCommandBase = 4500;
    private const uint ScaleCommandBase = 4700;
    private const uint MuteCommand = 5000;
    private const uint AudioDeviceCommandBase = 5100;

    private readonly SettingsStore _settings;
    private readonly ScenarioService _scenarios;
    private readonly TrayNative.WindowProcedure _windowProcedure;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly Dictionary<uint, ActiveDisplayStatus> _hdrCommands = [];
    private readonly Dictionary<uint, (ActiveDisplayStatus Display, DisplayResolutionPreset Preset)> _resolutionCommands = [];
    private readonly Dictionary<uint, (ActiveDisplayStatus Display, int Percent)> _scaleCommands = [];
    private readonly Dictionary<uint, AudioDevice> _audioDeviceCommands = [];
    private string? _muteEndpointId;
    private nint _window;
    private nint _icon;
    private TrayNative.NOTIFYICONDATA _notifyData;
    private WinUiSettingsWindow? _settingsWindow;
    private bool _hotKeyRegistered;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _deviceTimer;
    private AudioDeviceWatcher? _audioWatcher;
    private nint _deviceNotification;
    private uint _taskbarCreated;
    private bool _disposed, _menuOpen, _deviceRefreshRunning;
    private string? _lastIconKey;
    private string? _remoteVolumeEndpointId;

    public TrayService(SettingsStore settings, ScenarioService scenarios)
    {
        _settings = settings;
        _scenarios = scenarios;
        _windowProcedure = WindowProc;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _deviceTimer = _dispatcher.CreateTimer();
        _deviceTimer.IsRepeating = false;
        _deviceTimer.Interval = TimeSpan.FromMilliseconds(750);
        _deviceTimer.Tick += async (_, _) => await RefreshDevicesAsync();
        _settings.Saved += OnSettingsSaved;
        _scenarios.Changed += OnScenarioChanged;
    }

    public void Initialize()
    {
        const string className = "sol669.CozyRoomswitch.TrayWindow";
        nint instance = TrayNative.GetModuleHandle(null);
        var windowClass = new TrayNative.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<TrayNative.WNDCLASSEX>(),
            lpfnWndProc = _windowProcedure,
            hInstance = instance,
            lpszClassName = className
        };
        TrayNative.RegisterClassEx(ref windowClass);
        _window = TrayNative.CreateWindowEx(0, className, "Cozy Roomswitch", 0,
            0, 0, 0, 0, nint.Zero, nint.Zero, instance, nint.Zero);
        TrayNative.WTSRegisterSessionNotification(_window, TrayNative.NOTIFY_FOR_THIS_SESSION);
        _taskbarCreated = TrayNative.RegisterWindowMessage("TaskbarCreated");
        var filter = new TrayNative.DEVICE_INTERFACE_FILTER
        {
            Size = (uint)Marshal.SizeOf<TrayNative.DEVICE_INTERFACE_FILTER>(), DeviceType = 5
        };
        _deviceNotification = TrayNative.RegisterDeviceNotification(_window, ref filter, 4);
        if (_deviceNotification == nint.Zero)
            SettingsStore.Log(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        try { _audioWatcher = new AudioDeviceWatcher(() => _dispatcher.TryEnqueue(ScheduleDeviceRefresh)); }
        catch (Exception ex) { SettingsStore.Log(ex); }

        _icon = TrayIconFactory.Create(GetActiveScenario(), remote: IsRemoteSession);
        _notifyData = new TrayNative.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<TrayNative.NOTIFYICONDATA>(), hWnd = _window, uID = 1,
            uFlags = TrayNative.NIF_MESSAGE | TrayNative.NIF_ICON | TrayNative.NIF_TIP,
            uCallbackMessage = TrayMessage, hIcon = _icon, szTip = BuildTooltip(),
            szInfo = string.Empty, szInfoTitle = string.Empty
        };
        TrayNative.Shell_NotifyIcon(TrayNative.NIM_ADD, ref _notifyData);
        HotKeyDefinition hotKey = _settings.Current.SwitchScenarioHotKey ?? HotKeyDefinition.Default;
        _hotKeyRegistered = TrayNative.RegisterHotKey(_window, SwitchHotKeyId,
            hotKey.Modifiers | TrayNative.MOD_NOREPEAT, hotKey.VirtualKey);
        if (!_hotKeyRegistered)
            ShowNotification($"Не удалось назначить {FormatHotKey(hotKey)}: комбинация уже занята.", false);
        ScheduleDeviceRefresh();
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == TrayMessage)
            {
                uint trayEvent = unchecked((uint)lParam.ToInt64());
                if (trayEvent == TrayNative.WM_RBUTTONUP) ShowMenu();
                else if (trayEvent == TrayNative.WM_LBUTTONDBLCLK && !IsRemoteSession)
                    _dispatcher.TryEnqueue(HandleShortcutAction);
                return nint.Zero;
            }
            if (message == TrayNative.WM_HOTKEY && (int)wParam == SwitchHotKeyId)
            {
                if (!IsRemoteSession) _dispatcher.TryEnqueue(HandleShortcutAction);
                return nint.Zero;
            }
            if (message == TrayNative.WM_WTSSESSION_CHANGE)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (IsRemoteSession) _scenarios.CancelAudioWait();
                    _remoteVolumeEndpointId = null;
                    _lastIconKey = null;
                    Refresh();
                    ScheduleDeviceRefresh();
                });
                return nint.Zero;
            }
            if (message == TrayNative.WM_DEVICECHANGE || message == TrayNative.WM_DISPLAYCHANGE ||
                message == TrayNative.WM_POWERBROADCAST)
                _dispatcher.TryEnqueue(ScheduleDeviceRefresh);
            if (message == TrayNative.WM_SETTINGCHANGE || message == TrayNative.WM_THEMECHANGED ||
                (_taskbarCreated != 0 && message == _taskbarCreated))
                _dispatcher.TryEnqueue(() => { _lastIconKey = null; Refresh(); });
        }
        catch (Exception ex) { SettingsStore.Log(ex); }
        return TrayNative.DefWindowProc(window, message, wParam, lParam);
    }

    private async void ShowMenu()
    {
        if (_disposed || _menuOpen) return;
        _menuOpen = true;
        await RefreshDevicesAsync();
        if (_disposed) { _menuOpen = false; return; }
        NativeTheme.Apply(_window, _settings.Current.Theme);
        nint menu = TrayNative.CreatePopupMenu();
        try
        {
            _hdrCommands.Clear();
            _resolutionCommands.Clear();
            _scaleCommands.Clear();
            _audioDeviceCommands.Clear();
            _muteEndpointId = null;
            if (IsRemoteSession)
            {
                TrayNative.AppendMenu(menu, TrayNative.MF_STRING | TrayNative.MF_DISABLED, 0, UiText.Get(_settings.Current, "Remote"));
                TrayNative.AppendMenu(menu, TrayNative.MF_SEPARATOR, 0, null);
                BuildStatusSection(menu, remoteSession: true);
                TrayNative.AppendMenu(menu, TrayNative.MF_SEPARATOR, 0, null);
                AppendTurnOffMonitors(menu);
                TrayNative.AppendMenu(menu, TrayNative.MF_STRING, SettingsCommand, UiText.Get(_settings.Current, "Settings"));
            }
            else if (_settings.IsConfigured)
            {
                Guid? nextScenarioId = GetNextScenario()?.Id;
                foreach ((ScenarioDefinition scenario, int index) in ScenarioPolicy.TrayOrder(_settings.Current))
                {
                    uint scenarioCommand = ScenarioCommandBase + (uint)index;
                    string label = scenario.Name;
                    if (scenario.Id == nextScenarioId)
                        label += $"\t{FormatHotKey(_settings.Current.SwitchScenarioHotKey)}";
                    bool available = _scenarios.HasReliableSnapshot && ScenarioPolicy.CanApply(scenario, _scenarios.Snapshot);
                    TrayNative.AppendMenu(menu, TrayNative.MF_STRING | (available ? 0 : TrayNative.MF_GRAYED), scenarioCommand, label);
                    if (_settings.Current.ActiveScenarioId == scenario.Id)
                        TrayNative.SetMenuDefaultItem(menu, scenarioCommand, 0);
                }

                TrayNative.AppendMenu(menu, TrayNative.MF_SEPARATOR, 0, null);
                BuildStatusSection(menu, remoteSession: false);
                TrayNative.AppendMenu(menu, TrayNative.MF_SEPARATOR, 0, null);
                AppendTurnOffMonitors(menu);
                TrayNative.AppendMenu(menu, TrayNative.MF_STRING, SettingsCommand, UiText.Get(_settings.Current, "Settings"));
            }
            else
            {
                TrayNative.AppendMenu(menu, TrayNative.MF_STRING,
                    SettingsCommand, UiText.Get(_settings.Current, "Configure"));
                TrayNative.SetMenuDefaultItem(menu, SettingsCommand, 0);
                TrayNative.AppendMenu(menu, TrayNative.MF_SEPARATOR, 0, null);
            }
            TrayNative.AppendMenu(menu, TrayNative.MF_STRING, ExitCommand, UiText.Get(_settings.Current, "Exit"));

            TrayNative.GetCursorPos(out TrayNative.POINT point);
            TrayNative.SetForegroundWindow(_window);
            uint command = TrayNative.TrackPopupMenu(menu, TrayNative.TPM_RIGHTBUTTON | TrayNative.TPM_RETURNCMD,
                point.X, point.Y, 0, _window, nint.Zero);
            TrayNative.PostMessage(_window, 0, 0, 0);
            _dispatcher.TryEnqueue(() => Execute(command));
        }
        catch (Exception ex) { SettingsStore.Log(ex); }
        finally { TrayNative.DestroyMenu(menu); _menuOpen = false; }
    }

    private void BuildStatusSection(nint menu, bool remoteSession)
    {
        bool added = false;
        bool english = _settings.Current.Language == AppLanguage.English;
        string disconnected = english ? "Not connected" : "Не подключён";
        string unused = english ? "Not in use" : "Не используется";
        try
        {
            DeviceSnapshot snapshot = _scenarios.Snapshot;
            if (remoteSession)
            {
                var active = App.Displays.GetActiveDisplayStatuses();
                var endpoints = AudioService.GetRenderDevices();
                snapshot = new DeviceSnapshot(
                    active.Select(item => new DisplayDevice(item.Id, item.Name, true, true, null)).ToArray(),
                    endpoints, active, App.Audio.GetDefaultEndpointStatus(endpoints));
            }
            ScenarioDefinition? scenario = GetActiveScenario();
            ScenarioTrayDevices selected = ScenarioPolicy.TrayDevices(scenario, snapshot);
            IEnumerable<string> displayIds = remoteSession
                ? snapshot.Displays.Where(item => item.IsActive).Select(item => item.Id)
                : selected.DisplayIds;
            int remoteDisplayCount = displayIds.Count();
            int remoteDisplayIndex = 0;
            foreach (string id in displayIds)
            {
                remoteDisplayIndex++;
                DisplayDevice? device = snapshot.Displays.FirstOrDefault(item => ScenarioPolicy.Same(item.Id, id));
                ActiveDisplayStatus? display = snapshot.ActiveDisplays.FirstOrDefault(item => ScenarioPolicy.Same(item.Id, id));
                string name = remoteSession
                    ? remoteDisplayCount > 1 ? $"{UiText.Get(_settings.Current, "RemoteDisplay")} - {remoteDisplayIndex}" : UiText.Get(_settings.Current, "RemoteDisplay")
                    :
                    ScenarioPolicy.Name(_settings.Current, snapshot, id, english ? "Monitor" : "Монитор");
                int? activeScale = display is null ? null : App.Displays.GetScale(display.Id);
                string state = device?.IsAvailable != true ? disconnected : display is null ? unused :
                    $"{display.Width} × {display.Height}" +
                    (activeScale.HasValue ? $" · {activeScale}%" : string.Empty) +
                    $" · {(remoteSession ? "SDR" : display.HdrEnabled ? "HDR" : "SDR")}";
                string text = $"{name}\t{state}";
                if (!remoteSession && device?.IsAvailable == true && display is not null)
                {
                    nint submenu = TrayNative.CreatePopupMenu();
                    if (display.HdrSupported)
                    {
                        uint command = HdrCommandBase + (uint)_hdrCommands.Count;
                        _hdrCommands[command] = display;
                        TrayNative.AppendMenu(submenu, TrayNative.MF_STRING, command,
                            UiText.Get(_settings.Current, display.HdrEnabled ? "DisableHdr" : "EnableHdr"));
                    }
                    nint resolutions = TrayNative.CreatePopupMenu();
                    foreach (DisplayResolutionPreset preset in App.Displays.GetSupportedResolutionPresets(display.Id))
                    {
                        (int width, int height) = DisplayResolution.Size(preset);
                        uint command = ResolutionCommandBase + (uint)_resolutionCommands.Count;
                        _resolutionCommands[command] = (display, preset);
                        bool checkedItem = display.Width == width && display.Height == height;
                        TrayNative.AppendMenu(resolutions, TrayNative.MF_STRING | (checkedItem ? TrayNative.MF_CHECKED : 0),
                            command, ResolutionLabel(preset));
                    }
                    TrayNative.AppendMenu(submenu, TrayNative.MF_STRING | TrayNative.MF_POPUP,
                        (nuint)resolutions, english ? "Resolution" : "Разрешение");
                    nint scales = TrayNative.CreatePopupMenu();
                    IReadOnlyList<int> supportedScales = App.Displays.GetSupportedScales(display.Id);
                    foreach (int percent in supportedScales.Count > 0 ? supportedScales : new[] { 100, 125, 150, 175, 200 })
                    {
                        uint command = ScaleCommandBase + (uint)_scaleCommands.Count;
                        _scaleCommands[command] = (display, percent);
                        TrayNative.AppendMenu(scales, TrayNative.MF_STRING |
                            (activeScale == percent ? TrayNative.MF_CHECKED : 0), command, $"{percent}%");
                    }
                    TrayNative.AppendMenu(submenu, TrayNative.MF_STRING | TrayNative.MF_POPUP,
                        (nuint)scales, english ? "Scale" : "Масштаб");
                    TrayNative.AppendMenu(menu, TrayNative.MF_STRING | TrayNative.MF_POPUP, (nuint)submenu, text);
                }
                else
                {
                    // SDR is information, not an unavailable device. Keep an active display
                    // in the normal menu color; it simply has no HDR submenu.
                    bool activeDisplay = device?.IsAvailable == true && display is not null;
                    TrayNative.AppendMenu(menu, TrayNative.MF_STRING | (activeDisplay ? 0 : TrayNative.MF_GRAYED), 0, text);
                }
                added = true;
            }

            var audioRows = new List<(string Id, AudioDevice? Device)>();
            if (remoteSession)
            {
                // Remote-session controls are separate from local scenario configuration.
                AudioDevice? remoteAudio = snapshot.Audio.FirstOrDefault(item => item.IsActive && item.IsDefault);
                if (remoteAudio is not null) audioRows.Add((remoteAudio.Id, remoteAudio));
            }
            else
            {
                // The tray is a live control surface: after a temporary manual switch,
                // show the endpoint Windows is actually using instead of rewriting the scenario.
                AudioDevice? currentAudio = snapshot.Audio.FirstOrDefault(item => item.IsActive && item.IsDefault);
                if (currentAudio is not null) audioRows.Add((currentAudio.Id, currentAudio));
                else if (selected.AudioId is not null) audioRows.Add((selected.AudioId, selected.Audio));
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string id, AudioDevice? device) in audioRows)
            {
                if (!seen.Add(device?.Id ?? id)) continue;
                string name = remoteSession ? UiText.Get(_settings.Current, "RemoteAudio") :
                    ScenarioPolicy.Name(_settings.Current, snapshot, id, device?.DisplayName ?? device?.Name ??
                        (english ? "Audio device" : "Аудиоустройство"));
                AudioEndpointStatus? audio = device?.IsActive == true && device.IsDefault ? snapshot.DefaultAudio : null;
                string state = snapshot.AudioReadFailed ? (english ? "No data" : "Нет данных") :
                    device?.State == AudioDeviceState.Disabled
                    ? (english ? "Disabled in Windows" : "Отключено в Windows")
                    : device?.IsActive != true ? disconnected :
                        audio is null ? device.IsDefault ? (english ? "No data" : "Нет данных") : unused :
                        UiText.AudioLevel(_settings.Current, audio);
                if (audio is not null)
                {
                    _muteEndpointId = device!.Id;
                    nint submenu = TrayNative.CreatePopupMenu();
                    TrayNative.AppendMenu(submenu, TrayNative.MF_STRING | (audio.IsMuted ? TrayNative.MF_CHECKED : 0),
                        MuteCommand, UiText.Get(_settings.Current, "Mute"));
                    if (!remoteSession)
                    {
                        IReadOnlyList<AudioDevice> availableAudio = AudioService.GetVisibleRenderDevices(
                            snapshot.Audio, snapshot.Displays, _settings.Current.RetiredAudioDeviceIds,
                            [.. _settings.Current.Scenarios.Cast<ScenarioDefinition?>()])
                            .Where(item => item.IsActive)
                            .OrderByDescending(item => item.IsDefault)
                            .ThenBy(item => DeviceAliasService.NameFor(_settings.Current, item.Id,
                                item.DisplayName ?? item.Name), StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                        if (availableAudio.Count > 0)
                            TrayNative.AppendMenu(submenu, TrayNative.MF_SEPARATOR, 0, null);
                        foreach (AudioDevice endpoint in availableAudio)
                        {
                            uint command = AudioDeviceCommandBase + (uint)_audioDeviceCommands.Count;
                            _audioDeviceCommands[command] = endpoint;
                            string endpointName = DeviceAliasService.NameFor(_settings.Current, endpoint.Id,
                                endpoint.DisplayName ?? endpoint.Name);
                            TrayNative.AppendMenu(submenu, TrayNative.MF_STRING |
                                (endpoint.IsDefault ? TrayNative.MF_CHECKED : 0), command, endpointName);
                        }
                    }
                    TrayNative.AppendMenu(menu, TrayNative.MF_STRING | TrayNative.MF_POPUP, (nuint)submenu, $"{name}\t{state}");
                }
                else TrayNative.AppendMenu(menu, TrayNative.MF_STRING | TrayNative.MF_GRAYED, 0, $"{name}\t{state}");
                added = true;
            }
        }
        catch (Exception ex) { SettingsStore.Log(ex); }
        if (!added) TrayNative.AppendMenu(menu, TrayNative.MF_STRING | TrayNative.MF_GRAYED,
            0, UiText.Get(_settings.Current, "NoDevices"));
    }

    private void Execute(uint command)
    {
        if (command >= ScenarioCommandBase && command < ScenarioCommandBase + _settings.Current.Scenarios.Count)
        { _ = ApplyAsync(_settings.Current.Scenarios[(int)(command - ScenarioCommandBase)].Id); return; }
        if (_hdrCommands.TryGetValue(command, out ActiveDisplayStatus? display)) { _ = ToggleHdrAsync(display); return; }
        if (_resolutionCommands.TryGetValue(command, out var resolution)) { _ = SetResolutionAsync(resolution.Display, resolution.Preset); return; }
        if (_scaleCommands.TryGetValue(command, out var scale)) { _ = SetScaleAsync(scale.Display, scale.Percent); return; }
        if (_audioDeviceCommands.TryGetValue(command, out AudioDevice? audioDevice)) { _ = SetAudioDeviceAsync(audioDevice); return; }
        switch (command)
        {
            case MuteCommand: ToggleMute(); break;
            case TurnOffMonitorsCommand: TurnOffMonitors(); break;
            case SettingsCommand: ShowSettings(); break;
            case ExitCommand: App.Quit(); break;
        }
    }

    private void AppendTurnOffMonitors(nint menu)
    {
        int count = _scenarios.Snapshot.ActiveDisplays.Count;
        string key = count == 1 ? "TurnOffMonitor" : "TurnOffMonitors";
        TrayNative.AppendMenu(menu, TrayNative.MF_STRING, TurnOffMonitorsCommand, UiText.Get(_settings.Current, key));
    }

    private static void TurnOffMonitors() =>
        TrayNative.SendMessage(TrayNative.HWND_BROADCAST, TrayNative.WM_SYSCOMMAND,
            TrayNative.SC_MONITORPOWER, new nint(2));

    private async Task ToggleHdrAsync(ActiveDisplayStatus display)
    {
        bool targetState = !display.HdrEnabled;
        try
        {
            await Task.Run(() => App.Displays.SetHdr(display.Id, targetState));
            ShowNotification($"HDR для «{display.Name}» {(display.HdrEnabled ? "выключен" : "включён")}.", true);
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            ShowNotification($"Не удалось изменить HDR: {ex.Message}", false);
        }
    }

    private async Task SetResolutionAsync(ActiveDisplayStatus display, DisplayResolutionPreset preset)
    {
        try
        {
            await Task.Run(() => App.Displays.SetResolution(display.Id, preset));
            ShowNotification($"Разрешение для «{display.Name}»: {ResolutionLabel(preset)}.", true);
            await _scenarios.RefreshAsync();
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            ShowNotification($"Не удалось изменить разрешение: {ex.Message}", false);
        }
    }

    private async Task SetScaleAsync(ActiveDisplayStatus display, int percent)
    {
        try
        {
            await Task.Run(() => App.Displays.SetScale(display.Id, percent));
            ShowNotification($"Масштаб для «{display.Name}»: {percent}%.", true);
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            ShowNotification($"Не удалось изменить масштаб: {ex.Message}", false);
        }
    }

    private async Task SetAudioDeviceAsync(AudioDevice device)
    {
        try
        {
            await Task.Run(() => AudioService.SetDefault(device.Id));
            await _scenarios.RefreshAsync();
            string name = DeviceAliasService.NameFor(_settings.Current, device.Id,
                device.DisplayName ?? device.Name);
            ShowNotification($"Аудиоустройство: {name}.", true);
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            ShowNotification($"Не удалось переключить аудиоустройство: {ex.Message}", false);
        }
    }

    private static string ResolutionLabel(DisplayResolutionPreset preset)
    {
        (int width, int height) = DisplayResolution.Size(preset);
        return preset switch
        {
            DisplayResolutionPreset.UltraHd4K => $"4K · {width} × {height}",
            DisplayResolutionPreset.QuadHd2K => $"2K · {width} × {height}",
            _ => $"{height}p · {width} × {height}"
        };
    }


    private void ToggleMute()
    {
        try
        {
            if (_muteEndpointId is null) return;
            IReadOnlyList<AudioDevice> endpoints = AudioService.GetRenderDevices();
            AudioDevice? device = endpoints.FirstOrDefault(item => item.IsActive && ScenarioPolicy.Same(item.Id, _muteEndpointId));
            if (device is null) return;
            AudioEndpointStatus current = AudioService.GetEndpointStatus(device);
            // Bind the action to the row's endpoint even if Windows changes its default while the menu is open.
            AudioService.SetEndpointMuted(device.Id, !current.IsMuted);
            ShowNotification(current.IsMuted ? "Звук включён." : "Звук выключен.", true);
        }
        catch (Exception ex) { SettingsStore.Log(ex); ShowNotification($"Не удалось изменить звук: {ex.Message}", false); }
    }

    private async void ApplyNext()
    {
        if (IsRemoteSession) return;
        if (_settings.Current.Scenarios.Count == 0) { ShowSettings(openNewScenario: true); return; }
        await RefreshDevicesAsync();
        if (GetNextScenario() is ScenarioDefinition next) await ApplyAsync(next.Id);
    }

    private void HandleShortcutAction() => ApplyNext();

    private ScenarioDefinition? GetNextScenario() => _scenarios.HasReliableSnapshot
        ? ScenarioPolicy.Next(_settings.Current, _scenarios.Snapshot) : null;

    private ScenarioDefinition? GetActiveScenario() => _settings.Current.ActiveScenarioId is Guid id
        ? _settings.Current.Scenarios.FirstOrDefault(scenario => scenario.Id == id) : null;

    private async Task ApplyAsync(Guid scenarioId)
    {
        if (IsRemoteSession || _disposed) return;
        await _scenarios.ApplyAsync(scenarioId);
        // Persistent monochrome warning + tooltip replace repeated balloon errors.
        Refresh();
    }

    internal Task ApplyScenarioAsync(Guid scenarioId) => ApplyAsync(scenarioId);

    internal bool TryUpdateHotKey(HotKeyDefinition hotKey, out string error)
    {
        if (hotKey.Modifiers == 0 || hotKey.VirtualKey == 0)
        { error = "Выберите сочетание с Ctrl, Alt, Shift или Win и обычной клавишей."; return false; }
        if (_hotKeyRegistered) TrayNative.UnregisterHotKey(_window, SwitchHotKeyId);
        _hotKeyRegistered = TrayNative.RegisterHotKey(_window, SwitchHotKeyId, hotKey.Modifiers | TrayNative.MOD_NOREPEAT, hotKey.VirtualKey);
        if (_hotKeyRegistered) { error = string.Empty; return true; }
        HotKeyDefinition previous = _settings.Current.SwitchScenarioHotKey ?? HotKeyDefinition.Default;
        _hotKeyRegistered = TrayNative.RegisterHotKey(_window, SwitchHotKeyId, previous.Modifiers | TrayNative.MOD_NOREPEAT, previous.VirtualKey);
        error = $"{FormatHotKey(hotKey)} уже занято другой программой.";
        return false;
    }

    public void ShowSettings(bool openNewScenario = false)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            if (openNewScenario) _settingsWindow.OpenNewScenarioDraft();
            return;
        }
        try
        {
            _settingsWindow = new WinUiSettingsWindow(_settings, this, openNewScenario);
            _settingsWindow.ClosedByUser += (_, _) => _settingsWindow = null;
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            ShowNotification("Не удалось открыть настройки. Подробности записаны в error.log.", false);
        }
    }

    private void OnSettingsSaved(object? sender, EventArgs args)
    {
        _scenarios.SettingsChanged();
        _remoteVolumeEndpointId = null;
        // Refresh from the committed settings, regardless of which page saved them.
        // Queue after the save handler so icon-only edits never need an Apply action.
        _dispatcher.TryEnqueue(() =>
        {
            try { Refresh(); }
            catch (Exception ex) { SettingsStore.Log(ex); }
        });
    }

    internal void Refresh()
    {
        if (_window == nint.Zero || _disposed) return;
        ScenarioDefinition? scenario = _scenarios.DesiredScenario;
        bool remote = IsRemoteSession;
        bool warning = !remote && _scenarios.Status.Warn;
        string tooltip = BuildTooltip();
        string key = $"{remote}|{warning}|{scenario?.Icon}|{scenario?.IconLetters}|{scenario?.Name}|{tooltip}";
        if (key == _lastIconKey) return;
        nint replacement = TrayIconFactory.Create(scenario?.Clone(), warning, remote);
        var update = new TrayNative.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<TrayNative.NOTIFYICONDATA>(), hWnd = _window, uID = 1,
            uFlags = TrayNative.NIF_MESSAGE | TrayNative.NIF_TIP | TrayNative.NIF_ICON,
            uCallbackMessage = TrayMessage, hIcon = replacement, szTip = tooltip,
            szInfo = string.Empty, szInfoTitle = string.Empty
        };
        // Never reuse balloon-notification flags. If Explorer lost the icon,
        // restore it; ordinary saves only modify the existing tray entry.
        if (TrayNative.Shell_NotifyIcon(TrayNative.NIM_MODIFY, ref update) ||
            TrayNative.Shell_NotifyIcon(TrayNative.NIM_ADD, ref update))
        {
            nint previous = _icon;
            _icon = replacement;
            _notifyData = update;
            _lastIconKey = key;
            if (previous != nint.Zero) TrayNative.DestroyIcon(previous);
        }
        else
        {
            TrayNative.DestroyIcon(replacement);
            SettingsStore.Log(new InvalidOperationException("Windows did not accept the updated scenario tray icon."));
        }
    }

    private string BuildTooltip() => IsRemoteSession ? UiText.Get(_settings.Current, "Remote") :
        ScenarioPolicy.Tooltip(_scenarios.DesiredScenario, _scenarios.Status, _settings.Current.Language == AppLanguage.English);

    private void OnScenarioChanged(object? sender, EventArgs args) => Refresh();

    private void ScheduleDeviceRefresh()
    {
        if (_disposed) return;
        _deviceTimer.Stop();
        _deviceTimer.Start();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_disposed || _scenarios.IsApplying) return;
        if (IsRemoteSession)
        {
            ApplyRemoteSessionVolume();
            return;
        }
        if (_deviceRefreshRunning) { ScheduleDeviceRefresh(); return; }
        _deviceRefreshRunning = true;
        try
        {
            await _scenarios.RefreshAsync();
            if (!_disposed) _settingsWindow?.UpdateDeviceSnapshot(_scenarios.Snapshot);
        }
        catch (Exception ex) { SettingsStore.Log(ex); }
        finally { _deviceRefreshRunning = false; }
    }

    private void ApplyRemoteSessionVolume()
    {
        try
        {
            AppSettings settings = _settings.Current;
            if (!settings.AdaptiveRemoteSession) return;
            IReadOnlyList<AudioDevice> endpoints = AudioService.GetRenderDevices();
            AudioDevice? endpoint = endpoints.FirstOrDefault(item => item.IsActive && item.IsDefault);
            if (endpoint is null || ScenarioPolicy.Same(endpoint.Id, _remoteVolumeEndpointId)) return;
            int volume = Math.Clamp(settings.RemoteSessionVolumePercent, 0, 100);
            AudioService.SetEndpointVolume(endpoint.Id, volume);
            _remoteVolumeEndpointId = endpoint.Id;
        }
        catch (Exception ex) { SettingsStore.Log(ex); }
    }

    internal static bool IsRemoteSession => TrayNative.GetSystemMetrics(TrayNative.SM_REMOTESESSION) != 0;

    internal static string FormatHotKey(HotKeyDefinition? hotKey)
    {
        hotKey ??= HotKeyDefinition.Default;
        var parts = new List<string>();
        if ((hotKey.Modifiers & HotKeyDefinition.Control) != 0) parts.Add("Ctrl");
        if ((hotKey.Modifiers & HotKeyDefinition.Alt) != 0) parts.Add("Alt");
        if ((hotKey.Modifiers & HotKeyDefinition.Shift) != 0) parts.Add("Shift");
        if ((hotKey.Modifiers & HotKeyDefinition.Win) != 0) parts.Add("Win");
        parts.Add(FormatVirtualKey(hotKey.VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string FormatVirtualKey(uint key) => key switch
    {
        TrayNative.VK_SPACE => "Space",
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        0x1B => "Esc",
        0x09 => "Tab",
        0x0D => "Enter",
        0x2E => "Delete",
        _ when key is >= 0x30 and <= 0x5A => ((char)key).ToString().ToUpperInvariant(),
        _ => $"Клавиша {key}"
    };

    private void ShowNotification(string message, bool success)
    {
        _notifyData.uFlags = TrayNative.NIF_INFO;
        _notifyData.szInfoTitle = "Cozy Roomswitch";
        _notifyData.szInfo = message;
        _notifyData.dwInfoFlags = success ? TrayNative.NIIF_INFO : TrayNative.NIIF_ERROR;
        TrayNative.Shell_NotifyIcon(TrayNative.NIM_MODIFY, ref _notifyData);
    }

    public void Dispose()
    {
        _disposed = true;
        _deviceTimer.Stop();
        _audioWatcher?.Dispose();
        _audioWatcher = null;
        if (_deviceNotification != nint.Zero) TrayNative.UnregisterDeviceNotification(_deviceNotification);
        _scenarios.Changed -= OnScenarioChanged;
        _scenarios.Dispose();
        _settings.Saved -= OnSettingsSaved;
        if (_window != nint.Zero)
        {
            if (_hotKeyRegistered) TrayNative.UnregisterHotKey(_window, SwitchHotKeyId);
            TrayNative.WTSUnRegisterSessionNotification(_window);
            TrayNative.Shell_NotifyIcon(TrayNative.NIM_DELETE, ref _notifyData);
            TrayNative.DestroyWindow(_window);
            _window = nint.Zero;
        }
        if (_icon != nint.Zero) { TrayNative.DestroyIcon(_icon); _icon = nint.Zero; }
    }
}
