using System.Diagnostics;
using System.Text.Json;

namespace RoomSwitcherTray.Core.Services;

public sealed class SettingsStore
{
    // Installed and portable editions keep all app-owned state beside the executable.
    // The installer exposes only this Data folder as writable and removes it on uninstall.
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private const long MaximumLogBytes = 512 * 1024;
    private const long RetainedLogBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Current { get; private set; } = new();
    public bool IsConfigured => Current.Scenarios.Count > 0 && Current.Scenarios.All(s => s.IsComplete);
    public event EventHandler? Saved;

    public void Load()
    {
        try
        {
            string? json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
            Current = json is not null
                ? JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new()
                : new();
            // Existing settings files predate the opt-in setting.  Treat an absent
            // value as the new default instead of silently turning notifications off.
            if (json is not null && !HasProperty(json, nameof(AppSettings.EnableNotifications)))
                Current.EnableNotifications = true;
            UpgradeLegacySettings();
            if (!IsConfigured)
                Current.ActiveScenarioId = null;
        }
        catch (Exception ex)
        {
            Log(ex);
            Current = new();
        }
    }

    private static bool HasProperty(string json, string propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.EnumerateObject().Any(property =>
                string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private void UpgradeLegacySettings()
    {
        Current.KnownDeviceNames = new Dictionary<string, string>(Current.KnownDeviceNames ?? [], StringComparer.OrdinalIgnoreCase);
        Current.DeviceAliases = new Dictionary<string, string>(Current.DeviceAliases ?? [], StringComparer.OrdinalIgnoreCase);
        Current.RetiredAudioDeviceIds = (Current.RetiredAudioDeviceIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (Current.Scenarios.Count == 0)
        {
            if (Current.Scenario1 is not null) Current.Scenarios.Add(Current.Scenario1.Upgrade());
            if (Current.Scenario2 is not null) Current.Scenarios.Add(Current.Scenario2.Upgrade());
            if (Current.ActiveScenario is 1 or 2 && Current.Scenarios.Count >= Current.ActiveScenario)
                Current.ActiveScenarioId = Current.Scenarios[Current.ActiveScenario - 1].Id;
        }
        Current.Scenario1 = null;
        Current.Scenario2 = null;
        Current.ActiveScenario = 0;
        foreach (ScenarioDefinition scenario in Current.Scenarios)
        {
            scenario.DisplayResolutionPresets = new Dictionary<string, DisplayResolutionPreset>(
                scenario.DisplayResolutionPresets ?? [], StringComparer.OrdinalIgnoreCase);
            scenario.DisplayScalePercents = new Dictionary<string, int>(
                scenario.DisplayScalePercents ?? [], StringComparer.OrdinalIgnoreCase);
            scenario.IconLetters = ScenarioDefinition.MakeIconLetters(
                string.IsNullOrWhiteSpace(scenario.IconLetters) ? scenario.Name : scenario.IconLetters);
        }
        if (Current.ActiveScenarioId.HasValue &&
            Current.Scenarios.All(scenario => scenario.Id != Current.ActiveScenarioId.Value))
            Current.ActiveScenarioId = null;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        Saved?.Invoke(this, EventArgs.Empty);
    }

    public static void Log(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string logPath = Path.Combine(Folder, "error.log");
            TrimLogIfNeeded(logPath);
            File.AppendAllText(logPath,
                $"[{DateTime.Now:O}] {exception}\r\n\r\n");
        }
        catch
        {
            Debug.WriteLine(exception);
        }
    }

    private static void TrimLogIfNeeded(string logPath)
    {
        var info = new FileInfo(logPath);
        if (!info.Exists || info.Length <= MaximumLogBytes) return;

        string temporaryPath = logPath + ".trim";
        try
        {
            using (var source = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.Seek(-RetainedLogBytes, SeekOrigin.End);
                source.CopyTo(destination);
            }
            File.Move(temporaryPath, logPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
