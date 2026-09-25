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
    public void FirstCompletionMovesExecutableAndCreatesEmptyArgumentShortcut()
    {
        using var fixture = new Fixture();
        string original = FileOps.Hash(fixture.Source);
        fixture.Complete();
        Assert.False(File.Exists(fixture.Source));
        Assert.Equal(original, FileOps.Hash(fixture.Installed));
        Assert.True(fixture.Shortcuts.Matches(fixture.Shortcut, fixture.Installed, fixture.Root));
        var owner = fixture.Owner();
        Assert.Equal(original, owner.ExecutableSha256);
        Assert.Equal(FileOps.Hash(fixture.Shortcut), owner.ShortcutSha256);
        Assert.Null(owner.PendingExecutableSha256);
        Assert.Null(owner.PendingShortcutSha256);
    }

    [Fact]
    public void RepeatedInstalledLaunchPreservesExecutableAndShortcut()
    {
        using var fixture = new Fixture();
        fixture.Complete();
        DateTime timestamp = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fixture.Installed, timestamp);
        File.SetLastWriteTimeUtc(fixture.Shortcut, timestamp);
        fixture.Complete(fixture.Installed);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.Installed));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.Shortcut));
        Assert.Equal(1, fixture.Shortcuts.Writes);
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
        Assert.Null(fixture.Owner().PendingExecutableSha256);
    }

    [Theory]
    [InlineData("installation-prepared")]
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
    [InlineData("SCPSL-ModLaunch.lnk")]
    public void UnknownDestinationOrShortcutIsNeverOverwritten(string name)
    {
        using var fixture = new Fixture();
        string path = name.EndsWith(".lnk") ? fixture.Shortcut : fixture.Installed;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "user file");
        Assert.Throws<IOException>(() => fixture.Complete());
        Assert.Equal("user file", File.ReadAllText(path));
        Assert.True(File.Exists(fixture.Source));
        Assert.False(File.Exists(fixture.Ownership));
    }

    [Theory]
    [InlineData("SCPSL-ModLaunch.exe")]
    [InlineData("SCPSL-ModLaunch.lnk")]
    public void PreviouslyOwnedButModifiedFilesAreNeverOverwritten(string name)
    {
        using var fixture = new Fixture();
        fixture.Complete();
        File.WriteAllText(fixture.Source, "new release");
        string path = name.EndsWith(".lnk") ? fixture.Shortcut : fixture.Installed;
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
        Assert.False(File.Exists(fixture.Ownership));
    }

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
        Assert.Equal(1, fixture.Shortcuts.Writes);
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
        Assert.Equal(1, fixture.Shortcuts.Writes);
    }

    [Fact]
    public void ExistingMatchingUserShortcutIsAdoptedWithoutOverwriting()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Shortcut, JsonSerializer.Serialize(new[] { fixture.Installed, fixture.Root, "-noauth" }));
        string customShortcut = FileOps.Hash(fixture.Shortcut);
        fixture.Complete();
        Assert.Equal(customShortcut, FileOps.Hash(fixture.Shortcut));
        Assert.Equal(0, fixture.Shortcuts.Writes);
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
    public void WindowsShellLinkRoundTripsThroughStandardComInterfaces()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        LauncherInstallation.Complete(fixture.Root, fixture.Source);
        Assert.False(File.Exists(fixture.Source));
        var shortcuts = new WindowsLauncherShortcutStore();
        Assert.True(shortcuts.Matches(fixture.Shortcut, fixture.Installed, fixture.Root));
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
        public string Ownership => Path.Combine(Cache, "launcher-installation.json");
        public FakeShortcuts Shortcuts { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "SCPSL.exe"), "retail");
            File.WriteAllText(Source, "launcher");
        }

        public void WritePrivate(string name, string text)
        {
            Directory.CreateDirectory(Cache);
            File.WriteAllText(Path.Combine(Cache, name), text);
        }

        public void Complete(string? path = null, Action<string>? checkpoint = null) =>
            LauncherInstallation.Complete(Root, path ?? Source, Shortcuts, checkpoint: checkpoint);
        public LauncherInstallationOwnership Owner() => FileOps.ReadJson<LauncherInstallationOwnership>(Ownership);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FakeShortcuts : ILauncherShortcutStore
    {
        public int Writes { get; private set; }
        public bool FailWrite { get; set; }
        public void Write(string path, string executable, string workingDirectory)
        {
            if (FailWrite) throw new IOException("Injected shortcut creation failure.");
            Writes++;
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { executable, workingDirectory, "" }));
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
    }
}
