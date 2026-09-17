using System.Text.Json.Serialization;

namespace RoomSwitcherTray.Core;

public sealed class ScenarioDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<string> DisplayIds { get; set; } = [];
    public string AudioDeviceId { get; set; } = string.Empty;
    public string AudioDeviceContainerId { get; set; } = string.Empty;
    // Null means "do not change". Zero means mute; positive values are percentages.
    public int? VolumePercent { get; set; }
    public ScenarioIcon Icon { get; set; }
    public string IconLetters { get; set; } = string.Empty;
    // Existing settings predate this flag, so the default deliberately preserves
    // the old behavior: every scenario participates in the hotkey cycle.
    public bool IncludeInHotkeyList { get; set; } = true;
    // Per-display overrides. Missing entries deliberately mean "do not change".
    public Dictionary<string, DisplayResolutionPreset> DisplayResolutionPresets { get; set; } = [];
    public Dictionary<string, int> DisplayScalePercents { get; set; } = [];

    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Name) &&
        DisplayIds.Count is >= 1 and <= 4 &&
        DisplayIds.All(id => !string.IsNullOrWhiteSpace(id)) &&
        DisplayIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == DisplayIds.Count;

    public ScenarioDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        DisplayIds = [.. DisplayIds],
        AudioDeviceId = AudioDeviceId,
        AudioDeviceContainerId = AudioDeviceContainerId,
        VolumePercent = VolumePercent,
        Icon = Icon,
        IconLetters = IconLetters,
        IncludeInHotkeyList = IncludeInHotkeyList,
        DisplayResolutionPresets = new Dictionary<string, DisplayResolutionPreset>(DisplayResolutionPresets, StringComparer.OrdinalIgnoreCase),
        DisplayScalePercents = new Dictionary<string, int>(DisplayScalePercents, StringComparer.OrdinalIgnoreCase)
    };

    public static string MakeIconLetters(string? value)
    {
        string letters = new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Take(3)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return letters;
    }
}

public enum DisplayResolutionPreset
{
    KeepCurrent = 0,
    UltraHd4K = 1,
    QuadHd2K = 2,
    FullHd1080 = 3,
    Hd720 = 4
}

public static class DisplayResolution
{
    public static readonly IReadOnlyList<DisplayResolutionPreset> Presets =
        [DisplayResolutionPreset.UltraHd4K, DisplayResolutionPreset.QuadHd2K,
         DisplayResolutionPreset.FullHd1080, DisplayResolutionPreset.Hd720];

    public static (int Width, int Height) Size(DisplayResolutionPreset preset) => preset switch
    {
        DisplayResolutionPreset.UltraHd4K => (3840, 2160),
        DisplayResolutionPreset.QuadHd2K => (2560, 1440),
        DisplayResolutionPreset.FullHd1080 => (1920, 1080),
        DisplayResolutionPreset.Hd720 => (1280, 720),
        _ => (0, 0)
    };

    // Used both for quick tray changes and as the initial scenario value.
    // A scenario may still deliberately override this choice afterwards.
    public static int? RecommendedScale(DisplayResolutionPreset preset) => preset switch
    {
        DisplayResolutionPreset.UltraHd4K => 200,
        DisplayResolutionPreset.QuadHd2K => 125,
        DisplayResolutionPreset.FullHd1080 or DisplayResolutionPreset.Hd720 => 100,
        _ => null
    };
}

public enum StartupScenarioMode
{
    KeepCurrentConfiguration,
    RestoreLastScenario,
    AlwaysUseScenario
}

public enum AppThemeMode { System, Light, Dark }
public enum AppLanguage { Russian, English }
// Letters deliberately keeps the old Automatic ordinal so existing settings migrate cleanly.
public enum ScenarioIcon
{
    // Values 0–4 are persisted in existing settings. Never reorder them.
    Letters = 0, Desktop = 1, Television = 2, Sofa = 3, Gamepad = 4,
    Laptop = 5, DualMonitors = 6, LaptopAndMonitor = 7, TripleMonitors = 8,
    QuadMonitors = 9, Speakers = 10, Headphones = 11, Projector = 12,
    Microphone = 13, Webcam = 14, Deck = 15, DesktopAudio = 16
}

