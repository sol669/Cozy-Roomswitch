namespace RoomSwitcherTray.Core.Services;

public sealed record DeviceSnapshot(
    IReadOnlyList<DisplayDevice> Displays,
    IReadOnlyList<AudioDevice> Audio,
    IReadOnlyList<ActiveDisplayStatus> ActiveDisplays,
    AudioEndpointStatus? DefaultAudio,
    bool AudioReadFailed = false)
{
    public static DeviceSnapshot Empty { get; } = new([], [], [], null);
}

public enum ScenarioHealth { None, Checking, Full, Partial, Failed }
public enum ScenarioIssue { None, DevicesMissing, DevicesCheckFailed }

public sealed record ScenarioTrayDevices(IReadOnlyList<string> DisplayIds, string? AudioId, AudioDevice? Audio);

public sealed record ScenarioStatus(ScenarioHealth Health, string Reason, ScenarioIssue Issue = ScenarioIssue.None)
{
    public bool Warn => Health is ScenarioHealth.Partial or ScenarioHealth.Failed;
}

// Pure rules shared by menu, hotkey, startup and the coordinator's regression tests.
public static class ScenarioPolicy
{
    // Tray order is the exact order configured by the user. Keep the original
    // index as it is also used for menu command dispatch.
    public static IEnumerable<(ScenarioDefinition Scenario, int Index)> TrayOrder(AppSettings settings) =>
        settings.Scenarios.Select((scenario, index) => (Scenario: scenario, Index: index));

