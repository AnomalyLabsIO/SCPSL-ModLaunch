using System.Security;
using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class LauncherThemePreferencesTests
{
    [Theory]
    [InlineData(LauncherTheme.Light, "light")]
    [InlineData(LauncherTheme.Dark, "dark")]
    public void ExplicitChoiceSurvivesRestartAndDoesNotQuerySystemTheme(LauncherTheme theme, string stored)
    {
        using var game = new TestGame();
        Assert.True(LauncherThemePreferences.TrySave(game.Root, theme));

        Assert.Equal(theme, LauncherThemePreferences.Load(game.Root,
            () => throw new InvalidOperationException("Saved selection must take precedence.")));
        Assert.Contains($"\"theme\": \"{stored}\"", File.ReadAllText(SettingsPath(game.Root)));
    }

    [Theory]
    [InlineData(LauncherTheme.Light)]
    [InlineData(LauncherTheme.Dark)]
    public void FirstLaunchUsesSystemThemeWithoutCreatingAnExplicitPreference(LauncherTheme system)
    {
        using var game = new TestGame();
        Assert.Equal(system, LauncherThemePreferences.Load(game.Root, () => system));
        Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch")));
    }

    [Fact]
    public void AutomaticChoiceContinuesFollowingSystemUntilUserSelectsATheme()
    {
        using var game = new TestGame();
        Assert.Equal(LauncherTheme.Light, LauncherThemePreferences.Load(game.Root, () => LauncherTheme.Light));
        Assert.Equal(LauncherTheme.Dark, LauncherThemePreferences.Load(game.Root, () => LauncherTheme.Dark));

        Assert.True(LauncherThemePreferences.TrySave(game.Root, LauncherTheme.Light));

        Assert.Equal(LauncherTheme.Light, LauncherThemePreferences.Load(game.Root, () => LauncherTheme.Dark));
    }

    [Fact]
    public void MissingOrUnknownSystemThemeDefaultsToDark()
    {
        using var game = new TestGame();
        Assert.Equal(LauncherTheme.Dark, LauncherThemePreferences.Load(game.Root, () => null));
        Assert.Equal(LauncherTheme.Dark, LauncherThemePreferences.Load(game.Root, () => (LauncherTheme)42));
        Assert.False(File.Exists(SettingsPath(game.Root)));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(SecurityException))]
    [InlineData(typeof(PlatformNotSupportedException))]
    [InlineData(typeof(IOException))]
    public void UnavailableSystemDetectionDefaultsToDark(Type errorType)
    {
        using var game = new TestGame();
        Assert.Equal(LauncherTheme.Dark, LauncherThemePreferences.Load(game.Root,
            () => throw (Exception)Activator.CreateInstance(errorType)!));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"theme\":\"unsupported\"}")]
    [InlineData("{\"theme\":7}")]
    public void InvalidPreferenceFallsBackToSystemWithoutRewritingTheFile(string content)
    {
        using var game = new TestGame();
        game.Write(".modlaunch/gui-settings.json", System.Text.Encoding.UTF8.GetBytes(content));

        Assert.Equal(LauncherTheme.Light, LauncherThemePreferences.Load(game.Root, () => LauncherTheme.Light));
        Assert.Equal(content, File.ReadAllText(SettingsPath(game.Root)));
    }

    [Fact]
    public void PreferenceStorageDoesNotChangePreparationSettingsOrLeaveTemporaryFiles()
    {
        using var game = new TestGame();
        const string original = "{\"melonLoaderVersion\":\"custom\",\"extra\":true}";
        game.Write(".modlaunch/settings.json", System.Text.Encoding.UTF8.GetBytes(original));

        Assert.True(LauncherThemePreferences.TrySave(game.Root, LauncherTheme.Light));
        Assert.True(LauncherThemePreferences.TrySave(game.Root, LauncherTheme.Dark));

        Assert.Equal(original, File.ReadAllText(Path.Combine(game.Root, ".modlaunch/settings.json")));
        Assert.Equal(LauncherTheme.Dark, LauncherThemePreferences.Load(game.Root, () => LauncherTheme.Light));
        Assert.Empty(Directory.GetFiles(Path.Combine(game.Root, ".modlaunch"), "*.tmp"));
    }

    [Fact]
    public void UnwritablePreferenceLocationDoesNotPreventThemeSelection()
    {
        using var game = new TestGame();
        // A file occupying the required directory fails consistently on Windows and Linux.
        game.Write(".modlaunch", [1, 2, 3]);

        Assert.False(LauncherThemePreferences.TrySave(game.Root, LauncherTheme.Light));
        Assert.Equal(LauncherTheme.Light, LauncherThemePreferences.Load(game.Root, () => LauncherTheme.Light));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(game.Root, ".modlaunch")));
    }

    [Fact]
    public void DirectoryAtPreferenceFilenameIsNotReplacedAndTemporaryFileIsRemoved()
    {
        using var game = new TestGame();
        Directory.CreateDirectory(SettingsPath(game.Root));

        Assert.False(LauncherThemePreferences.TrySave(game.Root, LauncherTheme.Light));
        Assert.Equal(LauncherTheme.Dark, LauncherThemePreferences.Load(game.Root, () => null));
        Assert.True(Directory.Exists(SettingsPath(game.Root)));
        Assert.Empty(Directory.GetFiles(Path.Combine(game.Root, ".modlaunch")));
    }

    [Fact]
    public void InvalidEnumIsNotPersisted()
    {
        using var game = new TestGame();
        Assert.False(LauncherThemePreferences.TrySave(game.Root, (LauncherTheme)42));
        Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch")));
    }

    [Theory]
    [InlineData(0, LauncherTheme.Dark)]
    [InlineData(1, LauncherTheme.Light)]
    public void WindowsDwordMapsToApplicationTheme(int value, LauncherTheme expected)
    {
        Assert.Equal(expected, LauncherThemePreferences.FromSystemValue(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData(true)]
    public void MissingOrUnexpectedRegistryValueIsUnavailable(object? value)
    {
        Assert.Null(LauncherThemePreferences.FromSystemValue(value));
    }

    [Fact]
    public void NativeLinuxDetectionHasNoWindowsRegistryDependencyAndDefaultsToDark()
    {
        if (OperatingSystem.IsWindows()) return;
        using var game = new TestGame();
        Assert.Null(LauncherThemePreferences.ReadSystemTheme());
        Assert.Equal(LauncherTheme.Dark, LauncherThemePreferences.Load(game.Root));
        Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch")));
    }

    private static string SettingsPath(string root) => Path.Combine(root, ".modlaunch/gui-settings.json");
}
