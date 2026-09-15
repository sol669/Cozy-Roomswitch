using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RoomSwitcherTray.Core.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace RoomSwitcherTray.Core;

/// <summary>Fixed WinUI settings shell shared by general, device and scenario pages.</summary>
public sealed class WinUiSettingsWindow : Window, IDisposable
{
    private const double TitleBandHeight = 64;
    private const double ContentTopMargin = 8;
    private const double ValueColumnWidth = 244;
    private readonly SettingsStore _settings;
    private readonly TrayService _tray;
    private readonly List<ScenarioDefinition> _scenarios;
    private readonly List<DisplayDevice> _displays = [];
    private readonly List<AudioDevice> _audio = [];
    private readonly Grid _root = new();
    private readonly ContentControl _pageHost = new();
    private readonly Dictionary<string, Button> _navButtons = [];
    private readonly Dictionary<string, string> _pendingAliases = new(StringComparer.OrdinalIgnoreCase);
    private StackPanel? _scenarioNavPanel;
    private Button? _saveButton;
    private Button? _deleteButton;
    private TextBlock? _hotKeyTitle;
    private TextBlock? _hotKeyHint;
    private Button? _hotKeyCaptureButton;
    private TextBlock? _autostartState;
    private ToggleSwitch? _autostartToggle;
    private TextBlock? _adaptiveRemoteState;
    private ToggleSwitch? _adaptiveRemoteToggle;
    private ComboBox? _startupChoiceBox;
    private ScenarioDefinition? _draft;
    private ScenarioDefinition? _scenarioBaseline;
    private StartupVolumeInput _volumeInput = new(null);
    private string[] _draftMonitorSlots = new string[4];
    private string _currentPage = "general";
    private bool _draftIsNew;
    private bool _loading;
    private bool _devicesLoaded;
    private bool _deviceRefreshPending;
    private string? _deviceFingerprint;
    private bool _capturingHotKey;
    private bool _allowClose;
    private bool _dialogOpen;
    private bool _pendingStartWithWindows;
    private bool _pendingAdaptiveRemoteSession;
    private int _pendingRemoteVolume;
    private StartupVolumeInput _remoteVolumeInput = new(100);
    private StartupScenarioMode _pendingStartupMode;
    private Guid? _pendingStartupScenarioId;
    private AppThemeMode _pendingTheme;
    private AppLanguage _pendingLanguage;
    private HotKeyDefinition _pendingHotKey = HotKeyDefinition.Default;

    public event EventHandler? ClosedByUser;

