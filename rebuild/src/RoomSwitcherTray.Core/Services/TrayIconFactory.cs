using Microsoft.Win32;
using System.Drawing;

namespace RoomSwitcherTray.Core.Services;

internal static class TrayIconFactory
{
    public static nint Create(ScenarioDefinition? scenario = null, bool warning = false, bool remote = false)
    {
        Color color = UsesLightTaskbar() ? Color.FromArgb(24, 24, 24) : Color.White;
        ScenarioIcon icon = scenario?.Icon ?? ScenarioIcon.Desktop;
        string? letters = string.IsNullOrWhiteSpace(scenario?.IconLetters) ? scenario?.Name : scenario.IconLetters;
        using var bitmap = remote ? ScenarioArtwork.RenderRemote(color, 32) :
            warning ? ScenarioArtwork.RenderWarning(color, 32) :
            ScenarioArtwork.Render(icon, letters, color, 32, compactThreeLetters: true);
        return bitmap.GetHicon();
    }

    private static bool UsesLightTaskbar()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int number && number != 0;
        }
        catch { return false; }
    }
}
