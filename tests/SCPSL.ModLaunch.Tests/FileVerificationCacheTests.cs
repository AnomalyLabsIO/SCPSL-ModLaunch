using System.Text.Json.Nodes;
using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class FileVerificationCacheTests
{
    [Fact]
    public void UnchangedFileReusesItsVerifiedDigestInMemoryAndAcrossLaunches()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "GameAssembly.dll");
        string expected = FileOps.Hash(path);
        var cache = new FileVerificationCache(game.Root);

        Assert.Equal(expected, cache.Hash(path));
        Assert.Equal(1L, cache.HashedFiles);
        Assert.Equal(0L, cache.CachedFiles);
        Assert.Equal(expected, cache.Hash(path));
        Assert.Equal(1L, cache.HashedFiles);
        Assert.Equal(1L, cache.CachedFiles);
        cache.Save();

        var nextLaunch = new FileVerificationCache(game.Root);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(nextLaunch.Matches(path, expected));
        Assert.Equal(0L, nextLaunch.HashedFiles);
        Assert.Equal(1L, nextLaunch.CachedFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SizeOrModifiedTimeChangeRehashesTheFile(bool sameLength)
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "GameAssembly.dll");
        var cache = new FileVerificationCache(game.Root);
        string original = cache.Hash(path);
        DateTime lastWrite = File.GetLastWriteTimeUtc(path);
        byte[] changed = Enumerable.Repeat((byte)0x77, (int)new FileInfo(path).Length + (sameLength ? 0 : 1)).ToArray();
        File.WriteAllBytes(path, changed);
        File.SetLastWriteTimeUtc(path, sameLength ? lastWrite.AddDays(1) : lastWrite);

        Assert.False(cache.Matches(path, original));
        Assert.Equal(FileOps.Hash(path), cache.Hash(path));
        Assert.Equal(2L, cache.HashedFiles);
        Assert.Equal(1L, cache.CachedFiles);
    }

    [Fact]
    public void MetadataChangeWithIdenticalContentsRefreshesTheRecordWithoutChangingIdentity()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "GameAssembly.dll");
        var cache = new FileVerificationCache(game.Root);
        string expected = cache.Hash(path);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddDays(1));

        Assert.True(cache.Matches(path, expected));
        Assert.Equal(2L, cache.HashedFiles);
        cache.Save();
        var nextLaunch = new FileVerificationCache(game.Root);
        Assert.True(nextLaunch.Matches(path, expected));
        Assert.Equal(0L, nextLaunch.HashedFiles);
        Assert.Equal(1L, nextLaunch.CachedFiles);
    }

    [Fact]
    public void ChangedCreationTimeRequiresAContentRead()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "GameAssembly.dll");
        var cache = new FileVerificationCache(game.Root);
        string expected = cache.Hash(path);
        DateTime originalCreation = File.GetCreationTimeUtc(path);
        File.SetCreationTimeUtc(path, originalCreation.AddDays(-1));

        Assert.NotEqual(originalCreation, File.GetCreationTimeUtc(path));
        Assert.True(cache.Matches(path, expected));
        Assert.Equal(2L, cache.HashedFiles);
        Assert.Equal(0L, cache.CachedFiles);
    }

    [Fact]
    public void ExpectedDigestMismatchIsRejectedEvenWhenTheMetadataMatches()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "GameAssembly.dll");
        var cache = new FileVerificationCache(game.Root);
        string verified = cache.Hash(path);
        string different = new(verified[0] == 'A' ? 'B' : 'A', 64);

        Assert.False(cache.Matches(path, different));
        Assert.True(cache.Matches(path, verified.ToLowerInvariant()));
        Assert.Equal(1L, cache.HashedFiles);
        Assert.Equal(2L, cache.CachedFiles);
    }

    [Fact]
    public void MissingFileCannotBeAcceptedUsingItsSavedRecord()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "GameAssembly.dll");
        var cache = new FileVerificationCache(game.Root);
        string expected = cache.Hash(path);
        File.Delete(path);

        Assert.False(cache.Matches(path, expected));
        cache.Save();
        game.Write("GameAssembly.dll", game.Originals["GameAssembly.dll"]);
        var nextLaunch = new FileVerificationCache(game.Root);
        Assert.True(nextLaunch.Matches(path, expected));
        Assert.Equal(1L, nextLaunch.HashedFiles);
        Assert.Equal(0L, nextLaunch.CachedFiles);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("different-game")]
    [InlineData("invalid-record")]
    public void UnusableSavedStateFallsBackToAFullHash(string defect)
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "GameAssembly.dll");
        string state = Path.Combine(game.Root, ".modlaunch", "file-verification.json");
        var cache = new FileVerificationCache(game.Root);
        string expected = cache.Hash(path);
        cache.Save();
        switch (defect)
        {
            case "missing": File.Delete(state); break;
            case "corrupt": File.WriteAllText(state, "{ interrupted cache write"); break;
            case "null": File.WriteAllText(state, "null"); break;
            default:
                JsonNode document = JsonNode.Parse(File.ReadAllText(state))!;
                if (defect == "version") document["schemaVersion"] = 999;
                else if (defect == "different-game") document["gameDirectory"] = Path.Combine(game.Root, "different-game");
                else document["files"]![0]!["relativePath"] = "../outside.dll";
                File.WriteAllText(state, document.ToJsonString());
                break;
        }

        var nextLaunch = new FileVerificationCache(game.Root);
        Assert.True(nextLaunch.Matches(path, expected));
        Assert.Equal(1L, nextLaunch.HashedFiles);
        Assert.Equal(0L, nextLaunch.CachedFiles);
    }

    [Fact]
    public void ExplicitInvalidationPersistsAndRequiresAnotherContentRead()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "GameAssembly.dll");
        var cache = new FileVerificationCache(game.Root);
        string expected = cache.Hash(path);
        cache.Save();
        cache.Invalidate(path);
        cache.Save();

        var nextLaunch = new FileVerificationCache(game.Root);
        Assert.True(nextLaunch.Matches(path, expected));
        Assert.Equal(1L, nextLaunch.HashedFiles);
        Assert.Equal(0L, nextLaunch.CachedFiles);
    }

    [Fact]
    public void SavePrunesVanishedRecordsEvenWhenNoLookupInvalidatedThem()
    {
        using var game = new TestGame();
        var cache = new FileVerificationCache(game.Root);
        cache.Hash(Path.Combine(game.Root, "GameAssembly.dll"));
        cache.Hash(Path.Combine(game.Root, "UnityPlayer.dll"));
        cache.Save();
        File.Delete(Path.Combine(game.Root, "GameAssembly.dll"));
        cache.Save();

        JsonNode document = JsonNode.Parse(File.ReadAllText(Path.Combine(game.Root, ".modlaunch", "file-verification.json")))!;
        Assert.Equal("UnityPlayer.dll", Assert.Single(document["files"]!.AsArray())!["relativePath"]!.GetValue<string>());
    }

    [Fact]
    public void TemporarySessionPayloadsAlwaysHashAndAreNeverPersisted()
    {
        using var game = new TestGame();
        const string relative = ".modlaunch/sessions/fixture/bootstrap.bin";
        game.Write(relative, [1, 2, 3]);
        string path = Path.Combine(game.Root, relative);
        var cache = new FileVerificationCache(game.Root);
        cache.Hash(Path.Combine(game.Root, "GameAssembly.dll"));
        Assert.Equal(FileOps.Hash(path), cache.HashFresh(path));
        Assert.Equal(FileOps.Hash(path), cache.Hash(path));
        Assert.Equal(3L, cache.HashedFiles);
        Assert.Equal(0L, cache.CachedFiles);
        cache.Save();

        JsonNode document = JsonNode.Parse(File.ReadAllText(Path.Combine(game.Root, ".modlaunch", "file-verification.json")))!;
        Assert.Equal("GameAssembly.dll", Assert.Single(document["files"]!.AsArray())!["relativePath"]!.GetValue<string>());
    }

    [Fact]
    public void FreshVerificationDetectsChangesThatPreserveTheMetadataTuple()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "GameAssembly.dll");
        var cache = new FileVerificationCache(game.Root);
        string original = cache.Hash(path);
        DateTime originalCreation = File.GetCreationTimeUtc(path);
        DateTime originalWrite = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x77, (int)new FileInfo(path).Length).ToArray());
        File.SetLastWriteTimeUtc(path, originalWrite);
        Assert.Equal(originalCreation, File.GetCreationTimeUtc(path));

        // Metadata reuse is an update-detection heuristic, not content integrity proof.
        Assert.Equal(original, cache.Hash(path));
        string changed = cache.HashFresh(path);
        Assert.NotEqual(original, changed);
        Assert.Equal(FileOps.Hash(path), changed);
        Assert.Equal(changed, cache.Hash(path));
        Assert.Equal(2L, cache.HashedFiles);
        Assert.Equal(2L, cache.CachedFiles);
    }
}
