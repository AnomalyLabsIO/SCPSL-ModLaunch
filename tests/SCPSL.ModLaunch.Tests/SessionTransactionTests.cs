using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class SessionTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectStorageEditBacksUpAndRestoresExactBootConfiguration(bool skipMelonLoader)
    {
        using var game = new TestGame();
        const string relative = GameFileContract.BootConfigPath;
        byte[] original = "\uFEFFenable-directstorage = True  # bêta\r\nother-option=True\r\n"u8.ToArray();
        game.Write(relative, original);
        var prepared = game.Prepare();
        prepared.SkipMelonLoader = skipMelonLoader;
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        var entry = Assert.Single(journal.Entries, e => e.RelativePath == relative);
        string file = Path.Combine(game.Root, relative);
        string backup = Path.Combine(game.Root, ".modlaunch", entry.BackupPath!);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Equal(original, File.ReadAllBytes(backup));

        transaction.Activate(journal);
        Assert.Equal("\uFEFFenable-directstorage = False  # bêta\r\nother-option=True\r\n"u8.ToArray(), File.ReadAllBytes(file));

        var recovery = new SessionTransaction(game.Root);
        var pending = Assert.IsType<SessionJournal>(recovery.LoadPending());
        recovery.VerifyActive(pending);
        Assert.True(recovery.Restore(pending).Success);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.False(File.Exists(backup));
    }

    [Theory]
    [InlineData("enable-directstorage=False\n")]
    [InlineData("# enable-directstorage=True\nother-option=True\n")]
    public void BootConfigurationAlreadyDisabledOrWithoutDirectStorageIsLeftAlone(string contents)
    {
        using var game = new TestGame();
        game.Write(GameFileContract.BootConfigPath, System.Text.Encoding.UTF8.GetBytes(contents));
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        Assert.DoesNotContain(journal.Entries, e => e.RelativePath == GameFileContract.BootConfigPath);
        transaction.Activate(journal);
        Assert.Equal(contents, File.ReadAllText(Path.Combine(game.Root, GameFileContract.BootConfigPath)));
        Assert.True(transaction.Restore(journal).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedSessionPersistsBootConfigurationBackupBeforeEditing(bool interrupt)
    {
        using var game = new TestGame();
        const string relative = GameFileContract.BootConfigPath;
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        transaction.Activate(journal);
        byte[] original = "enable-directstorage=True\n"u8.ToArray();
        game.Write(relative, original);

        var resumed = new SessionTransaction(game.Root, checkpoint: point =>
        {
            if (point != "before-activate:" + relative) return;
            var saved = Assert.IsType<SessionJournal>(transaction.LoadPending());
            var entry = Assert.Single(saved.Entries, e => e.RelativePath == relative);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(game.Root, ".modlaunch", entry.BackupPath!)));
            if (interrupt) throw new SimulatedInterruption();
        });
        var pending = Assert.IsType<SessionJournal>(resumed.LoadPending());
        resumed.VerifyActive(pending);
        if (interrupt) Assert.Throws<SimulatedInterruption>(() => resumed.ApplyBootConfigToRetainedSession(pending));
        else
        {
            resumed.ApplyBootConfigToRetainedSession(pending);
            resumed.ApplyBootConfigToRetainedSession(pending);
            resumed.VerifyActive(pending);
            Assert.Equal("enable-directstorage=False\n", File.ReadAllText(Path.Combine(game.Root, relative)));
        }
        Assert.True(transaction.Restore(Assert.IsType<SessionJournal>(transaction.LoadPending())).Success);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(game.Root, relative)));
    }

    [Fact]
    public void CompleteSessionRestoresOriginalsAndRetainsUserFilesAndLoaderWork()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        // Provisioning installs dormant runtime files before the reversible transaction.
        game.Write("MelonLoader/net6/MelonLoader.dll", [5, 6, 7]);
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        transaction.Activate(journal);

        Assert.Equal("prepared fixture SCPSL.exe", File.ReadAllText(Path.Combine(game.Root, "SCPSL.exe")));
        Assert.Equal(TestGame.BundleBytes("14.2.7"), File.ReadAllBytes(Path.Combine(game.Root, GameFileContract.BundleVersionPath)));
        Assert.False(File.Exists(Path.Combine(game.Root, "SL-AC.dll")));
        Assert.True(File.Exists(Path.Combine(game.Root, "version.dll")));
        game.Write("MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll", [1, 2, 3]);

        var restored = transaction.Restore(journal);
        Assert.True(restored.Success, string.Join(Environment.NewLine, restored.Failures));
        Assert.Null(transaction.LoadPending());
        foreach (var (path, data) in game.Originals)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(game.Root, path)));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
        Assert.True(File.Exists(Path.Combine(game.Root, "MelonLoader/net6/MelonLoader.dll")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(game.Root, "MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll")));
        foreach (var path in new[] { "Mods/user.dll", "Plugins/plugin.dll", "UserData/user.cfg", "UserLibs/library.dll" })
            Assert.Equal("user owned " + path, File.ReadAllText(Path.Combine(game.Root, path)));
        Assert.All(journal.Entries.Where(entry => entry.BackupPath != null),
            entry => Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch", entry.BackupPath!))));
    }

    [Fact]
    public void ParserMetadataCannotBeActivatedAsARuntimeOutput()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        string source = "parser-metadata.bin";
        File.WriteAllText(Path.Combine(game.Stage, source), "parser-only v31 metadata");
        prepared.Files.Add(new PreparedFile(SessionTransaction.RuntimeMetadataPath,
            FileOps.Hash(Path.Combine(game.Stage, source)), source));

        var transaction = new SessionTransaction(game.Root);
        var error = Assert.Throws<InvalidDataException>(() => transaction.Create(prepared));
        Assert.Contains("Runtime metadata must remain unchanged", error.Message);
        Assert.Null(transaction.LoadPending());
        foreach (var (path, original) in game.Originals)
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(game.Root, path)));
    }

    [Fact]
    public void PreparedSessionDoesNotActivateWhenItsInputFingerprintChanged()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        File.WriteAllText(Path.Combine(game.Root, "GameAssembly.dll"), "Steam update");
        Assert.ThrowsAny<Exception>(() => new SessionTransaction(game.Root).Create(prepared));
        Assert.Equal("Steam update", File.ReadAllText(Path.Combine(game.Root, "GameAssembly.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
    }

    [Fact]
    public void PreparedOutputWithWrongHashIsRejectedBeforeActivation()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        File.WriteAllText(Path.Combine(game.Stage, prepared.Files.Single(f => f.RelativePath == "GameAssembly.dll").SourcePath!), "corrupt prepared output");
        var transaction = new SessionTransaction(game.Root);
        Assert.ThrowsAny<Exception>(() => { var journal = transaction.Create(prepared); transaction.Activate(journal); });
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
        var pending = transaction.LoadPending();
        if (pending is not null) Assert.True(transaction.Restore(pending).Success);
        Assert.Equal(game.Originals["GameAssembly.dll"], File.ReadAllBytes(Path.Combine(game.Root, "GameAssembly.dll")));
    }

    [Theory]
    [MemberData(nameof(ActivationCheckpoints))]
    public void InterruptedActivationCanBeRecoveredByANewInstance(string checkpoint)
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        var transaction = new SessionTransaction(game.Root, checkpoint: point =>
        {
            if (point == checkpoint) throw new SimulatedInterruption();
        });
        Assert.Throws<SimulatedInterruption>(() => transaction.Activate(transaction.Create(prepared)));

        var recovery = new SessionTransaction(game.Root);
        var pending = Assert.IsType<SessionJournal>(recovery.LoadPending());
        var restored = recovery.Restore(pending);
        Assert.True(restored.Success, string.Join(Environment.NewLine, restored.Failures));
        foreach (var (name, original) in game.Originals)
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(game.Root, name)));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
        Assert.Null(recovery.LoadPending());
    }

    public static IEnumerable<object[]> ActivationCheckpoints()
    {
        yield return ["journal-created"];
        foreach (string path in ManagedPaths())
        {
            yield return ["before-activate:" + path];
            yield return ["after-activate:" + path];
        }
    }

    public static IEnumerable<object[]> RestoreCheckpoints() => ManagedPaths().SelectMany(path => new[] { new object[] { "before-restore:" + path }, new object[] { "after-restore:" + path } });

    private static string[] ManagedPaths() => ["SCPSL.exe", "GameAssembly.dll", "UnityPlayer.dll", "SCPSL_Data/il2cpp_data/Metadata/global-metadata.dat", GameFileContract.BundleVersionPath, "SL-AC.dll", "version.dll"];

    [Theory]
    [MemberData(nameof(RestoreCheckpoints))]
    public void EveryRestoreStepCanResumeWithoutRestoringExecutableEarly(string checkpoint)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        transaction.Activate(journal);
        var interrupted = new SessionTransaction(game.Root, checkpoint: point =>
        {
            if (point != checkpoint) return;
            if (point != "after-restore:SCPSL.exe")
                Assert.Equal("prepared fixture SCPSL.exe", File.ReadAllText(Path.Combine(game.Root, "SCPSL.exe")));
            throw new SimulatedInterruption();
        });
        Assert.Throws<SimulatedInterruption>(() => interrupted.Restore(journal));
        var recovery = new SessionTransaction(game.Root);
        var restored = recovery.Restore(Assert.IsType<SessionJournal>(recovery.LoadPending()));
        Assert.True(restored.Success, string.Join(Environment.NewLine, restored.Failures));
        foreach (var (name, original) in game.Originals)
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(game.Root, name)));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
        Assert.Null(recovery.LoadPending());
    }

    [Fact]
    public void ExecutableReplacementIsLastAndDataDirectoryNamesNeverChange()
    {
        using var game = new TestGame();
        var points = new List<string>();
        var transaction = new SessionTransaction(game.Root, checkpoint: point =>
        {
            points.Add(point);
            Assert.True(Directory.Exists(Path.Combine(game.Root, "SCPSL_Data")));
            Assert.False(Directory.Exists(Path.Combine(game.Root, "SCPSL_Modded_Data")));
            Assert.False(File.Exists(Path.Combine(game.Root, "SCPSL_Modded.exe")));
            if (point == "before-activate:SCPSL.exe")
            {
                Assert.Equal(game.Originals["SCPSL.exe"], File.ReadAllBytes(Path.Combine(game.Root, "SCPSL.exe")));
                Assert.True(File.Exists(Path.Combine(game.Root, "version.dll")));
                Assert.False(File.Exists(Path.Combine(game.Root, "SL-AC.dll")));
                Assert.Equal(TestGame.BundleBytes("14.2.7"), File.ReadAllBytes(Path.Combine(game.Root, GameFileContract.BundleVersionPath)));
            }
            if (point == "before-restore:SCPSL.exe")
            {
                Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
                foreach (var (path, bytes) in game.Originals.Where(x => x.Key != "SCPSL.exe"))
                    Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(game.Root, path)));
            }
        });
        var journal = transaction.Create(game.Prepare());
        transaction.Activate(journal);
        Assert.Equal("after-activate:SCPSL.exe", points.Last());
        Assert.True(transaction.Restore(journal).Success);
        Assert.Equal("after-restore:SCPSL.exe", points.Last(p => p.StartsWith("after-restore:", StringComparison.Ordinal)));
    }

    [Fact]
    public void BootstrapComesOnlyFromArchiveAndItsSessionCopyIsNeutral()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        var loader = journal.Entries.Single(e => e.RelativePath == "version.dll");
        Assert.Equal($"temporary/session-{journal.Id}/version.dll.modded", loader.PreparedSource);
        Assert.Empty(Directory.GetFiles(Path.Combine(game.Root, ".modlaunch"), "version.dll", SearchOption.AllDirectories));
        transaction.Activate(journal);
        Assert.Equal(prepared.BootstrapHash, FileOps.Hash(Path.Combine(game.Root, "version.dll")));
        Assert.True(transaction.Restore(journal).Success);
        Assert.Empty(Directory.GetFiles(game.Root, "version.dll", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("corrupt")]
    public void InvalidBootstrapArchiveIsRejectedBeforeAnyLiveChange(string defect)
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        if (defect == "corrupt") prepared.BootstrapHash = new string('0', 64);
        else
        {
            using var output = new FileStream(prepared.MelonLoaderArchive, FileMode.Create);
            using var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create);
            if (defect == "duplicate")
                foreach (string name in new[] { "version.dll", "VERSION.DLL" })
                {
                    using var entry = archive.CreateEntry(name).Open();
                    entry.WriteByte(1);
                }
        }
        var transaction = new SessionTransaction(game.Root);
        Assert.Throws<InvalidDataException>(() => transaction.Create(prepared));
        Assert.Null(transaction.LoadPending());
        foreach (var (path, bytes) in game.Originals) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(game.Root, path)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyJournalsRemainRecoverableBeforeAnyFolderMigration(int schema)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        journal.SchemaVersion = schema;
        if (schema == 1) journal.Entries.RemoveAll(e => e.RelativePath == GameFileContract.BundleVersionPath);
        var ac = journal.Entries.Single(e => e.RelativePath == "SL-AC.dll");
        ac.ParkedPath = schema == 1 ? $"sessions/{journal.Id}/parked/SL-AC.dll.disabled" : $"sessions/{journal.Id}/parked/anticheat.bin";
        string cache = Path.Combine(game.Root, ".modlaunch");
        foreach (var entry in journal.Entries.Where(e => e.OriginalHash != null))
        {
            string previous = entry.BackupPath == null ? Path.Combine(game.Root, entry.RelativePath) : Path.Combine(cache, entry.BackupPath);
            entry.BackupPath = $"originals/{entry.OriginalHash!.ToUpperInvariant()}.bin";
            FileOps.CopyVerified(previous, Path.Combine(cache, entry.BackupPath), entry.OriginalHash);
        }
        foreach (var entry in journal.Entries)
        {
            string target = Path.Combine(game.Root, entry.RelativePath);
            if (entry.Action == FileAction.Replace)
                FileOps.CopyVerified(Path.Combine(cache, entry.PreparedSource!), target, entry.PreparedHash!);
            else if (entry.Action == FileAction.Park)
            {
                string parked = Path.Combine(cache, entry.ParkedPath!);
                Directory.CreateDirectory(Path.GetDirectoryName(parked)!);
                File.Move(target, parked);
            }
        }
        FileOps.WriteJson(Path.Combine(cache, "session.json"), journal);
        var recovery = new SessionTransaction(game.Root);
        var restored = recovery.Restore(Assert.IsType<SessionJournal>(recovery.LoadPending()));
        Assert.True(restored.Success, string.Join(Environment.NewLine, restored.Failures));
        Assert.Null(recovery.LoadPending());
        foreach (var (path, bytes) in game.Originals) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(game.Root, path)));
    }

    [Theory]
    [InlineData("GameAssembly.dll")]
    [InlineData(GameFileContract.BundleVersionPath)]
    public void RestoreDisablesLoaderButDoesNotOverwriteExternalChanges(string changedPath)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        transaction.Activate(journal);
        File.WriteAllText(Path.Combine(game.Root, changedPath), "external update");

        var restored = transaction.Restore(journal);
        Assert.False(restored.Success);
        Assert.Contains(restored.Failures, message => message.Contains(changedPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("external update", File.ReadAllText(Path.Combine(game.Root, changedPath)));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
        Assert.NotNull(transaction.LoadPending());
        Assert.All(journal.Entries.Where(entry => entry.BackupPath != null),
            entry => Assert.True(File.Exists(Path.Combine(game.Root, ".modlaunch", entry.BackupPath!))));
        Assert.Equal("prepared fixture SCPSL.exe", File.ReadAllText(Path.Combine(game.Root, "SCPSL.exe")));
    }

    [Fact]
    public void AlreadyCorrectBundleVersionIsAcceptedAndRestoredWithoutAnOriginalMismatch()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        byte[] corrected = TestGame.BundleBytes("14.2.7");
        game.Write(GameFileContract.BundleVersionPath, corrected);
        int input = prepared.Inputs.FindIndex(f => f.RelativePath == GameFileContract.BundleVersionPath);
        prepared.Inputs[input] = new FileIdentity(GameFileContract.BundleVersionPath, FileOps.Hash(Path.Combine(game.Root, GameFileContract.BundleVersionPath)));
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        transaction.Activate(journal);
        Assert.True(transaction.Restore(journal).Success);
        Assert.Equal(corrected, File.ReadAllBytes(Path.Combine(game.Root, GameFileContract.BundleVersionPath)));
    }

    [Fact]
    public void RestoreCanResumeAfterAnInterruptionFollowingTheLoaderRename()
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        transaction.Activate(journal);
        var interrupted = new SessionTransaction(game.Root, checkpoint: point =>
        {
            if (point == "after-restore:version.dll") throw new SimulatedInterruption();
        });
        Assert.Throws<SimulatedInterruption>(() => interrupted.Restore(journal));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));

        var recovery = new SessionTransaction(game.Root);
        var restored = recovery.Restore(Assert.IsType<SessionJournal>(recovery.LoadPending()));
        Assert.True(restored.Success, string.Join(Environment.NewLine, restored.Failures));
        Assert.True(recovery.Restore(journal).Success);
        foreach (var (name, original) in game.Originals)
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(game.Root, name)));
    }

    [Fact]
    public void ExistingPendingSessionCannotBeReplacedByANewBackup()
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        Assert.ThrowsAny<Exception>(() => transaction.Create(game.Prepare()));
        Assert.True(transaction.Restore(journal).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedSessionsRecreateRecoveryBackupsWithoutRebuildingPreparedOutputs(bool skipMelonLoader)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var prepared = game.Prepare();
        prepared.SkipMelonLoader = skipMelonLoader;
        var outputs = prepared.Files.Select(file => Path.Combine(game.Stage, file.SourcePath!)).ToArray();
        var outputHashes = outputs.Select(FileOps.Hash).ToArray();
        var first = transaction.Create(prepared);
        string[] backups = first.Entries.Where(entry => entry.BackupPath != null)
            .Select(entry => Path.Combine(game.Root, ".modlaunch", entry.BackupPath!)).ToArray();
        Assert.Equal(4, backups.Length);
        Assert.All(backups, backup => Assert.True(File.Exists(backup)));
        transaction.Activate(first);
        Assert.True(transaction.Restore(first).Success);
        Assert.All(backups, backup => Assert.False(File.Exists(backup)));

        var second = transaction.Create(prepared);
        Assert.All(backups, backup => Assert.True(File.Exists(backup)));
        transaction.Activate(second);
        transaction.VerifyActive(second);
        Assert.True(transaction.Restore(second).Success);
        Assert.All(backups, backup => Assert.False(File.Exists(backup)));
        Assert.Equal(outputHashes, outputs.Select(FileOps.Hash));
        Assert.Empty(Directory.GetFiles(Path.Combine(game.Root, ".modlaunch"), "version.dll", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(game.Root, ".modlaunch", "temporary"), "SL-AC.dll.original", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewJournalsObserveUnchangedInputsWithoutCopyingThem(bool skipMelonLoader)
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        prepared.SkipMelonLoader = skipMelonLoader;
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        Assert.Equal(skipMelonLoader ? 6 : 5, journal.SchemaVersion);
        foreach (string relative in new[] { "UnityPlayer.dll", SessionTransaction.RuntimeMetadataPath })
        {
            var observed = journal.Entries.Single(entry => entry.RelativePath == relative);
            Assert.Equal(FileAction.Observe, observed.Action);
            Assert.Null(observed.BackupPath);
            Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch", CacheLayout.OriginalFile(relative, observed.OriginalHash!))));
        }
        transaction.Activate(journal);
        transaction.VerifyActive(journal);
        Assert.True(transaction.Restore(journal).Success);
    }

    [Fact]
    public void BackupCleanupRunsOnlyAfterVerifiedRestoreAndJournalRemoval()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        transaction.Activate(journal);
        string[] backups = journal.Entries.Where(entry => entry.BackupPath != null)
            .Select(entry => Path.Combine(game.Root, ".modlaunch", entry.BackupPath!)).ToArray();
        Assert.Throws<InvalidOperationException>(() => SessionTransaction.CleanIdleOriginalBackups(game.Root, prepared.Inputs));
        Assert.All(backups, backup => Assert.True(File.Exists(backup)));
        var interrupted = new SessionTransaction(game.Root, checkpoint: point =>
        {
            if (point != "journal-cleared") return;
            Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch", "session.json")));
            Assert.All(backups, backup => Assert.True(File.Exists(backup)));
            throw new SimulatedInterruption();
        });
        Assert.Throws<SimulatedInterruption>(() => interrupted.Restore(journal));
        SessionTransaction.CleanIdleOriginalBackups(game.Root, prepared.Inputs);
        Assert.All(backups, backup => Assert.False(File.Exists(backup)));
        Assert.True(transaction.Restore(journal).Success);
    }

    [Fact]
    public void RestoreCleanupPreservesChangedAndUnrelatedBackups()
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        string changed = Path.Combine(game.Root, ".modlaunch", journal.Entries.Single(entry => entry.RelativePath == "SCPSL.exe").BackupPath!);
        File.WriteAllText(changed, "changed backup must survive");
        string unrelated = ".modlaunch/" + CacheLayout.OriginalFile("GameAssembly.dll", new string('1', 64));
        game.Write(unrelated, [1, 2, 3]);
        Assert.True(transaction.Restore(journal).Success); // The originals were never activated.
        Assert.Equal("changed backup must survive", File.ReadAllText(changed));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(game.Root, unrelated)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdleCleanupRemovesVerifiedOldBackupsAndPreservesUnknownOrChangedFiles(bool changedInput)
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        string hash = FileOps.Hash(Path.Combine(game.Root, "UnityPlayer.dll"));
        string known = ".modlaunch/" + CacheLayout.OriginalFile("UnityPlayer.dll", hash);
        game.Write(known, game.Originals["UnityPlayer.dll"]);
        string oldHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 8, 9, 10 }));
        string old = ".modlaunch/" + CacheLayout.OriginalFile("GameAssembly.dll", oldHash);
        game.Write(old, [8, 9, 10]);
        string legacy = $".modlaunch/originals/{oldHash}.bin";
        game.Write(legacy, [8, 9, 10]);
        string changed = ".modlaunch/" + CacheLayout.OriginalFile("SCPSL.exe", new string('0', 64));
        game.Write(changed, [4, 5, 6]);
        string unknown = ".modlaunch/" + CacheLayout.OriginalFile("unknown.dll", oldHash);
        game.Write(unknown, [8, 9, 10]);
        game.Write(".modlaunch/original-game-files/keep.txt", [11, 12, 13]);
        if (changedInput) game.Write("GameAssembly.dll", [99]);

        SessionTransaction.CleanIdleOriginalBackups(game.Root, prepared.Inputs);

        foreach (string path in new[] { known, old, legacy }) Assert.Equal(changedInput, File.Exists(Path.Combine(game.Root, path)));
        foreach (string path in new[] { changed, unknown, ".modlaunch/original-game-files/keep.txt" }) Assert.True(File.Exists(Path.Combine(game.Root, path)));
    }

    [Fact]
    public void AnOldJournalCannotRestoreOrDeleteAnotherActiveSessionsRecoveryFiles()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        var transaction = new SessionTransaction(game.Root);
        var first = transaction.Create(prepared);
        Assert.True(transaction.Restore(first).Success);
        var current = transaction.Create(prepared);
        transaction.Activate(current);
        Assert.Throws<InvalidOperationException>(() => transaction.Restore(first));
        Assert.Equal(current.Id, transaction.LoadPending()!.Id);
        transaction.VerifyActive(current);
        Assert.True(transaction.Restore(current).Success);
    }

    [Fact]
    public void DirectoryLockRejectsConcurrentLaunchAndCanBeReacquiredAfterExit()
    {
        using var game = new TestGame();
        using (var first = new DirectoryLock(game.Root))
            Assert.Throws<IOException>(() => new DirectoryLock(game.Root));
        using var second = new DirectoryLock(game.Root);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("backup")]
    [InlineData("prepared")]
    [InlineData("duplicate")]
    public void TamperedJournalIsRejectedWithoutChangingGameFiles(string field)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        var entry = journal.Entries.First(item => item.RelativePath == "SCPSL.exe");
        if (field == "target") entry.RelativePath = "../outside.dll";
        if (field == "backup") entry.BackupPath = "../outside.dll";
        if (field == "prepared") entry.PreparedSource = "../outside.dll";
        if (field == "duplicate") journal.Entries.Add(entry);
        FileOps.WriteJson(Path.Combine(game.Root, ".modlaunch", "session.json"), journal);
        Assert.ThrowsAny<Exception>(() => new SessionTransaction(game.Root).LoadPending());
        foreach (var (name, original) in game.Originals)
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(game.Root, name)));
    }

    [Theory]
    [InlineData("SCPSL.exe")]
    [InlineData("GameAssembly.dll")]
    [InlineData("SL-AC.dll")]
    [InlineData("version.dll")]
    public void RecoveryCannotDeclareSuccessWhenMandatoryJournalEntriesWereRemoved(string omitted)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        transaction.Activate(journal);
        journal.Entries.RemoveAll(entry => entry.RelativePath == omitted);
        FileOps.WriteJson(Path.Combine(game.Root, ".modlaunch", "session.json"), journal);
        Assert.Throws<InvalidDataException>(() => new SessionTransaction(game.Root).LoadPending());
        Assert.True(File.Exists(Path.Combine(game.Root, "version.dll")));
        Assert.True(File.Exists(Path.Combine(game.Root, ".modlaunch", "session.json")));
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("gameDirectory")]
    [InlineData("relativePath")]
    public void ExplicitNullJournalFieldsAreReportedAsInvalidData(string field)
    {
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        if (field == "entries") journal.Entries = null!;
        if (field == "gameDirectory") journal.GameDirectory = null!;
        if (field == "relativePath") journal.Entries[0].RelativePath = null!;
        FileOps.WriteJson(Path.Combine(game.Root, ".modlaunch", "session.json"), journal);
        Assert.Throws<InvalidDataException>(() => new SessionTransaction(game.Root).LoadPending());
    }

    [Fact]
    public void LockedBootstrapReportsFailureAndRecoveryRetriesAfterUnlock()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix open-file deletion semantics differ.
        using var game = new TestGame();
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        transaction.Activate(journal);
        using (var locked = new FileStream(Path.Combine(game.Root, "version.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = transaction.Restore(journal);
            Assert.False(failed.Success);
            Assert.Contains(failed.Failures, message => message.Contains("version.dll", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(transaction.LoadPending());
        }
        Assert.True(transaction.Restore(Assert.IsType<SessionJournal>(transaction.LoadPending())).Success);
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
    }

    [Fact]
    public void UnknownRootProxyIsNotAdopted()
    {
        using var game = new TestGame();
        game.Write("version.dll", [9, 9, 9]);
        var transaction = new SessionTransaction(game.Root);
        Assert.ThrowsAny<Exception>(() => transaction.Create(game.Prepare()));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(game.Root, "version.dll")));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("/outside.dll")]
    [InlineData("C:/outside.dll")]
    [InlineData(".modlaunch/state.json")]
    [InlineData("Mods/user.dll")]
    public void PreparedFilesCannotWriteOutsideTheManagedGameSurface(string relative)
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        prepared.Files.Add(new PreparedFile(relative, new string('0', 64)));
        Assert.ThrowsAny<Exception>(() => new SessionTransaction(game.Root).Create(prepared));
        Assert.Equal("user owned Mods/user.dll", File.ReadAllText(Path.Combine(game.Root, "Mods/user.dll")));
    }

    [Theory]
    [InlineData("SCPSL-ModLaunch.exe")]
    [InlineData("SCPSL-ModLaunch.lnk")]
    [InlineData("SCPSL-ModLaunch.ModPatch.Headless.exe")]
    public void AdapterCannotReplaceLauncherFilesEvenWithAValidPayload(string relative)
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        string payload = Path.Combine(game.Stage, "launcher-change.bin");
        File.WriteAllText(payload, "valid payload that must never replace the launcher");
        prepared.Files.Add(new PreparedFile(relative, FileOps.Hash(payload), "launcher-change.bin"));
        var failure = Assert.Throws<InvalidDataException>(() => new SessionTransaction(game.Root).Create(prepared));
        Assert.Contains("User/launcher data cannot be part of the game transaction", failure.Message);
        Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch", "session.json")));
    }

    private sealed class SimulatedInterruption : Exception;
}