    public WinUiSettingsWindow(SettingsStore settings, TrayService tray, bool openNewScenario = false)
    {
        _settings = settings;
        _tray = tray;
        _scenarios = settings.Current.Scenarios.Select(item => item.Clone()).ToList();
        ResetPendingGeneral();
        ResetPendingRemote();
        ResetPendingAliases();
        _root.IsTabStop = true;
        _pageHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _pageHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        Title = "Cozy Roomswitch";
        try { SystemBackdrop = new MicaBackdrop(); } catch { }

        AppWindow appWindow = ConfigureWindow();
        appWindow.Closing += (sender, args) =>
        {
            if (_allowClose || !HasCurrentChanges()) return;
            args.Cancel = true;
            _ = ConfirmCloseAsync();
        };
        Closed += (_, _) => ClosedByUser?.Invoke(this, EventArgs.Empty);
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated && _deviceRefreshPending)
            {
                _deviceRefreshPending = false;
                ShowCurrentPage();
            }
        };

        BuildShell();
        _ = LoadDevicesAsync(openNewScenario);
    }

    public void Dispose() => Close();

    public void OpenNewScenarioDraft()
    {
        Activate();
        _ = OpenNewScenarioDraftAsync();
    }

    private AppWindow ConfigureWindow()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow window = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        try { window.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon", "RoomSwitcher.ico")); }
        catch (Exception ex) { SettingsStore.Log(ex); }
        NativeTheme.Apply(hwnd, _settings.Current.Theme);
        const int logicalWidth = 900;
        const int logicalHeight = 820;
        double scale = Math.Max(1, GetDpiForWindow(hwnd) / 96.0);
        GetCursorPos(out NativePoint cursor);
        RectInt32 work = DisplayArea.GetFromPoint(
            new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)Math.Round(logicalWidth * scale), Math.Max(780, work.Width - 48));
        int height = Math.Min((int)Math.Round(logicalHeight * scale), Math.Max(640, work.Height - 48));
        window.MoveAndResize(new RectInt32(
            work.X + Math.Max(0, (work.Width - width) / 2),
            work.Y + Math.Max(0, (work.Height - height) / 2), width, height));
        window.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (window.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        return window;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);

    private bool English => _settings.Current.Language == AppLanguage.English;

    private string T(string key) => (English, key) switch
    {
        (true, "Settings") => "Settings", (true, "Scenarios") => "Scenarios",
        (true, "General") => "General", (true, "Devices") => "Device names",
        (true, "NewScenario") => "New scenario", (true, "StartupScenario") => "Scenario at startup",
        (true, "LastLoaded") => "Last loaded", (true, "Hotkey") => "Hotkey",
        (true, "Change") => "Change…", (true, "Autostart") => "Autostart",
        (true, "Theme") => "Theme", (true, "Language") => "Language",
        (true, "Behavior") => "Behavior", (true, "System") => "System",
        (true, "AdaptiveRemoteSession") => "Adaptive remote session",
        (true, "RemoteSession") => "Remote session", (true, "RemoteAudioVolume") => "Remote audio volume",
        (true, "DisplaySettings") => "Display settings", (true, "SoundSettings") => "Sound settings",
        (true, "Open") => "Open…",
        (true, "WindowsSettings") => "Windows settings",
        (true, "SystemDisplayName") => "System display name", (true, "SystemAudioName") => "System audio device name",
        (true, "DisplayName") => "Display name",
        (true, "ScenarioDisplays") => "Scenario displays", (true, "ScenarioAudio") => "Scenario audio devices",
        (true, "Monitors") => "Monitors", (true, "AudioDevices") => "Audio devices",
        (true, "DeviceAlias") => "Name in Cozy Roomswitch", (true, "ScenarioName") => "Name the scenario",
        (true, "ScenarioSettings") => "Scenario name and icon", (true, "Monitor") => "Monitor", (true, "Resolution") => "Resolution",
        (true, "IncludeInHotkeyList") => "Add to hotkey list",
        (true, "Audio") => "Audio device", (true, "Volume") => "Volume",
        (true, "SetVolume") => "Set volume",
        (true, "Sound") => "Sound", (true, "TrayIcon") => "Tray icon",
        (true, "ScenarioIcon") => "Scenario icon", (true, "Letters") => "Letters",
        (true, "None") => "None", (true, "NoChange") => "Don't change",
        (true, "Desktop") => "Computer", (true, "Television") => "Television",
        (true, "Sofa") => "Sofa", (true, "Gamepad") => "Gamepad",
        (true, "Close") => "Close", (true, "Save") => "Save", (true, "Delete") => "Delete",
        (true, "FooterVersion") => "Cozy Roomswitch 1.0.5",
        (false, "Settings") => "Настройки", (false, "Scenarios") => "Сценарии",
        (false, "General") => "Основные", (false, "Devices") => "Имена устройств",
        (false, "NewScenario") => "Новый сценарий", (false, "StartupScenario") => "Сценарий при запуске",
        (false, "LastLoaded") => "Последний загруженный", (false, "Hotkey") => "Горячая клавиша",
        (false, "Change") => "Изменить…", (false, "Autostart") => "Автозапуск",
        (false, "Theme") => "Тема", (false, "Language") => "Язык",
        (false, "Behavior") => "Поведение", (false, "System") => "Система",
        (false, "AdaptiveRemoteSession") => "Адаптивный удаленный сеанс",
        (false, "RemoteSession") => "Удаленный сеанс", (false, "RemoteAudioVolume") => "Громкость удаленного аудио",
        (false, "DisplaySettings") => "Параметры экрана", (false, "SoundSettings") => "Параметры звука",
        (false, "Open") => "Открыть…",
        (false, "WindowsSettings") => "Параметры Windows",
        (false, "SystemDisplayName") => "Системное имя экрана", (false, "SystemAudioName") => "Системное имя аудиоустройства",
        (false, "DisplayName") => "Отображаемое имя",
        (false, "ScenarioDisplays") => "Экраны сценария", (false, "ScenarioAudio") => "Аудиоустройства сценария",
        (false, "Monitors") => "Мониторы", (false, "AudioDevices") => "Аудиоустройства",
        (false, "DeviceAlias") => "Имя в Cozy Roomswitch", (false, "ScenarioName") => "Назовите сценарий",
        (false, "ScenarioSettings") => "Имя и иконка сценария", (false, "Monitor") => "Монитор", (false, "Resolution") => "Разрешение",
        (false, "IncludeInHotkeyList") => "Добавить в hotkey-список",
        (false, "Audio") => "Аудиоустройство", (false, "Volume") => "Громкость",
        (false, "SetVolume") => "Установить громкость",
        (false, "Sound") => "Звук", (false, "TrayIcon") => "Иконка в трее",
        (false, "ScenarioIcon") => "Иконка сценария", (false, "Letters") => "Литеры",
        (false, "None") => "Нет", (false, "NoChange") => "Не менять",
        (false, "Desktop") => "Компьютер", (false, "Television") => "Телевизор",
        (false, "Sofa") => "Диван", (false, "Gamepad") => "Геймпад",
        (false, "Close") => "Закрыть", (false, "Save") => "Сохранить", (false, "Delete") => "Удалить",
        (false, "FooterVersion") => "Cozy Roomswitch 1.0.5",
        _ => key
    };

    private void BuildShell()
    {
        // A reused control must leave its old parent before rebuilding the visual tree.
        if (_pageHost.Parent is Panel previousParent) previousParent.Children.Remove(_pageHost);
        _root.Children.Clear();
        _navButtons.Clear();
        _root.RowDefinitions.Clear();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var main = new Grid();
        // The visible navigation cell is 244 px wide (280 minus its 20/16 margins),
        // exactly matching the value-control column used by every settings row.
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.Children.Add(BuildNavigation());
        Grid.SetColumn(_pageHost, 1);
        main.Children.Add(_pageHost);
        Grid.SetRow(main, 0);

        var footer = new Grid { Margin = new Thickness(20, 10, 56, 18) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var footerInfo = new StackPanel { Spacing = 0, Opacity = .68, Margin = new Thickness(8, 0, 0, 0) };
        footerInfo.Children.Add(new TextBlock { Text = T("FooterVersion") });
        var sourceLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sourceLine.Children.Add(new TextBlock { Text = "sol669 ·", VerticalAlignment = VerticalAlignment.Center });
        sourceLine.Children.Add(new HyperlinkButton
        {
            Content = "GitHub",
            NavigateUri = new Uri("https://github.com/sol669/Cozy-Roomswitch"),
            Padding = new Thickness(0)
        });
        footerInfo.Children.Add(sourceLine);
        footer.Children.Add(footerInfo);

        _deleteButton = StyledButton(T("Delete"), "RoomDefaultButtonStyle", 110);
        _deleteButton.HorizontalAlignment = HorizontalAlignment.Left;
        _deleteButton.Margin = new Thickness(24, 0, 0, 0);
        _deleteButton.Click += async (_, _) => await DeleteScenarioAsync();
        Grid.SetColumn(_deleteButton, 1);
        footer.Children.Add(_deleteButton);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        Button close = StyledButton(T("Close"), "RoomDefaultButtonStyle", 110);
        close.Click += async (_, _) => await RequestCloseAsync();
        _saveButton = StyledButton(T("Save"), "RoomAccentButtonStyle", 110);
        _saveButton.Click += async (_, _) => await SaveCurrentPageAsync();
        actions.Children.Add(close);
        actions.Children.Add(_saveButton);
        Grid.SetColumn(actions, 2);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 1);

        _root.Children.Clear();
        _root.Children.Add(main);
        _root.Children.Add(footer);
        _root.KeyDown -= RootKeyDown;
        _root.KeyDown += RootKeyDown;
        Content = _root;
        ShowCurrentPage();
        ApplyTheme();
        UpdateFooterState();
    }

    private UIElement BuildNavigation()
    {
        var navigation = new Grid { Margin = new Thickness(20, ContentTopMargin, 16, 12) };
        navigation.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navigation.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var settingsGroup = new StackPanel { Spacing = 5 };
        settingsGroup.Children.Add(SettingsTitleCell(T("Settings")));
        settingsGroup.Children.Add(NavButton(T("General"), "general"));
        settingsGroup.Children.Add(NavButton(T("Devices"), "devices"));
        settingsGroup.Children.Add(NavigationSeparatorCell());
        navigation.Children.Add(settingsGroup);

        _scenarioNavPanel = new StackPanel { Spacing = 5, Margin = new Thickness(0, 5, 0, 0) };
        var scenarioScroll = new ScrollViewer
        {
            Content = _scenarioNavPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scenarioScroll, 1);
        navigation.Children.Add(scenarioScroll);
        RebuildScenarioNavigation();
        return navigation;
    }

    private static Border NavigationSeparatorCell() => new()
    {
        Height = 46,
        Child = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(55, 128, 128, 128)),
            Margin = new Thickness(8, 0, 8, 0)
        }
    };

    private static Border SettingsTitleCell(string text) => new()
    {
        Height = TitleBandHeight,
        Child = new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        }
    };

    private Button NavButton(string text, string page)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
            Height = 46,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0)
        };
        _navButtons[page] = button;
        button.Click += async (_, _) => await RequestNavigateAsync(page);
        return button;
    }

    private void RebuildScenarioNavigation()
    {
        if (_scenarioNavPanel is null) return;
        foreach (string key in _navButtons.Keys.Where(key => key.StartsWith("scenario:", StringComparison.Ordinal)).ToList())
            _navButtons.Remove(key);
        _navButtons.Remove("remote");
        _navButtons.Remove("new");
        _scenarioNavPanel.Children.Clear();
        if (_settings.Current.AdaptiveRemoteSession)
        {
            Button remote = NavButton(T("RemoteSession"), "remote");
            remote.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var remoteContent = new Grid { ColumnSpacing = 12 };
            remoteContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            remoteContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            remoteContent.Children.Add(new TextBlock { Text = T("RemoteSession"), VerticalAlignment = VerticalAlignment.Center });
            var remoteIcon = new ScenarioIconView { Remote = true, VerticalAlignment = VerticalAlignment.Center };
            remoteIcon.SetBinding(Control.ForegroundProperty, new Binding { Source = remote, Path = new PropertyPath(nameof(Control.Foreground)) });
            Grid.SetColumn(remoteIcon, 1);
            remoteContent.Children.Add(remoteIcon);
            remote.Content = remoteContent;
            AutomationProperties.SetName(remote, T("RemoteSession"));
            _scenarioNavPanel.Children.Add(remote);
        }
        foreach (ScenarioDefinition scenario in _scenarios)
        {
            string page = ScenarioPage(scenario.Id);
            Button button = NavButton(scenario.Name, page);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var content = new Grid { ColumnSpacing = 12 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            var label = new TextBlock
            {
                Text = scenario.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            content.Children.Add(label);
            var icon = new ScenarioIconView
            {
                Icon = scenario.Icon,
                Letters = string.IsNullOrWhiteSpace(scenario.IconLetters) ? scenario.Name : scenario.IconLetters,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetBinding(Control.ForegroundProperty,
                new Binding { Source = button, Path = new PropertyPath(nameof(Control.Foreground)) });
            Grid.SetColumn(icon, 1);
            content.Children.Add(icon);
            button.Content = content;
            AutomationProperties.SetName(button, scenario.Name);
            _scenarioNavPanel.Children.Add(button);
        }
        _scenarioNavPanel.Children.Add(NavButton("+ " + T("NewScenario"), "new"));
        RefreshNavigationSelection();
    }

    private async Task RequestNavigateAsync(string page)
    {
        if (page == _currentPage) return;
        if (!await ConfirmLeaveCurrentPageAsync()) return;
        NavigateCore(page);
    }

    private void NavigateCore(string page)
    {
        _currentPage = page;
        _capturingHotKey = false;
        if (page == "general") ResetPendingGeneral();
        else if (page == "devices") ResetPendingAliases();
        else if (page == "remote") ResetPendingRemote();
        else if (page == "new") CreateNewDraft();
        else if (TryScenarioId(page, out Guid id)) LoadScenarioDraft(id);
        ShowCurrentPage();
        RefreshNavigationSelection();
        UpdateFooterState();
        _root.DispatcherQueue.TryEnqueue(RefreshNavigationSelection);
    }

    private void ShowCurrentPage()
    {
        _pageHost.Content = _currentPage switch
        {
            "devices" => BuildDevicesPage(),
            "remote" => BuildRemotePage(),
            "new" => BuildScenarioPage(),
            _ when _currentPage.StartsWith("scenario:", StringComparison.Ordinal) => BuildScenarioPage(),
            _ => BuildGeneralPage()
        };
    }

    private void RefreshNavigationSelection()
    {
        foreach ((string page, Button button) in _navButtons)
        {
            bool selected = page == _currentPage;
            string style = selected ? "RoomSelectedNavigationButtonStyle" : "RoomNavigationButtonStyle";
            button.Style = (Style)Application.Current.Resources[style];
        }
    }

    private UIElement BuildGeneralPage()
    {
        _loading = true;
        StackPanel panel = PagePanel();
        panel.Children.Add(TitleSpacerCell());
        panel.Children.Add(HeaderCell(T("Behavior")));
        _startupChoiceBox = SettingsComboBox(new ComboBox { DisplayMemberPath = "Name" });
        var startupChoices = new List<StartupChoice> { new(null, T("LastLoaded")) };
        startupChoices.AddRange(_scenarios.Where(s => s.IsComplete).Select(s => new StartupChoice(s.Id, s.Name)));
        _startupChoiceBox.ItemsSource = startupChoices;
        _startupChoiceBox.SelectedIndex = _pendingStartupMode == StartupScenarioMode.AlwaysUseScenario
            ? Math.Max(0, startupChoices.FindIndex(choice => choice.Id == _pendingStartupScenarioId)) : 0;
        _startupChoiceBox.SelectionChanged += (_, _) =>
        {
            if (_loading || _startupChoiceBox.SelectedItem is not StartupChoice choice) return;
            _pendingStartupMode = choice.Id is null ? StartupScenarioMode.RestoreLastScenario : StartupScenarioMode.AlwaysUseScenario;
            _pendingStartupScenarioId = choice.Id;
            UpdateFooterState();
        };
        panel.Children.Add(SettingRow(T("StartupScenario"), _startupChoiceBox));

        _hotKeyCaptureButton = SettingsButton(new Button { Content = T("Change"), MinWidth = 110 });
        _hotKeyCaptureButton.Click += (_, _) => BeginCapture();
        _hotKeyTitle = new TextBlock
        {
            Text = $"{T("Hotkey")} — {TrayService.FormatHotKey(_pendingHotKey)}",
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(SettingRow(_hotKeyTitle, _hotKeyCaptureButton));
        _hotKeyHint = new TextBlock { Opacity = .68, Margin = new Thickness(14, 0, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(_hotKeyHint);

        _adaptiveRemoteToggle = new ToggleSwitch
        {
            IsOn = _pendingAdaptiveRemoteSession, MinWidth = 0, Width = 44,
            OffContent = string.Empty, OnContent = string.Empty, VerticalAlignment = VerticalAlignment.Center
        };
        _adaptiveRemoteState = new TextBlock { MinWidth = 58, TextAlignment = TextAlignment.Right, Opacity = .72, VerticalAlignment = VerticalAlignment.Center };
        _adaptiveRemoteToggle.Toggled += (_, _) =>
        {
            UpdateAdaptiveRemoteState();
            if (_loading) return;
            _pendingAdaptiveRemoteSession = _adaptiveRemoteToggle.IsOn;
            UpdateFooterState();
        };
        UpdateAdaptiveRemoteState();
        panel.Children.Add(ToggleSettingRow(T("AdaptiveRemoteSession"), _adaptiveRemoteState, _adaptiveRemoteToggle));

        panel.Children.Add(HeaderCell(T("System")));

        _autostartToggle = new ToggleSwitch
        {
            IsOn = _pendingStartWithWindows,
            MinWidth = 0,
            Width = 44,
            OffContent = string.Empty,
            OnContent = string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };
        _autostartState = new TextBlock
        {
            MinWidth = 58,
            TextAlignment = TextAlignment.Right,
            Opacity = .72,
            VerticalAlignment = VerticalAlignment.Center
        };
        _autostartToggle.Toggled += (_, _) =>
        {
            UpdateAutostartState();
            if (_loading) return;
            _pendingStartWithWindows = _autostartToggle.IsOn;
            UpdateFooterState();
        };
        UpdateAutostartState();
        panel.Children.Add(ToggleSettingRow(T("Autostart"), _autostartState, _autostartToggle));

        ComboBox theme = SettingsComboBox(new ComboBox
        {
            ItemsSource = English ? new[] { "Like Windows", "Light", "Dark" } : new[] { "Как в Windows", "Светлая", "Тёмная" },
            SelectedIndex = (int)_pendingTheme
        });
        theme.SelectionChanged += (_, _) =>
        {
            if (!_loading && theme.SelectedIndex >= 0) { _pendingTheme = (AppThemeMode)theme.SelectedIndex; UpdateFooterState(); }
        };
        panel.Children.Add(SettingRow(T("Theme"), theme));

        ComboBox language = SettingsComboBox(new ComboBox
        {
            ItemsSource = new[] { "Русский", "English" },
            SelectedIndex = (int)_pendingLanguage
        });
        language.SelectionChanged += (_, _) =>
        {
            if (!_loading && language.SelectedIndex >= 0) { _pendingLanguage = (AppLanguage)language.SelectedIndex; UpdateFooterState(); }
        };
        panel.Children.Add(SettingRow(T("Language"), language));
        panel.Children.Add(HeaderCell(T("WindowsSettings")));
        panel.Children.Add(SystemSettingsRow(T("DisplaySettings"), "ms-settings:display"));
        panel.Children.Add(SystemSettingsRow(T("SoundSettings"), "ms-settings:sound"));
        FinishInitialLoading(panel);
        return PageScroll(panel);
    }

    private UIElement BuildRemotePage()
    {
        _loading = true;
        StackPanel panel = PagePanel();
        panel.Children.Add(TitleSpacerCell());
        panel.Children.Add(HeaderCell(T("RemoteSession")));
        TextBox volume = SettingsTextBox(new TextBox { Text = _remoteVolumeInput.Text, PlaceholderText = "0–100" });
        volume.InputScope = new InputScope { Names = { new InputScopeName { NameValue = InputScopeNameValue.Number } } };
        volume.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _remoteVolumeInput.Text = volume.Text;
            if (_remoteVolumeInput.IsValid) _pendingRemoteVolume = _remoteVolumeInput.Value ?? 100;
            volume.BorderBrush = _remoteVolumeInput.IsValid ? null : ThemeBrush("SystemFillColorCriticalBrush", Colors.Red);
            volume.BorderThickness = _remoteVolumeInput.IsValid ? new Thickness(0) : new Thickness(1);
            UpdateFooterState();
        };
        panel.Children.Add(SettingRow(T("RemoteAudioVolume"), volume));
        FinishInitialLoading(panel);
        return PageScroll(panel);
    }

    private UIElement BuildDevicesPage()
    {
        _loading = true;
        StackPanel panel = PagePanel();
        panel.Children.Add(TitleSpacerCell());
        AddDeviceAliasRows(panel, T("SystemDisplayName"), _displays.Select(display => (display.Id, display.Name))
            .Concat(_scenarios.SelectMany(item => item.DisplayIds)
                .Select(id => (id, _settings.Current.KnownDeviceNames.GetValueOrDefault(id) ?? T("Monitor")))));
        AddDeviceAliasRows(panel, T("SystemAudioName"), _audio.Select(audio => (audio.Id, audio.Name))
            .Concat(_scenarios.Where(item => !string.IsNullOrWhiteSpace(item.AudioDeviceId))
                .Select(item => (item.AudioDeviceId, _settings.Current.KnownDeviceNames.GetValueOrDefault(item.AudioDeviceId) ?? T("Audio")))));
        FinishInitialLoading(panel);
        return PageScroll(panel);
    }

    private UIElement BuildScenarioPage()
    {
        _loading = true;
        StackPanel panel = PagePanel();
        panel.Children.Add(TitleSpacerCell());
        if (_draft is null)
        {
            FinishInitialLoading(panel);
            return PageScroll(panel);
        }

        panel.Children.Add(HeaderCell(T("ScenarioSettings")));
        TextBox name = SettingsTextBox(new TextBox { Text = _draft.Name, MaxLength = 80 });
        name.TextChanged += (_, _) =>
        {
            if (_loading || _draft is null) return;
            _draft.Name = name.Text;
            UpdateFooterState();
        };
        panel.Children.Add(SettingRow(T("ScenarioName"), name));

        var hotkeyState = new TextBlock
        {
            MinWidth = 58, TextAlignment = TextAlignment.Right, Opacity = .72,
            VerticalAlignment = VerticalAlignment.Center,
            Text = _draft.IncludeInHotkeyList ? (English ? "On" : "Вкл.") : (English ? "Off" : "Откл.")
        };
        var hotkeyToggle = new ToggleSwitch
        {
            IsOn = _draft.IncludeInHotkeyList, MinWidth = 0, Width = 44,
            OffContent = string.Empty, OnContent = string.Empty, VerticalAlignment = VerticalAlignment.Center
        };
        hotkeyToggle.Toggled += (_, _) =>
        {
            if (_loading || _draft is null) return;
            _draft.IncludeInHotkeyList = hotkeyToggle.IsOn;
            hotkeyState.Text = hotkeyToggle.IsOn ? (English ? "On" : "Вкл.") : (English ? "Off" : "Откл.");
            UpdateFooterState();
        };
        panel.Children.Add(ToggleSettingRow(T("IncludeInHotkeyList"), hotkeyState, hotkeyToggle));

        var iconPicker = new ScenarioIconPicker(_draft.Icon, English);
        panel.Children.Add(SettingRow(T("ScenarioIcon"), iconPicker));
        TextBox letters = SettingsTextBox(new TextBox { Text = _draft.IconLetters, MaxLength = 3 });
        Border lettersRow = SettingRow(T("Letters"), letters);
        lettersRow.Visibility = _draft.Icon == ScenarioIcon.Letters ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(lettersRow);
        iconPicker.SelectionChanged += (_, _) =>
        {
            if (_loading || _draft is null) return;
            _draft.Icon = iconPicker.SelectedIcon;
            lettersRow.Visibility = _draft.Icon == ScenarioIcon.Letters ? Visibility.Visible : Visibility.Collapsed;
            UpdateFooterState();
        };
        letters.TextChanged += (_, _) =>
        {
            if (_loading || _draft is null) return;
            string normalized = ScenarioDefinition.MakeIconLetters(letters.Text);
            if (!string.Equals(letters.Text, normalized, StringComparison.Ordinal))
            {
                letters.Text = normalized;
                letters.SelectionStart = normalized.Length;
            }
            _draft.IconLetters = normalized;
            UpdateFooterState();
        };

        var displayHeader = new Grid { ColumnSpacing = 10, Margin = new Thickness(10, 0, 10, 0) };
        displayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        displayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        displayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        displayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var displayNameHeader = new TextBlock { Text = English ? "Display" : "Экран", Opacity = .52,
            VerticalAlignment = VerticalAlignment.Center };
        var resolutionHeader = new TextBlock { Text = English ? "Resolution" : "Разрешение", Opacity = .52,
            VerticalAlignment = VerticalAlignment.Center };
        var scaleHeader = new TextBlock { Text = English ? "Scale" : "Масштаб", Opacity = .52,
            VerticalAlignment = VerticalAlignment.Center };
        var numberHeader = new TextBlock { Text = "№", Opacity = .52,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        displayHeader.Children.Add(numberHeader);
        Grid.SetColumn(displayNameHeader, 1); displayHeader.Children.Add(displayNameHeader);
        Grid.SetColumn(resolutionHeader, 2); displayHeader.Children.Add(resolutionHeader);
        Grid.SetColumn(scaleHeader, 3); displayHeader.Children.Add(scaleHeader);
        panel.Children.Add(new Border { Height = 46, Child = displayHeader });

        var displayRows = new StackPanel { Spacing = 8 };
        for (int index = 0; index < 4; index++)
        {
            int slot = index;
            ComboBox monitor = SettingsComboBox(new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id" });
            monitor.Width = double.NaN;
            monitor.Tag = slot;
            BindMonitorBox(monitor, slot);
            ComboBox resolution = SettingsComboBox(new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Preset" });
            resolution.Width = double.NaN;
            IReadOnlyList<ResolutionChoice> resolutionChoices = ResolutionChoices();
            resolution.ItemsSource = resolutionChoices;
            string initialId = _draftMonitorSlots[slot] ?? string.Empty;
            DisplayResolutionPreset initialPreset = string.IsNullOrWhiteSpace(initialId) ? DisplayResolutionPreset.KeepCurrent :
                _draft.DisplayResolutionPresets.GetValueOrDefault(initialId, DisplayResolutionPreset.KeepCurrent);
            resolution.SelectedItem = resolutionChoices.FirstOrDefault(item => item.Preset == initialPreset) ?? resolutionChoices[0];
            resolution.IsEnabled = !string.IsNullOrWhiteSpace(initialId);

            ComboBox scale = SettingsComboBox(new ComboBox { DisplayMemberPath = "Name" });
            scale.Width = double.NaN;
            IReadOnlyList<ScaleChoice> scaleChoices = ScaleChoices();
            scale.ItemsSource = scaleChoices;
            int? initialScale = string.IsNullOrWhiteSpace(initialId) ? null :
                _draft.DisplayScalePercents.GetValueOrDefault(initialId);
            scale.SelectedItem = scaleChoices.FirstOrDefault(item => item.Percent == initialScale) ?? scaleChoices[0];
            scale.IsEnabled = !string.IsNullOrWhiteSpace(initialId);
            monitor.SelectionChanged += (_, _) =>
            {
                if (_loading) return;
                _draftMonitorSlots[slot] = monitor.SelectedValue as string ?? string.Empty;
                CaptureMonitorSlots();
                RefreshScenarioPagePreservingFocus();
            };
            resolution.SelectionChanged += (_, _) =>
            {
                if (_loading || _draft is null || string.IsNullOrWhiteSpace(_draftMonitorSlots[slot])) return;
                DisplayResolutionPreset preset = (resolution.SelectedItem as ResolutionChoice)?.Preset
                    ?? DisplayResolutionPreset.KeepCurrent;
                string id = _draftMonitorSlots[slot];
                if (preset == DisplayResolutionPreset.KeepCurrent) _draft.DisplayResolutionPresets.Remove(id);
                else _draft.DisplayResolutionPresets[id] = preset;
                UpdateFooterState();
            };
            scale.SelectionChanged += (_, _) =>
            {
                if (_loading || _draft is null || string.IsNullOrWhiteSpace(_draftMonitorSlots[slot])) return;
                int? percent = (scale.SelectedItem as ScaleChoice)?.Percent;
                string id = _draftMonitorSlots[slot];
                if (percent.HasValue) _draft.DisplayScalePercents[id] = percent.Value;
                else _draft.DisplayScalePercents.Remove(id);
                UpdateFooterState();
            };
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = (index + 1).ToString(), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Opacity = .7 });
            Grid.SetColumn(monitor, 1); row.Children.Add(monitor);
            Grid.SetColumn(resolution, 2); row.Children.Add(resolution);
            Grid.SetColumn(scale, 3); row.Children.Add(scale);
            displayRows.Children.Add(row);
        }
        panel.Children.Add(DisplaySettingsGroup(displayRows));

        panel.Children.Add(HeaderCell(T("ScenarioAudio")));
        ComboBox audio = SettingsComboBox(new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id" });
        var audioChoices = new List<Choice> { new(string.Empty, T("None")) };
        audioChoices.AddRange(_audio.Select(device => new Choice(device.Id,
            DeviceAliasService.NameFor(PendingAliasSettings(), device.Id, device.DisplayName ?? device.Name) +
            (device.IsActive ? "" : device.State == AudioDeviceState.Disabled
                ? (English ? " — disabled in Windows" : " — отключено в Windows")
                : (English ? " — not connected" : " — не подключён")))));
        if (!string.IsNullOrWhiteSpace(_draft.AudioDeviceId) &&
            audioChoices.All(item => !ScenarioPolicy.Same(item.Id, _draft.AudioDeviceId)))
            audioChoices.Add(new Choice(_draft.AudioDeviceId, SavedDeviceName(_draft.AudioDeviceId, T("Audio")) +
                (English ? " — not connected" : " — не подключён")));
        audio.ItemsSource = audioChoices;
        audio.SelectedValue = _draft.AudioDeviceId;
        audio.SelectionChanged += (_, _) =>
        {
            if (_loading || _draft is null) return;
            _draft.AudioDeviceId = audio.SelectedValue as string ?? string.Empty;
            _draft.AudioDeviceContainerId = _audio.FirstOrDefault(item => item.Id == _draft.AudioDeviceId)
                ?.ContainerId?.ToString("D") ?? string.Empty;
            UpdateFooterState();
        };
        panel.Children.Add(SettingRow(T("Audio"), audio));

        ComboBox volume = SettingsComboBox(new ComboBox
        {
            ItemsSource = new[] { T("NoChange"), T("SetVolume") },
            SelectedIndex = _volumeInput.Enabled ? 1 : 0
        });
        StartupVolumeInput input = _volumeInput;
        TextBox percent = SettingsTextBox(new TextBox { Text = input.Text, PlaceholderText = "0–100" });
        percent.InputScope = new InputScope
        {
            Names = { new InputScopeName { NameValue = InputScopeNameValue.Number } }
        };
        AutomationProperties.SetName(percent, T("SetVolume") + " (0–100%)");
        Border percentRow = SettingRow(T("SetVolume"), percent);
        percentRow.Visibility = input.Enabled ? Visibility.Visible : Visibility.Collapsed;
        void CaptureVolume()
        {
            if (_loading || _draft is null) return;
            if (input.IsValid) _draft.VolumePercent = input.Value;
            percent.BorderBrush = input.IsValid ? null : ThemeBrush("SystemFillColorCriticalBrush", Colors.Red);
            percent.BorderThickness = input.IsValid ? new Thickness(0) : new Thickness(1);
            UpdateFooterState();
        }
        volume.SelectionChanged += (_, _) =>
        {
            if (_loading || volume.SelectedIndex < 0) return;
            input.Enabled = volume.SelectedIndex == 1;
            percentRow.Visibility = input.Enabled ? Visibility.Visible : Visibility.Collapsed;
            CaptureVolume();
        };
        percent.TextChanged += (_, _) => { input.Text = percent.Text; CaptureVolume(); };
        panel.Children.Add(SettingRow(T("Volume"), volume));
        panel.Children.Add(percentRow);

        FinishInitialLoading(panel);
        return PageScroll(panel);
    }

    private void BindMonitorBox(ComboBox box, int slot)
    {
        string own = _draftMonitorSlots[slot] ?? string.Empty;
        HashSet<string> used = _draftMonitorSlots
            .Where((id, index) => index != slot && !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = new List<Choice> { new(string.Empty, T("None")) };
        choices.AddRange(_displays
            .Where(device => device.Id.Equals(own, StringComparison.OrdinalIgnoreCase) || !used.Contains(device.Id))
            .Select(device => new Choice(device.Id,
                DeviceAliasService.NameFor(PendingAliasSettings(), device.Id, device.Name))));
        if (!string.IsNullOrWhiteSpace(own) && choices.All(item => !ScenarioPolicy.Same(item.Id, own)))
            choices.Add(new Choice(own, SavedDeviceName(own, T("Monitor")) +
                (English ? " — not connected" : " — не подключён")));
        box.ItemsSource = choices;
        box.SelectedValue = own;
    }

    private void CaptureMonitorSlots()
    {
        if (_draft is null) return;
        _draft.DisplayIds = _draftMonitorSlots
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string stale in _draft.DisplayResolutionPresets.Keys
            .Where(id => !_draft.DisplayIds.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray())
            _draft.DisplayResolutionPresets.Remove(stale);
        foreach (string stale in _draft.DisplayScalePercents.Keys
            .Where(id => !_draft.DisplayIds.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray())
            _draft.DisplayScalePercents.Remove(stale);
    }

    private IReadOnlyList<ResolutionChoice> ResolutionChoices() =>
        [new(DisplayResolutionPreset.KeepCurrent, T("NoChange")),
         new(DisplayResolutionPreset.UltraHd4K, "4K · 3840 × 2160"),
         new(DisplayResolutionPreset.QuadHd2K, "2K · 2560 × 1440"),
         new(DisplayResolutionPreset.FullHd1080, "1080p · 1920 × 1080"),
         new(DisplayResolutionPreset.Hd720, "720p · 1280 × 720")];

    private IReadOnlyList<ScaleChoice> ScaleChoices() =>
        [new(null, T("NoChange")), new(100, "100%"), new(125, "125%"),
         new(150, "150%"), new(175, "175%"), new(200, "200%")];

    private void RefreshScenarioPagePreservingFocus()
    {
        ShowCurrentPage();
        UpdateFooterState();
    }

    private void AddDeviceAliasRows(StackPanel panel, string heading, IEnumerable<(string Id, string SystemName)> devices)
    {
        List<(string Id, string SystemName)> items = devices
            .Where(device => !string.IsNullOrWhiteSpace(device.Id))
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        panel.Children.Add(DeviceColumnHeader(heading));
        if (items.Count == 0) return;
        foreach ((string id, string systemName) in items)
        {
            TextBox alias = SettingsTextBox(new TextBox
            {
                Text = _pendingAliases.TryGetValue(id, out string? value) && !string.IsNullOrWhiteSpace(value)
                    ? value : systemName
            });
            alias.TextChanged += (_, _) =>
            {
                if (_loading) return;
                string value = alias.Text.Trim();
                if (string.IsNullOrWhiteSpace(value) || value == systemName) _pendingAliases.Remove(id);
                else _pendingAliases[id] = value;
                UpdateFooterState();
            };
            alias.LostFocus += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(alias.Text)) alias.Text = systemName;
            };
            AutomationProperties.SetName(alias, T("DisplayName") + ": " + systemName);
            var title = new TextBlock
            {
                Text = systemName,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            panel.Children.Add(SettingRow(title, alias));
        }
    }

    private Border DeviceColumnHeader(string systemHeading)
    {
        // Move only the left heading; the right column retains its original x.
        var grid = new Grid { Margin = new Thickness(2, 0, 15, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueColumnWidth) });
        grid.Children.Add(new TextBlock
        {
            Text = systemHeading, Opacity = .52, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var displayName = new TextBlock
        {
            Text = T("DisplayName"), Opacity = .52, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(displayName, 1);
        grid.Children.Add(displayName);
        return new Border { Height = 46, Child = grid };
    }

    private static StackPanel PagePanel() => new()
    {
        Width = 540,
        HorizontalAlignment = HorizontalAlignment.Left,
        Spacing = 5,
        // 12 scenario rows + 12 gaps + title + top margin = 684 logical px.
        // Keep the normal 820px window usable without shrinking its 46px cards.
        Margin = new Thickness(24, ContentTopMargin, 0, 0)
    };

    private static ScrollViewer PageScroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(0)
    };

    private static Border HeaderCell(string text)
    {
        var cell = new Border { Height = 46 };
        if (!string.IsNullOrWhiteSpace(text))
        {
            cell.Child = new TextBlock
            {
                Text = text,
                Opacity = .52,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            };
        }
        return cell;
    }

    private static Border TitleSpacerCell() => new() { Height = TitleBandHeight };

    private void FinishInitialLoading(FrameworkElement element)
    {
        element.Loaded += (_, _) =>
        {
            _loading = false;
            UpdateFooterState();
        };
    }

    private ComboBox SettingsComboBox(ComboBox comboBox)
    {
        comboBox.Width = ValueColumnWidth;
        comboBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        ApplyControlStyle(comboBox, "RoomSettingsComboBoxStyle");
        return comboBox;
    }

    private TextBox SettingsTextBox(TextBox textBox)
    {
        textBox.Width = ValueColumnWidth;
        textBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        ApplyControlStyle(textBox, "RoomDeviceAliasTextBoxStyle");
        return textBox;
    }

    private Button SettingsButton(Button button)
    {
        ApplyControlStyle(button, "RoomHotkeyButtonStyle");
        button.Width = ValueColumnWidth;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    private Border SystemSettingsRow(string title, string uri)
    {
        Button open = SettingsButton(new Button { Content = T("Open") });
        open.Click += async (_, _) =>
        {
            try
            {
                if (await Launcher.LaunchUriAsync(new Uri(uri))) return;
                throw new InvalidOperationException("Windows Settings did not open: " + uri);
            }
            catch (Exception ex)
            {
                SettingsStore.Log(ex);
                if (_dialogOpen || _root.XamlRoot is null) return;
                _dialogOpen = true;
                try
                {
                    await new ContentDialog
                    {
                        XamlRoot = _root.XamlRoot,
                        Title = title,
                        Content = English ? "Unable to open Windows Settings." : "Не удалось открыть параметры Windows.",
                        CloseButtonText = T("Close")
                    }.ShowAsync();
                }
                finally { _dialogOpen = false; }
            }
        };
        return SettingRow(title, open);
    }

    private static Button StyledButton(string text, string style, double minWidth)
    {
        var button = new Button { Content = text, MinWidth = minWidth };
        ApplyControlStyle(button, style);
        return button;
    }

    private Border SettingRow(string title, FrameworkElement control) =>
        SettingRow(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center }, control);

    private Border SettingRow(FrameworkElement title, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(title);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return SettingsCard(grid);
    }

    private Border ToggleSettingRow(string title, TextBlock state, ToggleSwitch toggle)
    {
        state.HorizontalAlignment = HorizontalAlignment.Right;
        state.VerticalAlignment = VerticalAlignment.Center;
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        toggle.VerticalAlignment = VerticalAlignment.Center;

        var right = new Grid { Width = ValueColumnWidth, Height = 34, ColumnSpacing = 12 };
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(state, 1);
        Grid.SetColumn(toggle, 2);
        right.Children.Add(state);
        right.Children.Add(toggle);
        return SettingRow(title, right);
    }

    private static Border SettingsCard(Grid content)
    {
        var card = new Border { Child = content };
        ApplyControlStyle(card, "RoomSettingsCardStyle");
        return card;
    }

    private static Border DisplaySettingsGroup(StackPanel content)
    {
        var group = new Border
        {
            Child = content,
            Height = double.NaN,
            Padding = new Thickness(10),
            MinHeight = 0
        };
        ApplyControlStyle(group, "RoomSettingsCardStyle");
        group.Height = double.NaN;
        group.Padding = new Thickness(10);
        return group;
    }

    private static void ApplyControlStyle(FrameworkElement control, string resourceKey)
    {
        if (Application.Current.Resources[resourceKey] is Style style) control.Style = style;
    }

    private void UpdateAutostartState()
    {
        if (_autostartState is null || _autostartToggle is null) return;
        _autostartState.Text = _autostartToggle.IsOn ? (English ? "On" : "Вкл.") : (English ? "Off" : "Откл.");
    }

    private void UpdateAdaptiveRemoteState()
    {
        if (_adaptiveRemoteState is null || _adaptiveRemoteToggle is null) return;
        _adaptiveRemoteState.Text = _adaptiveRemoteToggle.IsOn ? (English ? "On" : "Вкл.") : (English ? "Off" : "Откл.");
    }

    private void UpdateFooterState()
    {
        if (_saveButton is not null)
            _saveButton.IsEnabled = !_loading && CanSaveCurrentPage() && HasCurrentChanges();
        if (_deleteButton is not null)
            _deleteButton.Visibility = IsScenarioPage && !_draftIsNew && _scenarios.Count > 1
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool CanSaveCurrentPage() => _currentPage == "remote" ? _remoteVolumeInput.IsValid :
        !IsScenarioPage || _draft?.IsComplete == true && _volumeInput.IsValid;

    private bool HasCurrentChanges() => _currentPage switch
    {
        "general" => HasUnsavedGeneralChanges(),
        "devices" => HasUnsavedAliasChanges(),
        "remote" => _remoteVolumeInput.IsValid && _pendingRemoteVolume != _settings.Current.RemoteSessionVolumePercent,
        _ when IsScenarioPage => _draft is not null && (_draftIsNew || !_volumeInput.IsValid || !ScenarioEquals(_draft, _scenarioBaseline)),
        _ => false
    };

    private bool IsScenarioPage => _currentPage == "new" || _currentPage.StartsWith("scenario:", StringComparison.Ordinal);

    private async Task<bool> SaveCurrentPageAsync()
    {
        if (!CanSaveCurrentPage()) return false;
        if (_currentPage == "general") return SaveGeneral();
        if (_currentPage == "devices") return SaveAliases();
        if (_currentPage == "remote") return SaveRemote();
        if (IsScenarioPage) return await SaveScenarioAsync();
        return false;
    }

    private bool SaveGeneral()
    {
        try
        {
            bool appearanceChanged = _pendingTheme != _settings.Current.Theme ||
                _pendingLanguage != _settings.Current.Language;
            StartupService.SetEnabled(_pendingStartWithWindows);
            _settings.Current.StartWithWindows = _pendingStartWithWindows;
            bool remoteModeChanged = _pendingAdaptiveRemoteSession != _settings.Current.AdaptiveRemoteSession;
            _settings.Current.AdaptiveRemoteSession = _pendingAdaptiveRemoteSession;
            _settings.Current.StartupScenarioMode = _pendingStartupMode;
            _settings.Current.StartupScenarioId = _pendingStartupScenarioId;
            _settings.Current.Theme = _pendingTheme;
            _settings.Current.Language = _pendingLanguage;
            _settings.Current.SwitchScenarioHotKey = CloneHotKey(_pendingHotKey);
            _settings.Save();
            ResetPendingGeneral();
            if (appearanceChanged) BuildShell();
            else { if (remoteModeChanged) RebuildScenarioNavigation(); ApplyTheme(); RefreshNavigationSelection(); UpdateFooterState(); }
            return true;
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            UpdateFooterState();
            return false;
        }
    }

    private bool SaveRemote()
    {
        if (!_remoteVolumeInput.IsValid) return false;
        _settings.Current.RemoteSessionVolumePercent = Math.Clamp(_pendingRemoteVolume, 0, 100);
        _settings.Save();
        ResetPendingRemote();
        UpdateFooterState();
        return true;
    }

    private bool SaveAliases()
    {
        try
        {
            SynchronizeAudioBindings(App.Scenarios.Snapshot);
            _settings.Current.DeviceAliases = new Dictionary<string, string>(_pendingAliases, StringComparer.OrdinalIgnoreCase);
            _settings.Save();
            ResetPendingAliases();
            ShowCurrentPage();
            UpdateFooterState();
            return true;
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            UpdateFooterState();
            return false;
        }
    }

    private async Task<bool> SaveScenarioAsync()
    {
        SynchronizeAudioBindings(App.Scenarios.Snapshot);
        if (_draft?.IsComplete != true || !_volumeInput.IsValid) return false;
        _draft.Name = _draft.Name.Trim();
        _draft.IconLetters = ScenarioDefinition.MakeIconLetters(_draft.IconLetters);
        bool wasActive = !_draftIsNew && _settings.Current.ActiveScenarioId == _draft.Id;
        bool operationalChanged = wasActive && !OperationallyEqual(_draft, _scenarioBaseline);

        if (_draftIsNew)
        {
            _scenarios.Add(_draft.Clone());
            _draftIsNew = false;
        }
        else
        {
            int index = _scenarios.FindIndex(item => item.Id == _draft.Id);
            if (index >= 0) _scenarios[index] = _draft.Clone();
        }
        _settings.Current.Scenarios = _scenarios.Select(item => item.Clone()).ToList();
        _settings.Save();
        _scenarioBaseline = _draft.Clone();
        _currentPage = ScenarioPage(_draft.Id);
        RebuildScenarioNavigation();
        RefreshNavigationSelection();
        UpdateFooterState();

        if (operationalChanged) await PromptApplyScenarioAsync(_draft);
        return true;
    }

    private async Task PromptApplyScenarioAsync(ScenarioDefinition scenario)
    {
        if (_root.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = English ? $"Scenario “{scenario.Name}” saved" : $"Сценарий «{scenario.Name}» сохранён",
            Content = English ? "Apply the updated scenario now?" : "Применить обновлённый сценарий сейчас?",
            PrimaryButtonText = English ? "Apply now" : "Применить сейчас",
            CloseButtonText = English ? "Later" : "Позже",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await _tray.ApplyScenarioAsync(scenario.Id);
    }

    private async Task DeleteScenarioAsync()
    {
        if (_draft is null || _draftIsNew || _scenarios.Count <= 1 || _root.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = English ? $"Delete “{_draft.Name}”?" : $"Удалить сценарий «{_draft.Name}»?",
            Content = English ? "This action cannot be undone." : "Это действие нельзя отменить.",
            PrimaryButtonText = T("Delete"),
            CloseButtonText = English ? "Cancel" : "Отмена",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        Guid deletedId = _draft.Id;
        _scenarios.RemoveAll(item => item.Id == deletedId);
        _settings.Current.Scenarios = _scenarios.Select(item => item.Clone()).ToList();
        if (_settings.Current.ActiveScenarioId == deletedId) _settings.Current.ActiveScenarioId = null;
        if (_settings.Current.StartupScenarioId == deletedId)
        {
            _settings.Current.StartupScenarioId = null;
            _settings.Current.StartupScenarioMode = StartupScenarioMode.RestoreLastScenario;
        }
        _settings.Save();
        _draft = null;
        _scenarioBaseline = null;
        _currentPage = "general";
        ResetPendingGeneral();
        RebuildScenarioNavigation();
        ShowCurrentPage();
        RefreshNavigationSelection();
        UpdateFooterState();
    }

    private async Task RequestCloseAsync()
    {
        if (HasCurrentChanges()) { await ConfirmCloseAsync(); return; }
        _allowClose = true;
        Close();
    }

    private async Task ConfirmCloseAsync()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            if (!await ConfirmLeaveCurrentPageAsync()) return;
            _allowClose = true;
            Close();
        }
        finally { _dialogOpen = false; }
    }

    private async Task<bool> ConfirmLeaveCurrentPageAsync()
    {
        if (!HasCurrentChanges()) return true;
        if (_root.XamlRoot is null) return false;

        if (IsScenarioPage && !CanSaveCurrentPage())
        {
            var invalid = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = _draftIsNew
                    ? (English ? "Cancel scenario creation?" : "Отменить создание сценария?")
                    : (English ? "Discard scenario changes?" : "Отменить изменения сценария?"),
                Content = English ? "The changes you made will be lost." : "Внесённые изменения будут потеряны.",
                PrimaryButtonText = _draftIsNew
                    ? (English ? "Cancel creation" : "Отменить создание")
                    : (English ? "Discard changes" : "Отменить изменения"),
                CloseButtonText = English ? "Continue editing" : "Продолжить редактирование",
                DefaultButton = ContentDialogButton.Close
            };
            if (await invalid.ShowAsync() != ContentDialogResult.Primary) return false;
            ResetCurrentPageChanges();
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = English ? "Save changes?" : "Сохранить изменения?",
            Content = English ? "There are unsaved changes." : "Остались несохранённые изменения.",
            PrimaryButtonText = T("Save"),
            SecondaryButtonText = English ? "Don't save" : "Не сохранять",
            CloseButtonText = English ? "Cancel" : "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) return await SaveCurrentPageAsync();
        if (result == ContentDialogResult.Secondary)
        {
            ResetCurrentPageChanges();
            return true;
        }
        return false;
    }

    private void ResetCurrentPageChanges()
    {
        if (_currentPage == "general")
        {
            _tray.TryUpdateHotKey(_settings.Current.SwitchScenarioHotKey, out _);
            ResetPendingGeneral();
        }
        else if (_currentPage == "devices") ResetPendingAliases();
        else if (_draftIsNew) { _draft = null; _scenarioBaseline = null; }
        else if (_scenarioBaseline is not null)
        {
            _draft = _scenarioBaseline.Clone();
            SetMonitorSlots(_draft);
        }
    }

    private void BeginCapture()
    {
        _capturingHotKey = true;
        if (_hotKeyCaptureButton is not null)
            _hotKeyCaptureButton.Content = English ? "Press shortcut…" : "Нажмите сочетание…";
        if (_hotKeyHint is not null)
        {
            _hotKeyHint.Text = English
                ? "Use Ctrl, Alt, Shift or Win with another key."
                : "Нажмите Ctrl, Alt, Shift или Win вместе с другой клавишей.";
            _hotKeyHint.Visibility = Visibility.Visible;
        }
        _root.Focus(FocusState.Programmatic);
    }

    private void RootKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_capturingHotKey || args.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
        uint modifiers = 0;
        if (Down(VirtualKey.Control)) modifiers |= HotKeyDefinition.Control;
        if (Down(VirtualKey.Menu)) modifiers |= HotKeyDefinition.Alt;
        if (Down(VirtualKey.Shift)) modifiers |= HotKeyDefinition.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= HotKeyDefinition.Win;
        var hotKey = new HotKeyDefinition { Modifiers = modifiers, VirtualKey = (uint)args.Key };
        _capturingHotKey = false;
        if (_tray.TryUpdateHotKey(hotKey, out string error))
        {
            _pendingHotKey = hotKey;
            if (_hotKeyTitle is not null) _hotKeyTitle.Text = $"{T("Hotkey")} — {TrayService.FormatHotKey(hotKey)}";
            if (_hotKeyHint is not null)
                _hotKeyHint.Text = English ? "Shortcut changed. Click Save." : "Сочетание изменено. Нажмите «Сохранить».";
        }
        else if (_hotKeyHint is not null) _hotKeyHint.Text = error;
        if (_hotKeyCaptureButton is not null) _hotKeyCaptureButton.Content = T("Change");
        UpdateFooterState();
        args.Handled = true;
    }

    private static bool Down(VirtualKey key) => (GetKeyState((int)key) & unchecked((short)0x8000)) != 0;

    private void CreateNewDraft()
    {
        if (_draftIsNew && _draft is not null) return;
        string name = NextScenarioName();
        _draft = new ScenarioDefinition
        {
            Name = name,
            Icon = ScenarioIcon.Letters,
            IconLetters = ScenarioDefinition.MakeIconLetters(name),
            VolumePercent = null
        };
        if (_scenarios.Count == 0)
        {
            _draft.DisplayIds = _displays.Where(display => display.IsActive).Take(4).Select(display => display.Id).ToList();
            AudioDevice? currentAudio = _audio.FirstOrDefault(device => device.IsDefault);
            if (currentAudio is not null)
            {
                _draft.AudioDeviceId = currentAudio.Id;
                _draft.AudioDeviceContainerId = currentAudio.ContainerId?.ToString("D") ?? string.Empty;
            }
        }
        _scenarioBaseline = null;
        _draftIsNew = true;
        SetMonitorSlots(_draft);
    }

    private void LoadScenarioDraft(Guid id)
    {
        ScenarioDefinition? scenario = _scenarios.FirstOrDefault(item => item.Id == id);
        _draft = scenario?.Clone();
        _scenarioBaseline = scenario?.Clone();
        _draftIsNew = false;
        SetMonitorSlots(_draft);
    }

    private void SetMonitorSlots(ScenarioDefinition? scenario)
    {
        _volumeInput = new StartupVolumeInput(scenario?.VolumePercent);
        _draftMonitorSlots = new string[4];
        if (scenario is null) return;
        for (int index = 0; index < Math.Min(4, scenario.DisplayIds.Count); index++)
            _draftMonitorSlots[index] = scenario.DisplayIds[index];
    }

    private string NextScenarioName()
    {
        string root = English ? "New scenario" : "Новый сценарий";
        var names = _scenarios.Select(item => item.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (int index = 1; ; index++)
        {
            string candidate = $"{root} {index}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private async Task OpenNewScenarioDraftAsync()
    {
        if (_currentPage == "new" && _draft is not null) return;
        if (!_devicesLoaded) await LoadDevicesAsync(false);
        await RequestNavigateAsync("new");
    }

    private string SavedDeviceName(string id, string fallback) =>
        DeviceAliasService.NameFor(PendingAliasSettings(), id, _settings.Current.KnownDeviceNames.GetValueOrDefault(id) ?? fallback);

    public void UpdateDeviceSnapshot(DeviceSnapshot snapshot)
    {
        SynchronizeAudioBindings(snapshot);
        string fingerprint = string.Join("|", snapshot.Displays.Select(item => $"{item.Id}:{item.IsAvailable}:{item.Name}")) +
            string.Join("|", snapshot.Audio.Select(item => $"{item.Id}:{item.State}:{item.Name}"));
        if (_deviceFingerprint == fingerprint) return;
        _deviceFingerprint = fingerprint;
        _displays.Clear();
        _displays.AddRange(snapshot.Displays.Where(item => item.IsAvailable));
        _audio.Clear();
        _audio.AddRange(AudioService.GetVisibleRenderDevices(snapshot.Audio, _displays,
            _settings.Current.RetiredAudioDeviceIds, _scenarios.Cast<ScenarioDefinition?>().ToArray()));
        _devicesLoaded = true;
        // Preserve an in-progress text edit or an open dropdown; rebuild on navigation/activation.
        object? focused = _root.XamlRoot is null ? null : FocusManager.GetFocusedElement(_root.XamlRoot);
        if (focused is TextBox or ComboBox || _loading) _deviceRefreshPending = true;
        else if (_currentPage != "general") ShowCurrentPage();
    }

    private void SynchronizeAudioBindings(DeviceSnapshot snapshot)
    {
        // Include the baseline: an automatic identity repair is not an unsaved user edit.
        var drafts = _scenarios.ToList();
        if (_draft is not null) drafts.Add(_draft);
        if (_scenarioBaseline is not null) drafts.Add(_scenarioBaseline);
        var reconciliation = new AppSettings
        {
            Scenarios = drafts,
            DeviceAliases = _pendingAliases,
            RetiredAudioDeviceIds = _settings.Current.RetiredAudioDeviceIds
        };
        if (AudioEndpointMigration.Reconcile(reconciliation, snapshot))
        {
            // Retirement is an automatic hardware-identity repair, not an editor change.
            try { _settings.Save(); }
            catch (Exception ex) { SettingsStore.Log(ex); }
        }
    }

    private async Task LoadDevicesAsync(bool openNewScenario)
    {
        if (!_devicesLoaded)
        {
            try
            {
                // RDP endpoints are transient session infrastructure, never user devices.
                // Do not display, alias or remember them in the device-name page.
                if (TrayService.IsRemoteSession)
                {
                    _devicesLoaded = true;
                    if (_currentPage == "devices") ShowCurrentPage();
                    if (openNewScenario) await RequestNavigateAsync("new");
                    return;
                }
                _displays.Clear();
                _displays.AddRange(await Task.Run(App.Displays.GetDisplays));
                _audio.Clear();
                _audio.AddRange(await App.Audio.GetVisibleRenderDevicesAsync(_displays,
                    _settings.Current.RetiredAudioDeviceIds, _scenarios.Cast<ScenarioDefinition?>().ToArray()));
                _devicesLoaded = true;
                if (_currentPage is "devices" || _currentPage == "new" || _currentPage.StartsWith("scenario:", StringComparison.Ordinal))
                    ShowCurrentPage();
            }
            catch (Exception ex) { SettingsStore.Log(ex); }
        }
        if (openNewScenario) await RequestNavigateAsync("new");
    }

    private void ResetPendingGeneral()
    {
        _pendingStartWithWindows = _settings.Current.StartWithWindows;
        _pendingAdaptiveRemoteSession = _settings.Current.AdaptiveRemoteSession;
        _pendingStartupMode = _settings.Current.StartupScenarioMode;
        _pendingStartupScenarioId = _settings.Current.StartupScenarioId;
        _pendingTheme = _settings.Current.Theme;
        _pendingLanguage = _settings.Current.Language;
        _pendingHotKey = CloneHotKey(_settings.Current.SwitchScenarioHotKey);
    }

    private void ResetPendingRemote()
    {
        _pendingRemoteVolume = Math.Clamp(_settings.Current.RemoteSessionVolumePercent, 0, 100);
        _remoteVolumeInput = new StartupVolumeInput(_pendingRemoteVolume);
    }

    private void ResetPendingAliases()
    {
        _pendingAliases.Clear();
        foreach ((string id, string alias) in _settings.Current.DeviceAliases)
            if (!string.IsNullOrWhiteSpace(alias)) _pendingAliases[id] = alias.Trim();
    }

    private bool HasUnsavedGeneralChanges()
    {
        AppSettings current = _settings.Current;
        return _pendingStartWithWindows != current.StartWithWindows ||
            _pendingAdaptiveRemoteSession != current.AdaptiveRemoteSession ||
            _pendingStartupMode != current.StartupScenarioMode ||
            _pendingStartupScenarioId != current.StartupScenarioId ||
            _pendingTheme != current.Theme ||
            _pendingLanguage != current.Language ||
            _pendingHotKey.Modifiers != current.SwitchScenarioHotKey.Modifiers ||
            _pendingHotKey.VirtualKey != current.SwitchScenarioHotKey.VirtualKey;
    }

    private bool HasUnsavedAliasChanges()
    {
        Dictionary<string, string> saved = _settings.Current.DeviceAliases
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        return saved.Count != _pendingAliases.Count || saved.Any(pair =>
            !_pendingAliases.TryGetValue(pair.Key, out string? alias) || !string.Equals(pair.Value, alias, StringComparison.Ordinal));
    }

    private AppSettings PendingAliasSettings() => new()
    {
        DeviceAliases = new Dictionary<string, string>(_pendingAliases, StringComparer.OrdinalIgnoreCase)
    };

    private static HotKeyDefinition CloneHotKey(HotKeyDefinition? hotKey)
    {
        hotKey ??= HotKeyDefinition.Default;
        return new HotKeyDefinition { Modifiers = hotKey.Modifiers, VirtualKey = hotKey.VirtualKey };
    }

    private static bool ScenarioEquals(ScenarioDefinition left, ScenarioDefinition? right) => right is not null &&
        left.Id == right.Id &&
        string.Equals(left.Name.Trim(), right.Name.Trim(), StringComparison.Ordinal) &&
        left.DisplayIds.SequenceEqual(right.DisplayIds, StringComparer.OrdinalIgnoreCase) &&
        string.Equals(left.AudioDeviceId, right.AudioDeviceId, StringComparison.OrdinalIgnoreCase) &&
        left.VolumePercent == right.VolumePercent &&
        left.Icon == right.Icon &&
        left.IncludeInHotkeyList == right.IncludeInHotkeyList &&
        left.DisplayResolutionPresets.OrderBy(item => item.Key).SequenceEqual(right.DisplayResolutionPresets.OrderBy(item => item.Key)) &&
        left.DisplayScalePercents.OrderBy(item => item.Key).SequenceEqual(right.DisplayScalePercents.OrderBy(item => item.Key)) &&
        string.Equals(ScenarioDefinition.MakeIconLetters(left.IconLetters),
            ScenarioDefinition.MakeIconLetters(right.IconLetters), StringComparison.Ordinal);

    private static bool OperationallyEqual(ScenarioDefinition left, ScenarioDefinition? right) => right is not null &&
        left.DisplayIds.SequenceEqual(right.DisplayIds, StringComparer.OrdinalIgnoreCase) &&
        left.DisplayResolutionPresets.OrderBy(item => item.Key).SequenceEqual(right.DisplayResolutionPresets.OrderBy(item => item.Key)) &&
        left.DisplayScalePercents.OrderBy(item => item.Key).SequenceEqual(right.DisplayScalePercents.OrderBy(item => item.Key)) &&
        string.Equals(left.AudioDeviceId, right.AudioDeviceId, StringComparison.OrdinalIgnoreCase) &&
        left.VolumePercent == right.VolumePercent;

    private void ApplyTheme()
    {
        _root.RequestedTheme = _settings.Current.Theme switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        NativeTheme.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this), _settings.Current.Theme);
    }

    private bool IsDark() => _settings.Current.Theme == AppThemeMode.Dark ||
        (_settings.Current.Theme == AppThemeMode.System && NativeTheme.IsSystemDark());

    private SolidColorBrush ThemeBrush(string key, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? resource))
        {
            if (resource is SolidColorBrush brush) return brush;
            if (resource is Color color) return new SolidColorBrush(color);
        }
        return new SolidColorBrush(fallback);
    }

    private SolidColorBrush AccentBrush() => ThemeBrush(
        "AccentFillColorDefaultBrush", new UISettings().GetColorValue(UIColorType.Accent));

    private SolidColorBrush AccentTextBrush()
    {
        Color accent = new UISettings().GetColorValue(UIColorType.Accent);
        double luminance = .299 * accent.R + .587 * accent.G + .114 * accent.B;
        return ThemeBrush("TextOnAccentFillColorPrimaryBrush", luminance > 165 ? Colors.Black : Colors.White);
    }

    private static string ScenarioPage(Guid id) => $"scenario:{id:D}";

    private static bool TryScenarioId(string page, out Guid id)
    {
        id = Guid.Empty;
        return page.StartsWith("scenario:", StringComparison.Ordinal) && Guid.TryParse(page[9..], out id);
    }

    private sealed record Choice(string Id, string Name);
    private sealed record ResolutionChoice(DisplayResolutionPreset Preset, string Name);
    private sealed record ScaleChoice(int? Percent, string Name);
    private sealed record StartupChoice(Guid? Id, string Name);
}