    // The tray describes the selected scenario, not the global/current Windows topology.
    // Preserve configured IDs even when disconnected. Never substitute Windows' fallback output.
    public static ScenarioTrayDevices TrayDevices(ScenarioDefinition? scenario, DeviceSnapshot snapshot)
    {
        if (scenario is null) return new([], null, null);
        string? audioId = string.IsNullOrWhiteSpace(scenario.AudioDeviceId) ? null : scenario.AudioDeviceId;
        return new(scenario.DisplayIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), audioId,
            audioId is null || snapshot.AudioReadFailed ? null :
                FindAudio(scenario, snapshot) ?? FindAudio(scenario, snapshot, activeOnly: false));
    }

    public static string[] AvailableDisplays(ScenarioDefinition scenario, DeviceSnapshot snapshot) =>
        scenario.DisplayIds.Where(id => snapshot.Displays.Any(device =>
            device.IsAvailable && Same(device.Id, id))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool CanApply(ScenarioDefinition scenario, DeviceSnapshot snapshot) =>
        scenario.IsComplete && AvailableDisplays(scenario, snapshot).Length > 0;

    public static ScenarioDefinition? Next(AppSettings settings, DeviceSnapshot snapshot)
    {
        int current = settings.Scenarios.FindIndex(item => item.Id == settings.ActiveScenarioId);
        for (int offset = 1; offset <= settings.Scenarios.Count; offset++)
        {
            int index = (current + offset) % settings.Scenarios.Count;
            ScenarioDefinition candidate = settings.Scenarios[index];
            if (candidate.Id != settings.ActiveScenarioId && candidate.IncludeInHotkeyList && CanApply(candidate, snapshot)) return candidate;
        }
        return null;
    }

    public static AudioDevice? FindAudio(ScenarioDefinition scenario, DeviceSnapshot snapshot, bool activeOnly = true)
    {
        if (snapshot.AudioReadFailed || string.IsNullOrWhiteSpace(scenario.AudioDeviceId)) return null;
        AudioDevice? exact = snapshot.Audio.FirstOrDefault(item => Same(item.Id, scenario.AudioDeviceId));
        AudioDevice? replacement = AudioEndpointMigration.FindReplacement(scenario, snapshot);
        if (replacement is not null) return replacement;
        // A disconnected/disabled endpoint must not resolve to a sibling output.
        if (exact is not null) return !activeOnly || exact.IsActive ? exact : null;
        if (!Guid.TryParse(scenario.AudioDeviceContainerId, out Guid container) ||
            !DeviceIdentity.IsDeviceContainer(container)) return null;
        // Count ALL states: an inactive sibling still makes the container ambiguous.
        var matches = snapshot.Audio.Where(item => item.ContainerId == container).ToList();
        return matches.Count == 1 && (!activeOnly || matches[0].IsActive) ? matches[0] : null;
    }

    public static bool MayAwaitHdmi(ScenarioDefinition scenario, DeviceSnapshot snapshot)
    {
        AudioDevice? known = FindAudio(scenario, snapshot, activeOnly: false);
        if (known?.State == AudioDeviceState.Disabled || snapshot.AudioReadFailed) return false;
        if (known is not null && known.Kind != AudioDeviceKind.Display) return false;
        bool matchingDisplay = Guid.TryParse(scenario.AudioDeviceContainerId, out Guid container) &&
            DeviceIdentity.IsDeviceContainer(container) &&
            snapshot.Displays.Any(item => item.IsAvailable && item.ContainerId == container &&
                scenario.DisplayIds.Any(id => Same(id, item.Id)));
        // Some GPU drivers assign different containers to the monitor and its HDMI endpoint.
        return matchingDisplay || known?.Kind == AudioDeviceKind.Display && AvailableDisplays(scenario, snapshot).Length > 0;
    }

    public static ScenarioStatus Evaluate(ScenarioDefinition? scenario, DeviceSnapshot snapshot, AppSettings settings)
    {
        if (scenario is null) return new(ScenarioHealth.None, "");
        bool english = settings.Language == AppLanguage.English;
        var missing = new List<string>();
        foreach (string id in scenario.DisplayIds)
            if (!snapshot.Displays.Any(item => item.IsAvailable && Same(item.Id, id)))
                missing.Add(Name(settings, snapshot, id, english ? "Monitor" : "Монитор"));

        AudioDevice? audio = FindAudio(scenario, snapshot);
        AudioDevice? configuredAudio = audio ?? FindAudio(scenario, snapshot, false);
        if (!string.IsNullOrWhiteSpace(scenario.AudioDeviceId) && audio is null && !snapshot.AudioReadFailed &&
            configuredAudio?.State != AudioDeviceState.Disabled)
            missing.Add(Name(settings, snapshot, scenario.AudioDeviceId, english ? "Audio device" : "Аудиоустройство"));

        if (missing.Count > 0) return new(ScenarioHealth.Partial,
            (english ? "Not connected: " : "Не подключено: ") + string.Join(", ", missing), ScenarioIssue.DevicesMissing);
        if (!string.IsNullOrWhiteSpace(scenario.AudioDeviceId) && snapshot.AudioReadFailed)
            return new(ScenarioHealth.Partial, english ? "Could not check audio devices" : "Не удалось проверить звук",
                ScenarioIssue.DevicesCheckFailed);
        if (configuredAudio?.State == AudioDeviceState.Disabled)
            return new(ScenarioHealth.Partial, (english ? "Disabled in Windows: " : "Отключено в Windows: ") +
                Name(settings, snapshot, scenario.AudioDeviceId, english ? "Audio device" : "Аудиоустройство"));

        bool allActive = scenario.DisplayIds.All(id =>
            snapshot.Displays.Any(item => item.IsAvailable && item.IsActive && Same(item.Id, id)));
        bool extraActive = snapshot.Displays.Any(item => item.IsActive && item.IsAvailable &&
            !scenario.DisplayIds.Any(id => Same(id, item.Id)));
        if (!allActive || extraActive) return new(ScenarioHealth.Partial,
            english ? "Display configuration differs" : "Конфигурация экранов отличается");
        if (audio is not null && !audio.IsDefault) return new(ScenarioHealth.Partial,
            english ? "A different audio output is selected" : "Выбран другой аудиовыход");
        return new(ScenarioHealth.Full, "");
    }

    public static string Name(AppSettings settings, DeviceSnapshot snapshot, string id, string fallback)
    {
        if (settings.DeviceAliases.TryGetValue(id, out string? alias) && !string.IsNullOrWhiteSpace(alias)) return alias;
        string? live = snapshot.Displays.FirstOrDefault(item => item.IsAvailable && Same(item.Id, id))?.Name ??
            snapshot.Audio.FirstOrDefault(item => Same(item.Id, id))?.DisplayName ??
            snapshot.Audio.FirstOrDefault(item => Same(item.Id, id))?.Name;
        return live ?? settings.KnownDeviceNames.GetValueOrDefault(id) ?? fallback;
    }

    public static string Tooltip(ScenarioDefinition? scenario, ScenarioStatus status, bool english)
    {
        if (scenario is null) return "Cozy Roomswitch";
        string message = status.Health switch
        {
            ScenarioHealth.Checking => english ? "Switching scenario" : "Переключение сценария",
            ScenarioHealth.Partial when status.Issue == ScenarioIssue.DevicesCheckFailed =>
                english ? "Could not check devices" : "Не удалось проверить устройства",
            ScenarioHealth.Partial when status.Issue == ScenarioIssue.DevicesMissing =>
                english ? "Some devices are not connected" : "Не все устройства подключены",
            ScenarioHealth.Partial => status.Reason.Length > 0 ? status.Reason :
                english ? "Scenario partially applied" : "Сценарий применён частично",
            ScenarioHealth.Failed => english ? "Could not apply scenario" : "Не удалось применить сценарий",
            _ => ""
        };
        string name = Shorten(scenario.Name.Replace('\r', ' ').Replace('\n', ' '), 64);
        return Shorten((english ? "Scenario: " : "Сценарий: ") + name +
            (message.Length > 0 ? "\n" + message : ""), 127);
    }

    private static string Shorten(string text, int limit)
    {
        if (text.Length <= limit) return text;
        int length = char.IsHighSurrogate(text[limit - 2]) ? limit - 2 : limit - 1;
        return text[..length] + "…";
    }

    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
