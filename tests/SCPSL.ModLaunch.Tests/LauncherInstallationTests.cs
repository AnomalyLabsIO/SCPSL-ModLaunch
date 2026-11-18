using System.Text.Json;
using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class LauncherInstallationTests
{
    [Fact]
    public void ResolvesOnlyRootOrImmediatePrivateDirectory()
    {
        using var fixture = new Fixture();
        Assert.Equal(fixture.Root, LauncherInstallation.ResolveGameDirectory(fixture.Source));
        Assert.Equal(fixture.Root, LauncherInstallation.ResolveGameDirectory(fixture.Installed));
        Assert.Throws<InvalidDataException>(() => LauncherInstallation.ResolveGameDirectory(Path.Combine(fixture.Root, "other", LauncherInstallation.ExecutableName)));
        Assert.Throws<InvalidDataException>(() => LauncherInstallation.ResolveGameDirectory(Path.Combine(fixture.Root, ".modlaunch", "tools", LauncherInstallation.ExecutableName)));
        Assert.Throws<InvalidDataException>(() => LauncherInstallation.ResolveGameDirectory(Path.Combine(fixture.Root, "renamed.exe")));
    }

    [Fact]
    public void ResolvesMissingRetailExecutableWhenRecoveryJournalExists()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Root, "SCPSL.exe"));
        fixture.WritePrivate("session.json", "pending");
        Assert.Equal(fixture.Root, LauncherInstallation.ResolveGameDirectory(fixture.Source));
        Assert.Equal(fixture.Root, LauncherInstallation.ResolveGameDirectory(fixture.Installed));
    }

    [Fact]
    public void FirstCompletionMovesExecutableAndCreatesExplicitCliAndGuiShortcuts()
    {
        using var fixture = new Fixture();
        string original = FileOps.Hash(fixture.Source);
        fixture.Complete();
        Assert.False(File.Exists(fixture.Source));
        Assert.Equal(original, FileOps.Hash(fixture.Installed));
        Assert.True(fixture.Shortcuts.Matches(fixture.Shortcut, fixture.Installed, fixture.Root));
        Assert.True(fixture.Shortcuts.Matches(fixture.GuiShortcut, fixture.Installed, fixture.Root));
        Assert.Equal(LauncherInstallation.CliShortcutArguments, fixture.Shortcuts.ReadArguments(fixture.Shortcut));
        Assert.Equal(LauncherInstallation.GuiShortcutArguments, fixture.Shortcuts.ReadArguments(fixture.GuiShortcut));
        var owner = fixture.Owner();
        Assert.Equal(2, owner.SchemaVersion);
        Assert.Equal(original, owner.ExecutableSha256);
        Assert.Equal(FileOps.Hash(fixture.Shortcut), owner.ShortcutSha256);
        Assert.Equal(FileOps.Hash(fixture.GuiShortcut), owner.GuiShortcutSha256);
        Assert.Null(owner.PendingExecutableSha256);
        Assert.Null(owner.PendingShortcutSha256);
        Assert.Null(owner.PendingGuiShortcutSha256);
    }

    [Fact]
    public void RepeatedInstalledLaunchPreservesExecutableAndShortcut()
    {
        using var fixture = new Fixture();
        fixture.Complete();
        DateTime timestamp = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fixture.Installed, timestamp);
        File.SetLastWriteTimeUtc(fixture.Shortcut, timestamp);
        File.SetLastWriteTimeUtc(fixture.GuiShortcut, timestamp);
        fixture.Complete(fixture.Installed);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.Installed));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.Shortcut));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.GuiShortcut));
        Assert.Equal(2, fixture.Shortcuts.Writes);
    }

    [Fact]
    public void RootReplacementUpdatesOnlyOwnedNestedExecutable()
    {
        using var fixture = new Fixture();
        fixture.Complete();
        string originalShortcut = FileOps.Hash(fixture.Shortcut);
        File.WriteAllText(fixture.Source, "new release");
        string replacement = FileOps.Hash(fixture.Source);
        fixture.Complete();
        Assert.False(File.Exists(fixture.Source));
        Assert.Equal(replacement, FileOps.Hash(fixture.Installed));
        Assert.Equal(replacement, fixture.Owner().ExecutableSha256);
        Assert.Equal(originalShortcut, FileOps.Hash(fixture.Shortcut));
    }

    [Fact]
    public void ManuallyRelocatedRunningExecutableCanEstablishOwnership()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Cache);
        File.Move(fixture.Source, fixture.Installed);
        fixture.Complete(fixture.Installed);
        Assert.True(fixture.Shortcuts.Matches(fixture.Shortcut, fixture.Installed, fixture.Root));
        Assert.Equal(FileOps.Hash(fixture.Installed), fixture.Owner().ExecutableSha256);
    }

    [Fact]
    public void InstalledCopyDoesNotRemoveAnUnprocessedRootReplacement()
    {
        using var fixture = new Fixture();
        fixture.Complete();
        File.WriteAllText(fixture.Source, "new release");
        string installed = FileOps.Hash(fixture.Installed);
        Assert.Throws<IOException>(() => fixture.Complete(fixture.Installed));
        Assert.Equal("new release", File.ReadAllText(fixture.Source));
        Assert.Equal(installed, FileOps.Hash(fixture.Installed));
    }

    [Theory]
    [InlineData("installation-prepared")]
    [InlineData("cli-shortcut-installed")]
    [InlineData("gui-shortcut-installed")]
    [InlineData("shortcut-installed")]
    [InlineData("executable-relocated")]
    [InlineData("installation-complete")]
    public void InterruptedFirstHandoffCanResume(string checkpoint)
    {
        using var fixture = new Fixture();
        Assert.Throws<InjectedFailure>(() => fixture.Complete(checkpoint: name =>
        {
            if (name == checkpoint) throw new InjectedFailure();
        }));
        fixture.Complete(File.Exists(fixture.Source) ? fixture.Source : fixture.Installed);
        Assert.False(File.Exists(fixture.Source));
        Assert.Equal(FileOps.Hash(fixture.Installed), fixture.Owner().ExecutableSha256);
        Assert.True(fixture.Shortcuts.Matches(fixture.Shortcut, fixture.Installed, fixture.Root));
        Assert.True(fixture.Shortcuts.Matches(fixture.GuiShortcut, fixture.Installed, fixture.Root));
        Assert.Null(fixture.Owner().PendingExecutableSha256);
        Assert.Null(fixture.Owner().PendingGuiShortcutSha256);
    }

    [Theory]
    [InlineData("installation-prepared")]
    [InlineData("cli-shortcut-installed")]
    [InlineData("gui-shortcut-installed")]
    [InlineData("shortcut-installed")]
    [InlineData("executable-relocated")]
    public void InterruptedRootUpdateCanResume(string checkpoint)
    {
        using var fixture = new Fixture();
        fixture.Complete();
        File.WriteAllText(fixture.Source, "new release");
        string expected = FileOps.Hash(fixture.Source);
        Assert.Throws<InjectedFailure>(() => fixture.Complete(checkpoint: name =>
        {
            if (name == checkpoint) throw new InjectedFailure();
        }));
        fixture.Complete(File.Exists(fixture.Source) ? fixture.Source : fixture.Installed);
        Assert.False(File.Exists(fixture.Source));
        Assert.Equal(expected, FileOps.Hash(fixture.Installed));
        Assert.Equal(expected, fixture.Owner().ExecutableSha256);
    }

    [Theory]
    [InlineData("SCPSL-ModLaunch.exe")]
    [InlineData(LauncherInstallation.ShortcutName)]
    [InlineData(LauncherInstallation.GuiShortcutName)]
    public void UnknownDestinationOrShortcutIsNeverOverwritten(string name)
    {
        using var fixture = new Fixture();
        string path = name.EndsWith(".lnk") ? Path.Combine(fixture.Root, name) : fixture.Installed;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "user file");
        Assert.Throws<IOException>(() => fixture.Complete());
        Assert.Equal("user file", File.ReadAllText(path));
        Assert.True(File.Exists(fixture.Source));
        Assert.False(File.Exists(fixture.Ownership));
    }

    [Theory]
    [InlineData("SCPSL-ModLaunch.exe")]
    [InlineData(LauncherInstallation.ShortcutName)]
    [InlineData(LauncherInstallation.GuiShortcutName)]
    public void PreviouslyOwnedButModifiedFilesAreNeverOverwritten(string name)
    {
        using var fixture = new Fixture();
        fixture.Complete();
        File.WriteAllText(fixture.Source, "new release");
        string path = name.EndsWith(".lnk") ? Path.Combine(fixture.Root, name) : fixture.Installed;
        File.WriteAllText(path, "external replacement");
        Assert.Throws<IOException>(() => fixture.Complete());
        Assert.Equal("external replacement", File.ReadAllText(path));
        Assert.True(File.Exists(fixture.Source));
    }

    [Fact]
    public void PendingGameRecoveryPreventsAllInstallationChanges()
    {
        using var fixture = new Fixture();
        fixture.WritePrivate("session.json", "pending");
        Assert.Throws<IOException>(() => fixture.Complete());
        Assert.True(File.Exists(fixture.Source));
        Assert.False(File.Exists(fixture.Installed));
        Assert.False(File.Exists(fixture.Shortcut));
        Assert.False(File.Exists(fixture.GuiShortcut));
        Assert.False(File.Exists(fixture.Ownership));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedSessionInstallsBothShortcutsWithoutChangingGameFilesOrRecovery(bool skipMelonLoader)
    {
        using var game = new TestGame();
        using var fixture = new Fixture(game.Root);
        var prepared = game.Prepare();
        prepared.SkipMelonLoader = skipMelonLoader;
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        transaction.Activate(journal);
        string journalPath = Path.Combine(fixture.Cache, "session.json");
        string journalHash = FileOps.Hash(journalPath);
        var before = SnapshotSessionFiles(game);

        fixture.Complete(retainActiveSession: true);
        Assert.False(File.Exists(fixture.Source));
        Assert.True(File.Exists(fixture.Installed));
        Assert.Equal(journalHash, FileOps.Hash(journalPath));
        Assert.Equal(before, SnapshotSessionFiles(game));
        Assert.True(fixture.Shortcuts.Matches(fixture.Shortcut, fixture.Installed, fixture.Root));
        Assert.True(fixture.Shortcuts.Matches(fixture.GuiShortcut, fixture.Installed, fixture.Root));
        Assert.Equal(LauncherInstallation.CliShortcutArguments, fixture.Shortcuts.ReadArguments(fixture.Shortcut));
        Assert.Equal(LauncherInstallation.GuiShortcutArguments, fixture.Shortcuts.ReadArguments(fixture.GuiShortcut));
        fixture.Complete(fixture.Installed, retainActiveSession: true);
        Assert.Equal(2, fixture.Shortcuts.Writes);
        transaction.VerifyActive(Assert.IsType<SessionJournal>(transaction.LoadPending()));
        Assert.True(transaction.Restore(journal).Success);
        foreach (var (relative, bytes) in game.Originals)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(game.Root, relative)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("inactive")]
    [InlineData("changed-game")]
    [InlineData("changed-backup")]
    public void RetainedInstallationRequiresAFullyActiveRecoverableSession(string condition)
    {
        using var game = new TestGame();
        using var fixture = new Fixture(game.Root);
        var transaction = new SessionTransaction(game.Root);
        if (condition != "missing")
        {
            var journal = transaction.Create(game.Prepare());
            if (condition != "inactive") transaction.Activate(journal);
            if (condition == "malformed") fixture.WritePrivate("session.json", "invalid");
            if (condition == "changed-game") game.Write("GameAssembly.dll", [99]);
            if (condition == "changed-backup")
            {
                var entry = journal.Entries.Single(entry => entry.RelativePath == "GameAssembly.dll");
                File.WriteAllText(Path.Combine(fixture.Cache, entry.BackupPath!), "changed backup");
            }
        }
        var before = SnapshotSessionFiles(game);
        Assert.Throws<IOException>(() => fixture.Complete(retainActiveSession: true));
        Assert.Equal(before, SnapshotSessionFiles(game));
        Assert.True(File.Exists(fixture.Source));
        Assert.False(File.Exists(fixture.Installed));
        Assert.False(File.Exists(fixture.Shortcut));
        Assert.False(File.Exists(fixture.GuiShortcut));
        Assert.False(File.Exists(fixture.Ownership));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedJournalMustRemainUnchangedBeforeExecutableRelocation(bool removed)
    {
        using var game = new TestGame();
        using var fixture = new Fixture(game.Root);
        var transaction = new SessionTransaction(game.Root);
        transaction.Activate(transaction.Create(game.Prepare()));
        Assert.Throws<IOException>(() => fixture.Complete(checkpoint: name =>
        {
            if (name != "shortcut-installed") return;
            string path = Path.Combine(fixture.Cache, "session.json");
            if (removed) File.Delete(path);
            else File.AppendAllText(path, " ");
        }, retainActiveSession: true));
        Assert.True(File.Exists(fixture.Source));
        Assert.False(File.Exists(fixture.Installed));
    }

    [Theory]
    [InlineData("installation-prepared")]
    [InlineData("gui-shortcut-installed")]
    [InlineData("executable-relocated")]
    public void RetainedInstallationCanResumeAfterInterruption(string checkpoint)
    {
        using var game = new TestGame();
        using var fixture = new Fixture(game.Root);
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(game.Prepare());
        transaction.Activate(journal);
        string journalHash = FileOps.Hash(Path.Combine(fixture.Cache, "session.json"));
        Assert.Throws<InjectedFailure>(() => fixture.Complete(checkpoint: name =>
        {
            if (name == checkpoint) throw new InjectedFailure();
        }, retainActiveSession: true));
        fixture.Complete(File.Exists(fixture.Source) ? fixture.Source : fixture.Installed, retainActiveSession: true);
        Assert.False(File.Exists(fixture.Source));
        Assert.True(File.Exists(fixture.Installed));
        Assert.Equal(journalHash, FileOps.Hash(Path.Combine(fixture.Cache, "session.json")));
        transaction.VerifyActive(journal);
        Assert.True(transaction.Restore(journal).Success);
    }

    private static string[] SnapshotSessionFiles(TestGame game) => Directory.GetFiles(game.Root, "*", SearchOption.AllDirectories)
        .Where(path => !new[] { LauncherInstallation.ExecutableName, LauncherInstallation.ShortcutName, LauncherInstallation.GuiShortcutName,
            "launcher-installation.json" }.Contains(Path.GetFileName(path)))
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => path + ":" + FileOps.Hash(path)).ToArray();

    [Fact]
    public void NewlyAppearingRecoveryJournalPreventsExecutableMove()
    {
        using var fixture = new Fixture();
        Assert.Throws<IOException>(() => fixture.Complete(checkpoint: name =>
        {
            if (name == "shortcut-installed") fixture.WritePrivate("session.json", "pending");
        }));
        Assert.True(File.Exists(fixture.Source));
        Assert.False(File.Exists(fixture.Installed));
    }

    [Fact]
    public void ShortcutCreationFailureLeavesRootExecutableAvailable()
    {
        using var fixture = new Fixture();
        fixture.Shortcuts.FailWrite = true;
        var failure = Assert.Throws<IOException>(() => fixture.Complete());
        Assert.Contains("SL-AC may reject vanilla", failure.Message);
        Assert.True(File.Exists(fixture.Source));
        Assert.False(File.Exists(fixture.Installed));
        Assert.False(File.Exists(fixture.Shortcut));
        Assert.False(File.Exists(fixture.GuiShortcut));
        Assert.False(File.Exists(fixture.Ownership));
    }

    [Fact]
    public void FileChangedBetweenCheckpointsIsPreserved()
    {
        using var fixture = new Fixture();
        Assert.Throws<IOException>(() => fixture.Complete(checkpoint: name =>
        {
            if (name == "installation-prepared") File.WriteAllText(fixture.Shortcut, "external race");
        }));
        Assert.Equal("external race", File.ReadAllText(fixture.Shortcut));
        Assert.True(File.Exists(fixture.Source));
        Assert.False(File.Exists(fixture.Installed));
    }

    [Fact]
    public void ShortcutIsNotSilentlyRetargetedWhenWholeGameDirectoryMoves()
    {
        using var fixture = new Fixture();
        fixture.Complete();
        string oldRoot = fixture.Root;
        string newRoot = oldRoot + "-moved";
        Directory.Move(oldRoot, newRoot);
        fixture.Root = newRoot;
        string originalShortcut = FileOps.Hash(fixture.Shortcut);
        Assert.Throws<IOException>(() => fixture.Complete(fixture.Installed));
        Assert.Equal(originalShortcut, FileOps.Hash(fixture.Shortcut));
        Assert.Equal(2, fixture.Shortcuts.Writes);
    }

    [Fact]
    public void CustomShortcutArgumentsAndMetadataArePreserved()
    {
        using var fixture = new Fixture();
        fixture.Complete();
        File.WriteAllText(fixture.Shortcut, JsonSerializer.Serialize(new[] { fixture.Installed, fixture.Root, "-noauth", "custom icon", "new Shell metadata" }));
        string customShortcut = FileOps.Hash(fixture.Shortcut);
        fixture.Complete(fixture.Installed);
        Assert.Equal(customShortcut, FileOps.Hash(fixture.Shortcut));
        Assert.Equal(customShortcut, fixture.Owner().ShortcutSha256);
        Assert.Equal(2, fixture.Shortcuts.Writes);
    }

    [Fact]
    public void ExistingMatchingUserShortcutIsAdoptedWithoutOverwriting()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Shortcut, JsonSerializer.Serialize(new[] { fixture.Installed, fixture.Root, "-noauth" }));
        string customShortcut = FileOps.Hash(fixture.Shortcut);
        fixture.Complete();
        Assert.Equal(customShortcut, FileOps.Hash(fixture.Shortcut));
        Assert.Equal(1, fixture.Shortcuts.Writes);
    }

    [Fact]
    public void OwnedShortcutPointingElsewhereIsNeverOverwritten()
    {
        using var fixture = new Fixture();
        fixture.Complete();
        File.WriteAllText(fixture.Shortcut, JsonSerializer.Serialize(new[] { "C:\\Other.exe", fixture.Root, "-noauth" }));
        string changedShortcut = FileOps.Hash(fixture.Shortcut);
        Assert.Throws<IOException>(() => fixture.Complete(fixture.Installed));
        Assert.Equal(changedShortcut, FileOps.Hash(fixture.Shortcut));
    }

    [Fact]
    public void GuiShortcutCustomArgumentsAndMetadataArePreserved()
    {
        using var fixture = new Fixture();
        fixture.Complete();
        File.WriteAllText(fixture.GuiShortcut, JsonSerializer.Serialize(new[]
            { fixture.Installed, fixture.Root, "--modlaunch-gui -noauth", "custom icon", "Explorer metadata" }));
        string customized = FileOps.Hash(fixture.GuiShortcut);
        fixture.Complete(fixture.Installed);
        Assert.Equal(customized, FileOps.Hash(fixture.GuiShortcut));
        Assert.Equal(customized, fixture.Owner().GuiShortcutSha256);
        Assert.Equal(2, fixture.Shortcuts.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedLegacyDefaultShortcutMigratesAfterBothNewShortcutsAreInstalled(bool interruptedLegacyInstall)
    {
        using var fixture = new Fixture();
        fixture.EstablishLegacyOwnership(interrupted: interruptedLegacyInstall);
        fixture.Complete(interruptedLegacyInstall ? fixture.Source : fixture.Installed);
        Assert.False(File.Exists(fixture.LegacyShortcut));
        Assert.Equal(LauncherInstallation.CliShortcutArguments, fixture.Shortcuts.ReadArguments(fixture.Shortcut));
        Assert.Equal(LauncherInstallation.GuiShortcutArguments, fixture.Shortcuts.ReadArguments(fixture.GuiShortcut));
        Assert.Equal(2, fixture.Owner().SchemaVersion);
        Assert.Null(fixture.Owner().LegacyShortcutSha256);
        Assert.Null(fixture.Owner().PendingGuiShortcutSha256);
    }

    [Theory]
    [InlineData("custom-arguments")]
    [InlineData("changed-metadata")]
    [InlineData("unowned")]
    public void CustomizedOrUnownedLegacyShortcutIsRetainedByteForByte(string condition)
    {
        using var fixture = new Fixture();
        if (condition == "unowned")
            File.WriteAllText(fixture.LegacyShortcut, JsonSerializer.Serialize(new[] { fixture.Installed, fixture.Root, "" }));
        else
        {
            fixture.EstablishLegacyOwnership(arguments: condition == "custom-arguments" ? "-noauth" : "");
            if (condition == "changed-metadata")
                File.WriteAllText(fixture.LegacyShortcut, JsonSerializer.Serialize(new[] { fixture.Installed, fixture.Root, "", "Explorer metadata" }));
        }
        string original = FileOps.Hash(fixture.LegacyShortcut);
        fixture.Complete(condition == "unowned" ? fixture.Source : fixture.Installed);
        Assert.Equal(original, FileOps.Hash(fixture.LegacyShortcut));
        Assert.True(fixture.Shortcuts.Matches(fixture.Shortcut, fixture.Installed, fixture.Root));
        Assert.True(fixture.Shortcuts.Matches(fixture.GuiShortcut, fixture.Installed, fixture.Root));
    }

    [Theory]
    [InlineData("installation-prepared")]
    [InlineData("cli-shortcut-installed")]
    [InlineData("gui-shortcut-installed")]
    [InlineData("executable-relocated")]
    [InlineData("legacy-shortcut-retired")]
    public void InterruptedLegacyMigrationCanResume(string checkpoint)
    {
        using var fixture = new Fixture();
        fixture.EstablishLegacyOwnership();
        Assert.Throws<InjectedFailure>(() => fixture.Complete(fixture.Installed, name =>
        {
            if (name == checkpoint) throw new InjectedFailure();
        }));
        fixture.Complete(fixture.Installed);
        Assert.False(File.Exists(fixture.LegacyShortcut));
        Assert.Equal(FileOps.Hash(fixture.Shortcut), fixture.Owner().ShortcutSha256);
        Assert.Equal(FileOps.Hash(fixture.GuiShortcut), fixture.Owner().GuiShortcutSha256);
        Assert.Null(fixture.Owner().LegacyShortcutSha256);
    }

    [Fact]
    public void SecondShortcutCreationFailurePublishesNothing()
    {
        using var fixture = new Fixture();
        fixture.Shortcuts.FailWriteNumber = 2;
        Assert.Throws<IOException>(() => fixture.Complete());
        Assert.True(File.Exists(fixture.Source));
        Assert.False(File.Exists(fixture.Installed));
        Assert.False(File.Exists(fixture.Shortcut));
        Assert.False(File.Exists(fixture.GuiShortcut));
        Assert.False(File.Exists(fixture.Ownership));
        Assert.Empty(Directory.GetFiles(fixture.Cache, "launcher-shortcut-*.tmp"));
    }

    [Fact]
    public void NewlyAppearingGuiShortcutIsNotOverwrittenAfterCliShortcutPublication()
    {
        using var fixture = new Fixture();
        Assert.Throws<IOException>(() => fixture.Complete(checkpoint: name =>
        {
            if (name == "cli-shortcut-installed") File.WriteAllText(fixture.GuiShortcut, "unrelated user file");
        }));
        Assert.Equal("unrelated user file", File.ReadAllText(fixture.GuiShortcut));
        Assert.True(File.Exists(fixture.Source));
        Assert.False(File.Exists(fixture.Installed));
    }

    [Fact]
    public void LegacyLinkChangedBeforeRetirementIsPreservedAndMigrationCanResume()
    {
        using var fixture = new Fixture();
        fixture.EstablishLegacyOwnership();
        Assert.Throws<IOException>(() => fixture.Complete(fixture.Installed, name =>
        {
            if (name == "executable-relocated") File.WriteAllText(fixture.LegacyShortcut, "external replacement");
        }));
        fixture.Complete(fixture.Installed);
        Assert.Equal("external replacement", File.ReadAllText(fixture.LegacyShortcut));
        Assert.True(fixture.Shortcuts.Matches(fixture.GuiShortcut, fixture.Installed, fixture.Root));
    }

    [Fact]
    public void WindowsShellLinkRoundTripsThroughStandardComInterfaces()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        LauncherInstallation.Complete(fixture.Root, fixture.Source);
        Assert.False(File.Exists(fixture.Source));
        var shortcuts = new WindowsLauncherShortcutStore();
        Assert.True(shortcuts.Matches(fixture.Shortcut, fixture.Installed, fixture.Root));
        Assert.True(shortcuts.Matches(fixture.GuiShortcut, fixture.Installed, fixture.Root));
        Assert.Equal(LauncherInstallation.CliShortcutArguments, shortcuts.ReadArguments(fixture.Shortcut));
        Assert.Equal(LauncherInstallation.GuiShortcutArguments, shortcuts.ReadArguments(fixture.GuiShortcut));
        LauncherInstallation.Complete(fixture.Root, fixture.Installed);
        Assert.True(shortcuts.Matches(fixture.Shortcut, fixture.Installed, fixture.Root));
    }

    private sealed class InjectedFailure : Exception { }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; set; } = Path.Combine(Path.GetTempPath(), "modlaunch-install-tests", Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Root, ".modlaunch");
        public string Source => Path.Combine(Root, LauncherInstallation.ExecutableName);
        public string Installed => Path.Combine(Cache, LauncherInstallation.ExecutableName);
        public string Shortcut => Path.Combine(Root, LauncherInstallation.ShortcutName);
        public string GuiShortcut => Path.Combine(Root, LauncherInstallation.GuiShortcutName);
        public string LegacyShortcut => Path.Combine(Root, LauncherInstallation.LegacyShortcutName);
        public string Ownership => Path.Combine(Cache, "launcher-installation.json");
        public FakeShortcuts Shortcuts { get; } = new();

        public Fixture(string? root = null)
        {
            if (root is not null) Root = root;
            Directory.CreateDirectory(Root);
            if (!File.Exists(Path.Combine(Root, "SCPSL.exe"))) File.WriteAllText(Path.Combine(Root, "SCPSL.exe"), "retail");
            File.WriteAllText(Source, "launcher");
        }

        public void WritePrivate(string name, string text)
        {
            Directory.CreateDirectory(Cache);
            File.WriteAllText(Path.Combine(Cache, name), text);
        }

        public void EstablishLegacyOwnership(string arguments = "", bool interrupted = false)
        {
            Directory.CreateDirectory(Cache);
            if (!interrupted) File.Move(Source, Installed);
            File.WriteAllText(LegacyShortcut, JsonSerializer.Serialize(new[] { Installed, Root, arguments }));
            FileOps.WriteJson(Ownership, new LauncherInstallationOwnership
            {
                SchemaVersion = 1,
                ExecutableSha256 = interrupted ? null : FileOps.Hash(Installed),
                ShortcutSha256 = interrupted ? null : FileOps.Hash(LegacyShortcut),
                PendingExecutableSha256 = interrupted ? FileOps.Hash(Source) : null,
                PendingShortcutSha256 = interrupted ? FileOps.Hash(LegacyShortcut) : null
            });
        }

        public void Complete(string? path = null, Action<string>? checkpoint = null, bool retainActiveSession = false) =>
            LauncherInstallation.Complete(Root, path ?? Source, Shortcuts, checkpoint: checkpoint, retainActiveSession: retainActiveSession);
        public LauncherInstallationOwnership Owner() => FileOps.ReadJson<LauncherInstallationOwnership>(Ownership);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FakeShortcuts : ILauncherShortcutStore
    {
        public int Writes { get; private set; }
        public bool FailWrite { get; set; }
        public int? FailWriteNumber { get; set; }
        public void Write(string path, string executable, string workingDirectory, string arguments)
        {
            if (FailWrite || FailWriteNumber == Writes + 1) throw new IOException("Injected shortcut creation failure.");
            Writes++;
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { executable, workingDirectory, arguments }));
        }
        public bool Matches(string path, string executable, string workingDirectory)
        {
            try
            {
                string[]? fields = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
                return fields is { Length: >= 2 } && fields[0] == executable && fields[1] == workingDirectory;
            }
            catch (JsonException) { return false; }
        }
        public string ReadArguments(string path) => JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))![2];
    }
}
