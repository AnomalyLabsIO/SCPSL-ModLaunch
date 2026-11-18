using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class CacheMaintenanceTests
{
    [Fact]
    public void UnfinishedJournalForbidsAllCacheCleanupAndNeutralization()
    {
        using var game = new TestGame();
        game.Write(".modlaunch/session.json", [1]);
        game.Write(".modlaunch/prepared/old/game/version.dll", [2]);
        Assert.Throws<InvalidOperationException>(() => CacheMaintenance.BeforePreparation(game.Root));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(game.Root, ".modlaunch/prepared/old/game/version.dll")));
    }

    [Fact]
    public void LegacyBootstrapNamesBecomeNeutralWithoutLosingTheirBytes()
    {
        using var game = new TestGame();
        game.Write(".modlaunch/prepared/old/game/version.dll", [2, 3]);
        game.Write(".modlaunch/tools/melonloader-0.7.2/version.dll", [2, 3]);
        CacheMaintenance.BeforePreparation(game.Root);
        string cache = Path.Combine(game.Root, ".modlaunch");
        Assert.Empty(Directory.GetFiles(cache, "version.dll", SearchOption.AllDirectories));
        Assert.Equal(2, Directory.GetFiles(cache, "version.dll.*.disabled", SearchOption.AllDirectories).Length);
        Assert.All(Directory.GetFiles(cache, "version.dll.*.disabled", SearchOption.AllDirectories), file => Assert.Equal(new byte[] { 2, 3 }, File.ReadAllBytes(file)));
    }

    [Fact]
    public void SuccessfulPreparationKeepsNamedGameFilesAndUsersButRemovesDuplicates()
    {
        using var game = new TestGame();
        foreach (string path in new[] { ".modlaunch/modded-game-files/SCPSL.exe.modded", ".modlaunch/prepared/old/game/old.bin",
                     ".modlaunch/original-game-files/SCPSL.exe.fixture.original", ".modlaunch/tools/melonloader-0.7.2/MelonLoader/net6/old.dll",
                     ".modlaunch/tools/dotnet-6.0.36/old.bin", ".modlaunch/downloads/dotnet-runtime-6.0.36-win-x64.zip",
                     ".modlaunch/work/failed/game/junk.bin", ".modlaunch/sessions/completed/bootstrap.bin" }) game.Write(path, [99]);
        CacheMaintenance.AfterSuccessfulPreparation(game.Root, new PreparedSession
        {
            PreparedDirectory = Path.Combine(game.Root, ".modlaunch/modded-game-files"),
            DotnetRoot = Path.Combine(game.Root, "host-runtime")
        });
        Assert.True(File.Exists(Path.Combine(game.Root, ".modlaunch/modded-game-files/SCPSL.exe.modded")));
        Assert.True(File.Exists(Path.Combine(game.Root, ".modlaunch/original-game-files/SCPSL.exe.fixture.original")));
        foreach (string removed in new[] { "prepared", "work", "sessions", "temporary", "tools" })
            Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch", removed)));
        Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch/patching-tools/melonloader-0.7.2")));
        Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch/patching-tools/dotnet-6.0.36")));
        Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch/downloads/dotnet-runtime-6.0.36-win-x64.zip")));
        Assert.Equal("user owned Mods/user.dll", File.ReadAllText(Path.Combine(game.Root, "Mods/user.dll")));
    }

    [Fact]
    public void SuccessfulPreparationDiscardsConsumedDownloadsAndBoundsLogs()
    {
        using var game = new TestGame();
        string current = new string('A', 64);
        string orphan = new string('B', 64);
        game.Write(".modlaunch/modded-game-files/SCPSL.exe.modded", [1]);
        game.Write(".modlaunch/" + CacheLayout.AdapterDownload(current), [2]);
        game.Write(".modlaunch/" + CacheLayout.AdapterDownload(orphan), [3]);
        game.Write(".modlaunch/downloads/unfinished.part", [4]);
        foreach (string name in new[] { "MelonLoader-0.7.3.x64.zip", "MelonLoader-0.7.3-native-bootstrap-PR13.zip",
                     "Unity-6000.0.43-managed.zip", "Unity-6000.0.43-managed.zip.sha256", "native-bootstrap-pr13-old.zip" })
            game.Write(".modlaunch/downloads/" + name, [8]);
        game.Write(".modlaunch/downloads/patch-definitions/ModPatch-current/patchinfo.json", [9]);
        game.Write(".modlaunch/downloads/unrecognized-user-file.zip", [10]);
        for (int i = 0; i < 10; i++)
        {
            string file = ".modlaunch/downloads/Cpp2IL-version-" + i + ".exe.download";
            game.Write(file, [5]);
            game.Write(file + ".sha256", [6]);
            File.SetLastWriteTimeUtc(Path.Combine(game.Root, file), DateTime.UtcNow.AddDays(-20 + i));
        }
        for (int i = 0; i < 25; i++) game.Write($".modlaunch/logs/{i}.log", [7]);
        CacheMaintenance.AfterSuccessfulPreparation(game.Root, new PreparedSession
        {
            PreparedDirectory = Path.Combine(game.Root, ".modlaunch/modded-game-files"),
            DotnetRoot = Path.Combine(game.Root, "host-runtime"),
            AdapterDirectory = Path.Combine(game.Root, ".modlaunch", CacheLayout.AdapterFolder(current))
        });
        Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch", CacheLayout.AdapterDownload(current))));
        Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch", CacheLayout.AdapterDownload(orphan))));
        Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch/downloads/unfinished.part")));
        Assert.Empty(Directory.GetFiles(Path.Combine(game.Root, ".modlaunch/downloads"), "*.download"));
        Assert.Empty(Directory.GetFiles(Path.Combine(game.Root, ".modlaunch/downloads"), "*.sha256"));
        Assert.Equal("unrecognized-user-file.zip", Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(game.Root, ".modlaunch/downloads")))));
        Assert.True(File.Exists(Path.Combine(game.Root, ".modlaunch/downloads/patch-definitions/ModPatch-current/patchinfo.json")));
        Assert.Equal(20, Directory.GetFiles(Path.Combine(game.Root, ".modlaunch/logs")).Length);
    }
}
