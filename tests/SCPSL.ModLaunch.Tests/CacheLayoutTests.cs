using System.Security.Cryptography;
using System.Text;
using SCPSL.ModLaunch.Core;
using SCPSL.ModLaunch.ModPatch.Headless;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class CacheLayoutTests
{
    [Fact]
    public void UpgradeNamesOriginalsAndConsolidatesToolsResourcesAndTemporaryWork()
    {
        using var game = new TestGame();
        string cache = Path.Combine(game.Root, ".modlaunch");
        foreach (string relative in game.Originals.Keys)
        {
            string hash = FileOps.Hash(Path.Combine(game.Root, relative));
            game.Write($".modlaunch/originals/{hash}.bin", game.Originals[relative]);
        }
        game.Write(".modlaunch/tools/adapter-bundled/patcher.exe", [1]);
        game.Write(".modlaunch/resources/" + new string('a', 40) + "/patchinfo.json", [2]);
        game.Write(".modlaunch/work/unfinished/game/SCPSL.exe", [3]);
        CacheLayout.Upgrade(game.Root);
        CacheLayout.Upgrade(game.Root);
        foreach (string relative in game.Originals.Keys)
        {
            string hash = FileOps.Hash(Path.Combine(game.Root, relative));
            string renamed = Path.Combine(cache, CacheLayout.OriginalFile(relative, hash));
            Assert.Equal(game.Originals[relative], File.ReadAllBytes(renamed));
        }
        Assert.True(File.Exists(Path.Combine(cache, "patching-tools/bundled/patcher.exe")));
        Assert.True(File.Exists(Path.Combine(cache, "downloads/patch-definitions/ModPatch-aaaaaaaaaaaa/patchinfo.json")));
        Assert.True(File.Exists(Path.Combine(cache, "temporary/preparation-unfinished/game/SCPSL.exe")));
        foreach (string old in new[] { "originals", "tools", "resources", "work" }) Assert.False(Directory.Exists(Path.Combine(cache, old)));
        Assert.Equal("user owned Mods/user.dll", File.ReadAllText(Path.Combine(game.Root, "Mods/user.dll")));
    }

    [Fact]
    public void MigrationDoesNotOverwriteConflictingSavedFiles()
    {
        using var game = new TestGame();
        game.Write(".modlaunch/tools/adapter-bundled/patcher.exe", [1]);
        game.Write(".modlaunch/patching-tools/bundled/patcher.exe", [2]);
        Assert.Throws<IOException>(() => CacheLayout.Upgrade(game.Root));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(game.Root, ".modlaunch/tools/adapter-bundled/patcher.exe")));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(game.Root, ".modlaunch/patching-tools/bundled/patcher.exe")));
    }

    [Fact]
    public void MigrationPreservesAnUnidentifiedOlderOriginalUnderADescriptiveName()
    {
        using var game = new TestGame();
        byte[] previous = Encoding.UTF8.GetBytes("older original game file");
        string hash = Convert.ToHexString(SHA256.HashData(previous));
        game.Write($".modlaunch/originals/{hash}.bin", previous);
        CacheLayout.Upgrade(game.Root);
        Assert.Equal(previous, File.ReadAllBytes(Path.Combine(game.Root, ".modlaunch", CacheLayout.OriginalFile("unidentified-game-file", hash))));
    }

    [Theory]
    [InlineData("https://github.com/SamboyCoding/Cpp2IL/releases/download/2022.1.0-pre-release.21/Cpp2IL-2022.1.0-pre-release.21-Windows.exe", "Cpp2IL-2022.1.0-pre-release.21-Windows.exe.download")]
    [InlineData("https://github.com/SamboyCoding/Cpp2IL/releases/download/2022.1.0-pre-release.21/Cpp2IL.Plugin.StrippedCodeRegSupport.dll", "Cpp2IL-2022.1.0-pre-release.21-StrippedCodeRegSupport.dll.download")]
    [InlineData("https://github.com/LavaGang/MelonLoader.UnityDependencies/releases/download/6000.0.43f1/Managed.zip", "Unity-6000.0.43f1-managed.zip")]
    [InlineData("https://raw.githubusercontent.com/AnomalyLabsIO/SCPSL-ModPatch/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/Shared_Files/clean_launchers/6000.0.43f1/SCPSL.exe", "Unity-6000.0.43f1-clean-SCPSL-aaaaaaaaaaaa.exe.download")]
    public void DownloadsKeepTheirProductVersionAndInactiveFileType(string url, string expected)
    {
        Assert.Equal(expected, ToolDownloads.CacheName(new Uri(url)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingHashNamedDownloadsAreVerifiedAndReusedWithoutNetworkAccess(bool nested)
    {
        using var game = new TestGame();
        string downloads = Path.Combine(game.Root, ".modlaunch/downloads");
        string oldRoot = nested ? Path.Combine(downloads, "adapter-dependencies") : downloads;
        Directory.CreateDirectory(oldRoot);
        const string url = "https://github.com/LavaGang/MelonLoader.UnityDependencies/releases/download/6000.0.43f1/Managed.zip";
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        string original = Path.Combine(oldRoot, key + ".bin");
        File.WriteAllText(original, "cached download fixture");
        string hash = FileOps.Hash(original);
        File.WriteAllText(original + ".sha256", hash);
        ToolDownloads.CacheDirectory = downloads;
        string current = ToolDownloads.Download(url, hash);
        Assert.Equal(Path.Combine(downloads, "Unity-6000.0.43f1-managed.zip"), current);
        Assert.Equal(hash, FileOps.Hash(current));
        Assert.False(File.Exists(original));
        Assert.False(File.Exists(original + ".sha256"));
    }
}
