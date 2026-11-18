using System.Text.Json.Nodes;
using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class SessionLaunchModeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullyActiveSessionCanBeReloadedReusedAndRestored(bool skipMelonLoader)
    {
        using var game = new TestGame();
        var prepared = Prepare(game, skipMelonLoader);
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        Assert.Equal(skipMelonLoader ? 6 : 5, journal.SchemaVersion);
        transaction.Activate(journal);
        // Generated runtime files and user configuration can change during a retained session.
        game.Write("MelonLoader/Il2CppAssemblies/Generated.dll", [4, 5, 6]);
        game.Write("UserData/Loader.cfg", [7, 8, 9]);
        var files = Snapshot(game);

        var resumed = new SessionTransaction(game.Root);
        var retained = Assert.IsType<SessionJournal>(resumed.LoadPending());
        Assert.Equal(prepared.DotnetRoot, retained.DotnetRoot);
        Assert.Equal(skipMelonLoader, retained.SkipMelonLoader);
        resumed.VerifyActive(retained);
        resumed.VerifyActive(retained);
        Assert.Equal(files, Snapshot(game));
        Assert.Equal("prepared fixture SCPSL.exe", File.ReadAllText(Path.Combine(game.Root, "SCPSL.exe")));
        Assert.Equal(!skipMelonLoader, File.Exists(Path.Combine(game.Root, "version.dll")));

        AssertRestored(game, resumed.Restore(retained));
        Assert.Null(resumed.LoadPending());
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(game.Root, "MelonLoader/Il2CppAssemblies/Generated.dll")));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(game.Root, "UserData/Loader.cfg")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteActivationCannotBeReusedAndRemainsRecoverable(bool skipMelonLoader)
    {
        using var game = new TestGame();
        var interrupted = new SessionTransaction(game.Root, checkpoint: point =>
        {
            if (point == "before-activate:SCPSL.exe") throw new SimulatedInterruption();
        });
        var journal = interrupted.Create(Prepare(game, skipMelonLoader));
        Assert.Throws<SimulatedInterruption>(() => interrupted.Activate(journal));
        Assert.Equal(game.Originals["SCPSL.exe"], File.ReadAllBytes(Path.Combine(game.Root, "SCPSL.exe")));
        var files = Snapshot(game);

        var recovery = new SessionTransaction(game.Root);
        var pending = Assert.IsType<SessionJournal>(recovery.LoadPending());
        Assert.Throws<InvalidDataException>(() => recovery.VerifyActive(pending));
        Assert.Equal(files, Snapshot(game));
        AssertRestored(game, recovery.Restore(pending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedRestorationCannotBeReusedAndCanFinish(bool skipMelonLoader)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(Prepare(game, skipMelonLoader));
        transaction.Activate(journal);
        var interrupted = new SessionTransaction(game.Root, checkpoint: point =>
        {
            if (point == "after-restore:GameAssembly.dll") throw new SimulatedInterruption();
        });
        Assert.Throws<SimulatedInterruption>(() => interrupted.Restore(journal));

        var recovery = new SessionTransaction(game.Root);
        var pending = Assert.IsType<SessionJournal>(recovery.LoadPending());
        Assert.Throws<InvalidDataException>(() => recovery.VerifyActive(pending));
        AssertRestored(game, recovery.Restore(pending));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("observed")]
    [InlineData("backup")]
    [InlineData("parked")]
    public void ActiveVerificationRehashesInstalledFilesBackupsAndParkedOriginals(string changed)
    {
        using var game = new TestGame();
        var verification = new FileVerificationCache(game.Root);
        var transaction = new SessionTransaction(game.Root, verification: verification);
        var journal = transaction.Create(Prepare(game, false));
        transaction.Activate(journal);
        var assembly = journal.Entries.Single(entry => entry.RelativePath == "GameAssembly.dll");
        var anticheat = journal.Entries.Single(entry => entry.RelativePath == "SL-AC.dll");
        string path = changed switch
        {
            "observed" => Path.Combine(game.Root, "UnityPlayer.dll"),
            "backup" => Path.Combine(game.Root, ".modlaunch", assembly.BackupPath!),
            "parked" => Path.Combine(game.Root, ".modlaunch", anticheat.ParkedPath!),
            _ => Path.Combine(game.Root, "GameAssembly.dll")
        };
        string cachedHash = verification.Hash(path);
        DateTime written = File.GetLastWriteTimeUtc(path);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xff;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, written);
        // Temporary parked files never participate in metadata reuse; installed files and
        // immutable backups must still be rehashed even when the cache stamp matches.
        if (changed != "parked") Assert.Equal(cachedHash, verification.Hash(path));
        verification.Save();
        var files = Snapshot(game);

        Assert.Throws<InvalidDataException>(() => transaction.VerifyActive(journal));
        Assert.Equal(files, Snapshot(game));
        Assert.NotNull(transaction.LoadPending());
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("parked")]
    public void RetainedSessionRejectsMissingOrInvalidRecoveryFiles(string changed)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(Prepare(game, false));
        transaction.Activate(journal);
        var anticheat = journal.Entries.Single(entry => entry.RelativePath == "SL-AC.dll");
        string path = Path.Combine(game.Root, ".modlaunch", changed == "backup" ? anticheat.BackupPath! : anticheat.ParkedPath!);
        if (changed == "backup") File.Delete(path);
        else
        {
            File.Delete(path);
            Directory.CreateDirectory(path);
        }

        Assert.Throws<InvalidDataException>(() => transaction.VerifyActive(journal));
        Assert.NotNull(transaction.LoadPending());
        Assert.Equal("prepared fixture SCPSL.exe", File.ReadAllText(Path.Combine(game.Root, "SCPSL.exe")));
    }

    [Fact]
    public void MissingParkedOriginalStillAllowsReuseAndBackupRestoration()
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(Prepare(game, false));
        transaction.Activate(journal);
        var anticheat = journal.Entries.Single(entry => entry.RelativePath == "SL-AC.dll");
        File.Delete(Path.Combine(game.Root, ".modlaunch", anticheat.ParkedPath!));

        transaction.VerifyActive(journal);
        AssertRestored(game, transaction.Restore(journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReappearingAnticheatCannotBeReusedOrOverwritten(bool skipMelonLoader)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(Prepare(game, skipMelonLoader));
        transaction.Activate(journal);
        game.Write("SL-AC.dll", [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => transaction.VerifyActive(journal));
        var result = transaction.Restore(journal);
        Assert.False(result.Success);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(game.Root, "SL-AC.dll")));
        Assert.Equal("prepared fixture SCPSL.exe", File.ReadAllText(Path.Combine(game.Root, "SCPSL.exe")));
    }

    [Fact]
    public void SkipMelonLoaderDoesNotNeedOrStageABootstrap()
    {
        using var game = new TestGame();
        var prepared = Prepare(game, true);
        File.Delete(prepared.MelonLoaderArchive);
        prepared.BootstrapHash = "";
        prepared.BootstrapArchive = "missing native archive";
        prepared.BootstrapArchiveSha256 = "invalid hash that must not be read";
        var transaction = new SessionTransaction(game.Root);

        var journal = transaction.Create(prepared);
        var bootstrap = journal.Entries.Single(entry => entry.RelativePath == "version.dll");
        Assert.Equal(FileAction.Observe, bootstrap.Action);
        Assert.Null(bootstrap.OriginalHash);
        Assert.Null(bootstrap.PreparedHash);
        Assert.Null(bootstrap.PreparedSource);
        Assert.Empty(Directory.GetFiles(Path.Combine(game.Root, ".modlaunch"), "version.dll*", SearchOption.AllDirectories));
        transaction.Activate(journal);
        transaction.VerifyActive(journal);
        AssertRestored(game, transaction.Restore(journal));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public void SkipMelonLoaderRefusesAnUnexpectedBootstrap(string when)
    {
        using var game = new TestGame();
        var prepared = Prepare(game, true);
        var transaction = new SessionTransaction(game.Root);
        if (when == "before")
        {
            game.Write("version.dll", [7, 8, 9]);
            Assert.Throws<InvalidDataException>(() => transaction.Create(prepared));
            Assert.Null(transaction.LoadPending());
        }
        else
        {
            var journal = transaction.Create(prepared);
            transaction.Activate(journal);
            game.Write("version.dll", [7, 8, 9]);
            Assert.Throws<InvalidDataException>(() => transaction.VerifyActive(journal));
            var restored = transaction.Restore(journal);
            Assert.False(restored.Success);
            Assert.NotNull(transaction.LoadPending());
            Assert.Equal("prepared fixture SCPSL.exe", File.ReadAllText(Path.Combine(game.Root, "SCPSL.exe")));
        }
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(game.Root, "version.dll")));
    }

    [Theory]
    [InlineData("skip-legacy")]
    [InlineData("schema4-with-loader")]
    [InlineData("skip-replace")]
    [InlineData("normal-observe")]
    [InlineData("bootstrap-original")]
    public void LoaderModeCannotWeakenMandatoryJournalValidation(string invalid)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(Prepare(game, invalid is "skip-legacy" or "skip-replace"));
        var bootstrap = journal.Entries.Single(entry => entry.RelativePath == "version.dll");
        if (invalid == "skip-legacy") journal.SchemaVersion = 3;
        if (invalid == "schema4-with-loader") journal.SchemaVersion = 4;
        if (invalid == "skip-replace")
        {
            bootstrap.Action = FileAction.Replace;
            bootstrap.PreparedHash = new string('0', 64);
            bootstrap.PreparedSource = $"temporary/session-{journal.Id}/version.dll.modded";
        }
        if (invalid == "normal-observe")
        {
            bootstrap.Action = FileAction.Observe;
            bootstrap.PreparedHash = null;
            bootstrap.PreparedSource = null;
        }
        if (invalid == "bootstrap-original")
        {
            bootstrap.OriginalHash = new string('0', 64);
            bootstrap.BackupPath = CacheLayout.OriginalFile("version.dll", bootstrap.OriginalHash);
        }
        FileOps.WriteJson(Path.Combine(game.Root, ".modlaunch", "session.json"), journal);
        var files = Snapshot(game);

        Assert.Throws<InvalidDataException>(() => transaction.LoadPending());
        Assert.Throws<InvalidDataException>(() => transaction.VerifyActive(journal));
        Assert.Equal(files, Snapshot(game));
    }

    [Fact]
    public void ExistingSchema3JournalWithoutLaunchModeFieldsRemainsRecoverable()
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(Prepare(game, false));
        AddLegacyObservedBackups(game, journal, 3);
        transaction.Activate(journal);
        string journalPath = Path.Combine(game.Root, ".modlaunch", "session.json");
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(journalPath)));
        Assert.True(json.Remove("skipMelonLoader"));
        Assert.True(json.Remove("dotnetRoot"));
        File.WriteAllText(journalPath, json.ToJsonString());

        var recovery = new SessionTransaction(game.Root);
        var pending = Assert.IsType<SessionJournal>(recovery.LoadPending());
        Assert.Equal(3, pending.SchemaVersion);
        Assert.False(pending.SkipMelonLoader);
        Assert.Equal("", pending.DotnetRoot);
        recovery.VerifyActive(pending);
        AssertRestored(game, recovery.Restore(pending));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void OlderRetainedSessionsStillVerifyTheirObservedBackupsBeforeRecovery(int schema, bool skipMelonLoader)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(Prepare(game, skipMelonLoader));
        AddLegacyObservedBackups(game, journal, schema);
        transaction.Activate(journal);
        var recovery = new SessionTransaction(game.Root);
        var pending = Assert.IsType<SessionJournal>(recovery.LoadPending());
        Assert.Equal(schema, pending.SchemaVersion);
        recovery.VerifyActive(pending);
        var observed = pending.Entries.Single(entry => entry.RelativePath == "UnityPlayer.dll");
        string backup = Path.Combine(game.Root, ".modlaunch", observed.BackupPath!);
        File.Delete(backup);
        Assert.Throws<InvalidDataException>(() => recovery.VerifyActive(pending));
        AssertRestored(game, recovery.Restore(pending));
        Assert.All(pending.Entries.Where(entry => entry.BackupPath != null),
            entry => Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch", entry.BackupPath!))));
    }

    private static void AddLegacyObservedBackups(TestGame game, SessionJournal journal, int schema)
    {
        journal.SchemaVersion = schema;
        foreach (var entry in journal.Entries.Where(entry => entry.OriginalHash != null && entry.BackupPath == null))
        {
            entry.BackupPath = CacheLayout.OriginalFile(entry.RelativePath, entry.OriginalHash!);
            FileOps.CopyVerified(Path.Combine(game.Root, entry.RelativePath), Path.Combine(game.Root, ".modlaunch", entry.BackupPath), entry.OriginalHash!);
        }
        FileOps.WriteJson(Path.Combine(game.Root, ".modlaunch", "session.json"), journal);
    }

    private static PreparedSession Prepare(TestGame game, bool skipMelonLoader)
    {
        var prepared = game.Prepare();
        prepared.SkipMelonLoader = skipMelonLoader;
        prepared.DotnetRoot = Path.Combine(game.Root, "host-dotnet");
        return prepared;
    }

    private static string[] Snapshot(TestGame game) => Directory.GetFiles(game.Root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(game.Root, path) + ":" + FileOps.Hash(path)).ToArray();

    private static void AssertRestored(TestGame game, RestoreResult result)
    {
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Failures));
        foreach (var (relative, bytes) in game.Originals)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(game.Root, relative)));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
    }

    private sealed class SimulatedInterruption : Exception;
}
