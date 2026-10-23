using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class SessionVerificationCacheTests
{
    [Fact]
    public void SuccessfulRestorationPersistsTheReplacementFilesNewMetadata()
    {
        using var game = new TestGame();
        SetOriginalModificationTimes(game);
        var prepared = game.Prepare();
        var verification = SeedOriginals(game);
        var transaction = new SessionTransaction(game.Root, verification: verification);
        var journal = transaction.Create(prepared);
        transaction.Activate(journal);

        var restored = transaction.Restore(journal);
        Assert.True(restored.Success, string.Join(Environment.NewLine, restored.Failures));
        long readsAfterRestoration = verification.HashedFiles;
        AssertOriginalsAreCached(game, verification);
        Assert.Equal(readsAfterRestoration, verification.HashedFiles);

        var nextLaunch = new FileVerificationCache(game.Root);
        AssertOriginalsAreCached(game, nextLaunch);
        Assert.Equal(0L, nextLaunch.HashedFiles);
        Assert.Equal((long)game.Originals.Count, nextLaunch.CachedFiles);
    }

    [Fact]
    public void RecoveryAfterPartiallyInterruptedRestorationPersistsTheFinalMetadata()
    {
        using var game = new TestGame();
        SetOriginalModificationTimes(game);
        var prepared = game.Prepare();
        var verification = SeedOriginals(game);
        var transaction = new SessionTransaction(game.Root, verification: verification);
        var journal = transaction.Create(prepared);
        transaction.Activate(journal);
        var interrupted = new SessionTransaction(game.Root, checkpoint: point =>
        {
            if (point == "after-restore:GameAssembly.dll") throw new SimulatedInterruption();
        }, verification: verification);
        Assert.Throws<SimulatedInterruption>(() => interrupted.Restore(journal));
        Assert.Equal(game.Originals["GameAssembly.dll"], File.ReadAllBytes(Path.Combine(game.Root, "GameAssembly.dll")));
        Assert.Equal("prepared fixture SCPSL.exe", File.ReadAllText(Path.Combine(game.Root, "SCPSL.exe")));

        var recoveryCache = new FileVerificationCache(game.Root);
        var recovery = new SessionTransaction(game.Root, verification: recoveryCache);
        var restored = recovery.Restore(Assert.IsType<SessionJournal>(recovery.LoadPending()));
        Assert.True(restored.Success, string.Join(Environment.NewLine, restored.Failures));
        Assert.Null(recovery.LoadPending());

        var nextLaunch = new FileVerificationCache(game.Root);
        AssertOriginalsAreCached(game, nextLaunch);
        Assert.Equal(0L, nextLaunch.HashedFiles);
        Assert.Equal((long)game.Originals.Count, nextLaunch.CachedFiles);
    }

    [Theory]
    [InlineData("GameAssembly.dll")]
    [InlineData("version.dll")]
    public void RestorationAndDeletionRequireFreshHashesDespiteMatchingCachedMetadata(string relative)
    {
        using var game = new TestGame();
        var verification = new FileVerificationCache(game.Root);
        var transaction = new SessionTransaction(game.Root, verification: verification);
        var journal = transaction.Create(game.Prepare());
        transaction.Activate(journal);
        string path = Path.Combine(game.Root, relative);
        string activeHash = verification.Hash(path);
        byte[] changed = ChangeContentsPreservingMetadata(path);
        Assert.Equal(activeHash, verification.Hash(path));

        var restored = transaction.Restore(journal);
        Assert.False(restored.Success);
        Assert.Contains(restored.Failures, failure => failure.Contains(relative, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(changed, File.ReadAllBytes(path));
        Assert.NotNull(transaction.LoadPending());
        Assert.Equal("prepared fixture SCPSL.exe", File.ReadAllText(Path.Combine(game.Root, "SCPSL.exe")));
    }

    [Fact]
    public void CorruptBackupCannotBeRestoredUsingAMatchingMetadataRecord()
    {
        using var game = new TestGame();
        var verification = new FileVerificationCache(game.Root);
        var transaction = new SessionTransaction(game.Root, verification: verification);
        var journal = transaction.Create(game.Prepare());
        string backup = Path.Combine(game.Root, ".modlaunch", journal.Entries.Single(entry => entry.RelativePath == "GameAssembly.dll").BackupPath!);
        string originalHash = verification.Hash(backup);
        ChangeContentsPreservingMetadata(backup);
        Assert.Equal(originalHash, verification.Hash(backup));
        transaction.Activate(journal);

        var restored = transaction.Restore(journal);
        Assert.False(restored.Success);
        Assert.Contains(restored.Failures, failure => failure.Contains("GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("prepared fixture GameAssembly.dll", File.ReadAllText(Path.Combine(game.Root, "GameAssembly.dll")));
        Assert.NotNull(transaction.LoadPending());
    }

    [Fact]
    public void PreparedCopySourceIsFullyVerifiedBeforeCreatingTheTransaction()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        string source = Path.Combine(prepared.PreparedDirectory, prepared.Files.Single(file => file.RelativePath == "GameAssembly.dll").SourcePath!);
        var verification = new FileVerificationCache(game.Root);
        string preparedHash = verification.Hash(source);
        ChangeContentsPreservingMetadata(source);
        Assert.Equal(preparedHash, verification.Hash(source));

        var transaction = new SessionTransaction(game.Root, verification: verification);
        Assert.Throws<InvalidDataException>(() => transaction.Create(prepared));
        Assert.Null(transaction.LoadPending());
        Assert.Equal(game.Originals["GameAssembly.dll"], File.ReadAllBytes(Path.Combine(game.Root, "GameAssembly.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
    }

    private static void SetOriginalModificationTimes(TestGame game)
    {
        foreach (string relative in game.Originals.Keys)
            File.SetLastWriteTimeUtc(Path.Combine(game.Root, relative), DateTime.UtcNow.AddDays(-3));
    }

    private static FileVerificationCache SeedOriginals(TestGame game)
    {
        var cache = new FileVerificationCache(game.Root);
        foreach (string relative in game.Originals.Keys) cache.Hash(Path.Combine(game.Root, relative));
        cache.Save();
        return cache;
    }

    private static void AssertOriginalsAreCached(TestGame game, FileVerificationCache cache)
    {
        foreach (var (relative, original) in game.Originals)
        {
            string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original));
            Assert.True(cache.Matches(Path.Combine(game.Root, relative), expected));
        }
    }

    private static byte[] ChangeContentsPreservingMetadata(string path)
    {
        DateTime created = File.GetCreationTimeUtc(path);
        DateTime written = File.GetLastWriteTimeUtc(path);
        byte[] changed = Enumerable.Repeat((byte)0x77, (int)new FileInfo(path).Length).ToArray();
        File.WriteAllBytes(path, changed);
        File.SetLastWriteTimeUtc(path, written);
        Assert.Equal(created, File.GetCreationTimeUtc(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        return changed;
    }

    private sealed class SimulatedInterruption : Exception { }
}
