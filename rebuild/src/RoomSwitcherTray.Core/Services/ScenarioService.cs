namespace RoomSwitcherTray.Core.Services;

public sealed class ScenarioService(SettingsStore settings, DisplayService displays, AudioService audio)
    : ScenarioCoordinator(() => settings.Current, settings.Save, SettingsStore.Log,
        new WindowsScenarioDevices(displays, audio));

internal sealed class WindowsScenarioDevices(DisplayService displays, AudioService audio) : IScenarioDevices
{
    public Task<DeviceSnapshot> CaptureAsync() => Task.Run(() =>
    {
        IReadOnlyList<DisplayDevice> screens = displays.GetKnownDisplays();
        IReadOnlyList<ActiveDisplayStatus> active = displays.GetActiveDisplayStatuses();
        IReadOnlyList<AudioDevice> endpoints = [];
        bool audioReadFailed = false;
        try { endpoints = AudioService.GetRenderDevices(); }
        catch (Exception ex) { SettingsStore.Log(ex); audioReadFailed = true; }
        AudioEndpointStatus? volume = null;
        try { volume = audio.GetDefaultEndpointStatus(endpoints); }
        catch (Exception ex) { SettingsStore.Log(ex); }
        return new DeviceSnapshot(screens, endpoints, active, volume, audioReadFailed);
    });

    public Task ApplyDisplaysAsync(IReadOnlyCollection<string> ids) =>
        Task.Run(() => displays.ApplyDisplays(ids));

    public Task ApplyResolutionsAsync(ScenarioDefinition scenario) =>
        Task.Run(() => displays.ApplyDisplaySettings(
            scenario.DisplayResolutionPresets, scenario.DisplayScalePercents));

    public void ApplyAudio(AudioDevice device, int? volume)
    {
        AudioService.SetDefault(device.Id);
        if (volume.HasValue) AudioService.SetEndpointVolume(device.Id, volume.Value);
    }
}
