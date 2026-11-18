using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace SCPSL.ModLaunch.Core;

public enum LauncherTheme { Light, Dark }

/// <summary>Keeps an explicit GUI preference separate from preparation settings.</summary>
public static class LauncherThemePreferences
{
    private const string SettingsPath = ".modlaunch/gui-settings.json";

    public static LauncherTheme Load(string gameDirectory, Func<LauncherTheme?>? readSystemTheme = null)
    {
        try
        {
            string path = FileOps.SafePath(gameDirectory, SettingsPath);
            if (File.Exists(path))
            {
                string? value = FileOps.ReadJson<GuiSettings>(path).Theme;
                if (string.Equals(value, "light", StringComparison.OrdinalIgnoreCase)) return LauncherTheme.Light;
                if (string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase)) return LauncherTheme.Dark;
            }
        }
        catch (Exception error) when (IsUnavailable(error)) { }

        // Automatic detection is not saved: only a user's explicit choice overrides the host.
        try
        {
            var theme = (readSystemTheme ?? ReadSystemTheme)();
            if (theme is LauncherTheme.Light or LauncherTheme.Dark) return theme.Value;
        }
        catch (Exception error) when (IsUnavailable(error)) { }
        return LauncherTheme.Dark;
    }

    public static bool TrySave(string gameDirectory, LauncherTheme theme)
    {
        if (theme is not (LauncherTheme.Light or LauncherTheme.Dark)) return false;
        try
        {
            string path = FileOps.SafePath(gameDirectory, SettingsPath);
            FileOps.WriteJson(path, new GuiSettings(theme == LauncherTheme.Light ? "light" : "dark"));
            return true;
        }
        catch (Exception error) when (IsUnavailable(error)) { return false; }
    }

    public static LauncherTheme? ReadSystemTheme()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Windows app-theme preference: REG_DWORD 0 = dark, 1 = light.
            // https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-common
            return FromSystemValue(Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", null));
        }
        catch (Exception error) when (IsUnavailable(error)) { return null; }
    }

    internal static LauncherTheme? FromSystemValue(object? value) => value switch
    {
        int number when number == 0 => LauncherTheme.Dark,
        int number when number == 1 => LauncherTheme.Light,
        _ => null
    };

    private static bool IsUnavailable(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or ArgumentException or NotSupportedException or SecurityException or JsonException or
        System.ComponentModel.Win32Exception;

    private sealed record GuiSettings(string? Theme);
}
