using SCPSL.ModLaunch.Core;
using Tomlet;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ArgumentsPreserveOrderAndEmptyAndQuotedValues()
    {
        string[] expected = ["-noauth", "--name", "a b", "", "literal\"quote", @"C:\a directory\", "--melonloader.hideconsole"];
        var result = GameArguments.Parse(expected);
        Assert.False(result.RecoverOnly);
        Assert.Equal(expected, result.GameArguments);
    }

    [Fact]
    public void NoImplicitGameArgumentsAreAdded()
    {
        Assert.Empty(GameArguments.Parse([]).GameArguments);
    }

    [Fact]
    public void LaunchUsesArgumentListAndScopesRuntimeConfigurationToChild()
    {
        using var game = new TestGame();
        string? inheritedRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        string? inheritedOverrides = Environment.GetEnvironmentVariable("WINEDLLOVERRIDES");
        string[] arguments = ["-noauth", "", "two words", "a\"b", @"trailing\"];
        var info = GameArguments.StartInfo(game.Root, game.Stage, arguments);
        Assert.Equal(arguments, info.ArgumentList);
        Assert.Empty(info.Arguments);
        Assert.Equal(game.Root, info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.Equal(game.Stage, info.Environment["DOTNET_ROOT"]);
        Assert.Contains("version=n,b", info.Environment["WINEDLLOVERRIDES"]);
        Assert.Equal(inheritedRoot, Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        Assert.Equal(inheritedOverrides, Environment.GetEnvironmentVariable("WINEDLLOVERRIDES"));
    }

    [Fact]
    public void RecoverOnlyFlagCannotSilentlyDiscardGameArguments()
    {
        Assert.True(GameArguments.Parse(["--modlaunch-recover-only"]).RecoverOnly);
        Assert.ThrowsAny<Exception>(() => GameArguments.Parse(["--modlaunch-recover-only", "-noauth"]));
    }

    [Fact]
    public void StandaloneSeparatorIsForwardedLikeAnyOtherGameArgument()
    {
        var result = GameArguments.Parse(["--", "-noauth", "two words"]);
        Assert.False(result.RecoverOnly);
        Assert.Equal(new[] { "--", "-noauth", "two words" }, result.GameArguments);
    }

    [Fact]
    public void LoaderConfigurationChangesOnlyTargetedKeysInTheirOwnSections()
    {
        using var game = new TestGame();
        var path = Path.Combine(game.Root, "UserData", "Loader.cfg");
        File.WriteAllText(path, "# user comment\n[console]\nhide_console = true # old value\nconsole_mode = 2\n[other]\nhide_console = true\nmy_value = 'retained'\n[mono]\nsearch_path = 'retained too'\n[loader]\nhostfxr_path_override = 'old'\n");
        var runtime = new HostRuntimeInfo(Path.Combine(game.Root, "runtime directory"), Path.Combine(game.Root, "runtime directory", "host", "fxr", "6.0.36", "hostfxr.dll"), new Version(6, 0, 36));
        LoaderConfig.Update(path, runtime);
        var first = File.ReadAllText(path);
        Assert.Contains("# user comment", first);
        Assert.Contains("console_mode = 2", first);
        var config = new TomlParser().Parse(first);
        Assert.True(config.GetSubTable("other").GetBoolean("hide_console"));
        Assert.Equal("retained", config.GetSubTable("other").GetString("my_value"));
        Assert.Equal("retained too", config.GetSubTable("mono").GetString("search_path"));
        Assert.Equal(Path.GetDirectoryName(runtime.HostFxrPath), config.GetSubTable("loader").GetString("hostfxr_path_override"));
        Assert.Contains("hide_console = false", first);
        Assert.DoesNotContain("hostfxr_path_override = 'old'", first);
        LoaderConfig.Update(path, runtime);
        Assert.Equal(first, File.ReadAllText(path));
    }

    [Fact]
    public void LoaderOverrideRestrictsDiscoveryToTheSelectedHostVersion()
    {
        using var game = new TestGame();
        string runtimeRoot = Path.Combine(game.Root, "mixed dotnet installation");
        HostRuntimeTests.WriteRuntime(runtimeRoot, "2.1.30");
        HostRuntimeTests.WriteRuntime(runtimeRoot, "6.0.36");
        var runtime = HostRuntime.ResolveFromRoot(runtimeRoot);
        string configPath = Path.Combine(game.Root, "UserData", "Loader.cfg");

        LoaderConfig.Update(configPath, runtime);

        string searchDirectory = new TomlParser().Parse(File.ReadAllText(configPath))
            .GetSubTable("loader").GetString("hostfxr_path_override");
        Assert.True(Directory.Exists(searchDirectory));
        // This is the recursive discovery used by MelonLoader 0.7.3. It must not
        // encounter the incompatible .NET 2.1 host beside the selected runtime.
        Assert.Equal(runtime.HostFxrPath,
            Assert.Single(Directory.GetFiles(searchDirectory, "hostfxr.dll", SearchOption.AllDirectories)));
        Assert.Equal(new Version(6, 0, 36), runtime.Version);
    }
}