public sealed class AppSettings
{
    public List<ScenarioDefinition> Scenarios { get; set; } = [];
    public Guid? ActiveScenarioId { get; set; }
    public bool StartWithWindows { get; set; }
    public StartupScenarioMode StartupScenarioMode { get; set; }
    public Guid? StartupScenarioId { get; set; }
    public HotKeyDefinition SwitchScenarioHotKey { get; set; } = HotKeyDefinition.Default;
    public Dictionary<string, string> DeviceAliases { get; set; } = [];
    // Last observed system names survive physical disconnection. Never replace aliases.
    public Dictionary<string, string> KnownDeviceNames { get; set; } = [];
    // Endpoint IDs replaced by a verified HDMI/DisplayPort successor. They remain
    // in scenario history for compatibility, but are not offered in the device picker.
    public List<string> RetiredAudioDeviceIds { get; set; } = [];
    // Optional RDP-only behavior. It deliberately has no device bindings and
    // never appears in the local device-name editor.
    public bool AdaptiveRemoteSession { get; set; }
    public int RemoteSessionVolumePercent { get; set; } = 100;
    // Tray actions are normally confirmed through Windows notifications.  Keep this
    // opt-out global: it is intentionally independent of individual scenarios.
    public bool EnableNotifications { get; set; } = true;
    // Resolution picked from the tray may restore a sensible DPI scale.  Scenario
    // scale values remain explicit and are never overridden by this preference.
    public bool AutoScaleOnResolutionChange { get; set; }
    public AppThemeMode Theme { get; set; }
    public AppLanguage Language { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyScenarioDefinition? Scenario1 { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyScenarioDefinition? Scenario2 { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ActiveScenario { get; set; }
}

public sealed class HotKeyDefinition
{
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Alt = 0x0001;
    public const uint Win = 0x0008;
    public uint Modifiers { get; set; } = Control;
    public uint VirtualKey { get; set; } = 0x20;
    public static HotKeyDefinition Default => new();
}

public sealed class LegacyScenarioDefinition
{
    public string Name { get; set; } = string.Empty;
    public string DisplayId { get; set; } = string.Empty;
    public string SecondaryDisplayId { get; set; } = string.Empty;
    public List<string>? DisplayIds { get; set; }
    public string AudioDeviceId { get; set; } = string.Empty;
    public string AudioDeviceContainerId { get; set; } = string.Empty;

    public ScenarioDefinition Upgrade()
    {
        IEnumerable<string> ids = DisplayIds is { Count: > 0 }
            ? DisplayIds
            : new[] { DisplayId, SecondaryDisplayId };
        return new ScenarioDefinition
        {
            Name = Name,
            DisplayIds = ids.Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList(),
            AudioDeviceId = AudioDeviceId,
            AudioDeviceContainerId = AudioDeviceContainerId
        };
    }
}

public sealed record DisplayDevice(
    string Id,
    string Name,
    bool IsActive,
    bool IsAvailable,
    Guid? ContainerId)
{
    public override string ToString() => IsActive ? $"{Name} — используется" : Name;
}

public enum AudioDeviceState
{
    Active,
    Disabled,
    NotPresent,
    Unplugged
}

/// <summary>Live information shown for a display that is active right now.</summary>
public sealed record ActiveDisplayStatus(
    string Id,
    string Name,
    int Width,
    int Height,
    bool HdrSupported,
    bool HdrEnabled);

public enum AudioDeviceKind
{
    Other,
    Display
}

public sealed record AudioDevice(
    string Id,
    string Name,
    bool IsDefault,
    AudioDeviceState State,
    Guid? ContainerId,
    AudioDeviceKind Kind,
    string? DisplayName = null)
{
    public bool IsActive => State == AudioDeviceState.Active;

    public override string ToString()
    {
        string label = Kind == AudioDeviceKind.Display && !string.IsNullOrWhiteSpace(DisplayName)
            ? $"{DisplayName} — HDMI/DisplayPort"
            : Name;
        return IsDefault ? $"{label} — используется" :
            State == AudioDeviceState.Disabled ? $"{label} — отключено в Windows" :
            State != AudioDeviceState.Active ? $"{label} — сейчас недоступно" :
            label;
    }
}

/// <summary>Live master-volume state of the current Windows render endpoint.</summary>
public sealed record AudioEndpointStatus(string Name, int VolumePercent, bool IsMuted);

public sealed record ApplyResult(bool Success, string Message);
