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
        Assert.Equal(2, Directory.GetFiles(cache, "version.dll.*.bin", SearchOption.AllDirectories).Length);
        Assert.All(Directory.GetFiles(cache, "version.dll.*.bin", SearchOption.AllDirectories), file => Assert.Equal(new byte[] { 2, 3 }, File.ReadAllBytes(file)));
    }

    [Fact]
    public void SuccessfulPreparationKeepsCurrentPayloadsOriginalsAndUsersButRemovesDuplicates()
    {
        using var game = new TestGame();
        foreach (string path in new[] { ".modlaunch/prepared/current/payloads/000.bin", ".modlaunch/prepared/old/game/old.bin", ".modlaunch/originals/keep.bin", ".modlaunch/tools/melonloader-0.7.2/MelonLoader/net6/old.dll", ".modlaunch/tools/dotnet-6.0.36/old.bin", ".modlaunch/downloads/dotnet-runtime-6.0.36-win-x64.zip", ".modlaunch/work/failed/game/junk.bin", ".modlaunch/sessions/completed/bootstrap.bin" }) game.Write(path, [99]);
        CacheMaintenance.AfterSuccessfulPreparation(game.Root, new PreparedSession { PreparedDirectory = Path.Combine(game.Root, ".modlaunch/prepared/current/payloads"), DotnetRoot = Path.Combine(game.Root, "host-runtime") });
        Assert.True(File.Exists(Path.Combine(game.Root, ".modlaunch/prepared/current/payloads/000.bin")));
        Assert.True(File.Exists(Path.Combine(game.Root, ".modlaunch/originals/keep.bin")));
        Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch/prepared/old")));
        Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch/tools/melonloader-0.7.2")));
        Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch/tools/dotnet-6.0.36")));
        Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch/downloads/dotnet-runtime-6.0.36-win-x64.zip")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(game.Root, ".modlaunch/sessions")));
        Assert.Equal("user owned Mods/user.dll", File.ReadAllText(Path.Combine(game.Root, "Mods/user.dll")));
    }

    [Fact]
    public void RetentionPinsCurrentAdapterAndBoundsOrphansDownloadsAndLogs()
    {
        using var game = new TestGame();
        string currentAdapter = "adapter-" + new string('a', 64);
        string orphanAdapter = "adapter-" + new string('b', 64);
        game.Write(".modlaunch/prepared/current/payloads/000.bin", [1]);
        game.Write(".modlaunch/downloads/" + currentAdapter + ".zip", [2]);
        game.Write(".modlaunch/downloads/" + orphanAdapter + ".zip", [3]);
        game.Write(".modlaunch/downloads/unfinished.part", [4]);
        for (int i = 0; i < 10; i++)
        {
            string file = ".modlaunch/downloads/adapter-dependencies/" + i.ToString("x64") + ".bin";
            game.Write(file, [5]);
            game.Write(file + ".sha256", [6]);
            File.SetLastWriteTimeUtc(Path.Combine(game.Root, file), DateTime.UtcNow.AddDays(-20 + i));
        }
        for (int i = 0; i < 25; i++) game.Write($".modlaunch/logs/{i}.log", [7]);
        CacheMaintenance.AfterSuccessfulPreparation(game.Root, new PreparedSession
        {
            PreparedDirectory = Path.Combine(game.Root, ".modlaunch/prepared/current/payloads"),
            DotnetRoot = Path.Combine(game.Root, "host-runtime"),
            AdapterDirectory = Path.Combine(game.Root, ".modlaunch/tools/" + currentAdapter)
        });
        Assert.True(File.Exists(Path.Combine(game.Root, ".modlaunch/downloads/" + currentAdapter + ".zip")));
        Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch/downloads/" + orphanAdapter + ".zip")));
        Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch/downloads/unfinished.part")));
        Assert.Equal(8, Directory.GetFiles(Path.Combine(game.Root, ".modlaunch/downloads/adapter-dependencies"), "*.bin").Length);
        Assert.Equal(8, Directory.GetFiles(Path.Combine(game.Root, ".modlaunch/downloads/adapter-dependencies"), "*.sha256").Length);
        Assert.Equal(20, Directory.GetFiles(Path.Combine(game.Root, ".modlaunch/logs")).Length);
    }
}
