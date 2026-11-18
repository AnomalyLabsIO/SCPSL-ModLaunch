using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class LauncherOverviewTests
{
    [Fact]
    public void FreshInspectionDoesNotReadGameFileContents()
    {
        using var game = new TestGame();
        var handles = game.Originals.Keys.Select(relative => new FileStream(Path.Combine(game.Root, relative),
            FileMode.Open, FileAccess.Read, FileShare.None)).ToArray();
        try
        {
            var overview = LauncherInspection.Inspect(game.Root);
            Assert.Equal("Patch game", overview.NextActionText);
            Assert.Equal("Not prepared", overview.ModPatchStatus);
            Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch")));
        }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }

    [Fact]
    public void FreshGameNeedsPatchingWithoutInventingItsVersionOrCreatingCacheFiles()
    {
        using var game = new TestGame();
        var before = Snapshot(game.Root);
        var overview = LauncherInspection.Inspect(game.Root);
        Assert.Equal("Patch game", overview.NextActionText);
        Assert.Equal("Not prepared", overview.ModPatchStatus);
        Assert.Null(overview.GameVersion);
        Assert.Null(overview.MelonLoaderVersion);
        Assert.False(overview.HasSession);
        Assert.Equal(before, Snapshot(game.Root));
        Assert.False(Directory.Exists(Path.Combine(game.Root, ".modlaunch")));
    }

    [Fact]
    public void ManagedAssemblyVersionCanBeReadBesideAnActiveNativeProxyWithoutLoadingEitherAssembly()
    {
        using var game = new TestGame();
        string melon = Path.Combine(game.Root, "MelonLoader/net6/MelonLoader.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(melon)!);
        File.Copy(typeof(LauncherInspection).Assembly.Location, melon);
        game.Write("version.dll", [0x4D, 0x5A, 0x00]);
        string before = FileOps.Hash(melon);
        var overview = LauncherInspection.Inspect(game.Root);
        Version expected = typeof(LauncherInspection).Assembly.GetName().Version!;
        Assert.Equal(expected.Revision == 0 ? expected.ToString(3) : expected.ToString(), overview.MelonLoaderVersion);
        Assert.Equal(before, FileOps.Hash(melon));
        Assert.Equal(new byte[] { 0x4D, 0x5A, 0x00 }, File.ReadAllBytes(Path.Combine(game.Root, "version.dll")));
    }

    [Fact]
    public void InvalidManagedImageReportsAnUnknownLoaderVersionWithoutNativeResourceParsing()
    {
        using var game = new TestGame();
        game.Write("MelonLoader/net6/MelonLoader.dll", [0x4D, 0x5A, 0x00]);
        var overview = LauncherInspection.Inspect(game.Root);
        Assert.Equal("Unknown", overview.MelonLoaderVersion);
        Assert.Equal("Repair", overview.NextActionText);
    }

    [Fact]
    public void VerifiedPreparedGameIsReadyWhileOriginalGameFilesRemainActive()
    {
        using var fixture = new PreparedFixture();
        var overview = fixture.Inspect();
        Assert.True(overview.NextAction == LauncherNextAction.Ready, string.Join("; ", overview.Details));
        Assert.Equal("Prepared", overview.ModPatchStatus);
        Assert.Equal("14.2.7", overview.GameVersion);
        Assert.False(overview.IsActive);
        Assert.False(overview.HasSession);
    }

    [Fact]
    public void WindowsPathCasingDoesNotInvalidateTheStoredPreparationIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new PreparedFixture();
        var overview = fixture.Inspect(fixture.Game.Root.ToUpperInvariant());
        Assert.True(overview.NextAction == LauncherNextAction.Ready, string.Join("; ", overview.Details));
        Assert.Equal("14.2.7", overview.GameVersion);
    }

    [Fact]
    public void ActiveSessionUsesVerifiedJournalAndBackupsForItsStatus()
    {
        using var fixture = new PreparedFixture();
        var transaction = new SessionTransaction(fixture.Game.Root);
        var journal = transaction.Create(fixture.Session);
        transaction.Activate(journal);
        var before = Snapshot(fixture.Game.Root);
        var overview = fixture.Inspect();
        Assert.True(overview.NextAction == LauncherNextAction.Ready, string.Join("; ", overview.Details));
        Assert.Equal("Active", overview.ModPatchStatus);
        Assert.True(overview.IsActive);
        Assert.True(overview.HasSession);
        Assert.Equal("14.2.7", overview.GameVersion);
        Assert.Equal(before, Snapshot(fixture.Game.Root));
        Assert.True(transaction.Restore(journal).Success);
    }

    [Fact]
    public void PreparedButUnactivatedSessionRequiresRecoveryRatherThanDisplayingActive()
    {
        using var fixture = new PreparedFixture();
        var transaction = new SessionTransaction(fixture.Game.Root);
        var journal = transaction.Create(fixture.Session);
        var overview = fixture.Inspect();
        Assert.Equal("Repair", overview.NextActionText);
        Assert.True(overview.HasSession);
        Assert.False(overview.IsActive);
        Assert.Null(overview.GameVersion);
        Assert.True(transaction.Restore(journal).Success);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("prepared")]
    [InlineData("correction")]
    [InlineData("runtime")]
    [InlineData("settings")]
    public void ChangedPreparationEvidenceCannotBeReportedAsReady(string condition)
    {
        using var fixture = new PreparedFixture();
        switch (condition)
        {
            case "original": fixture.Game.Write("GameAssembly.dll", [0]); break;
            case "prepared": File.WriteAllText(Path.Combine(fixture.Session.PreparedDirectory, CacheLayout.ModdedFile("GameAssembly.dll")), "changed"); break;
            case "correction": fixture.Session.BundleVersion = null; fixture.Save(); break;
            case "runtime": fixture.Game.Write("MelonLoader/net6/MelonLoader.dll", [0]); break;
            case "settings": FileOps.WriteJson(Path.Combine(fixture.Game.Root, ".modlaunch/settings.json"), new ModLaunchSettings { MelonLoaderVersion = "0.7.2" }); break;
        }
        Assert.Equal("Repair", fixture.Inspect().NextActionText);
    }

    [Fact]
    public void UnknownLoaderAndMalformedJournalAreDisplayedAsRepairWithoutMutatingThem()
    {
        using var game = new TestGame();
        game.Write("version.dll", [5]);
        Assert.Equal("Repair", LauncherInspection.Inspect(game.Root).NextActionText);
        game.Write(".modlaunch/session.json", [1, 2, 3]);
        var before = Snapshot(game.Root);
        var overview = LauncherInspection.Inspect(game.Root);
        Assert.Equal("Repair", overview.NextActionText);
        Assert.True(overview.HasSession);
        Assert.Equal(before, Snapshot(game.Root));
    }

    [Fact]
    public void PatchOnlyCacheShowsItsVerifiedTargetButNormalGuiLaunchStillNeedsPreparation()
    {
        using var fixture = new PreparedFixture(skipMelonLoader: true);
        var overview = fixture.Inspect();
        Assert.Equal("Patch game", overview.NextActionText);
        Assert.Equal("Prepared without MelonLoader", overview.ModPatchStatus);
        Assert.Equal("14.2.7", overview.GameVersion);
        Assert.True(overview.SkipMelonLoader);
    }

    [Fact]
    public void MissingGameFileCannotBePresentedAsAFreshPatchableInstallation()
    {
        using var game = new TestGame();
        File.Delete(Path.Combine(game.Root, "GameAssembly.dll"));
        var overview = LauncherInspection.Inspect(game.Root);
        Assert.Equal("Repair", overview.NextActionText);
        Assert.Contains(overview.Details, message => message.Contains("GameAssembly.dll"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedInputIdentityCannotHideGameFilesEvenWhenItsStoredKeyIsUpdated(bool active)
    {
        using var fixture = new PreparedFixture();
        SessionJournal? journal = null;
        var transaction = new SessionTransaction(fixture.Game.Root);
        if (active)
        {
            journal = transaction.Create(fixture.Session);
            transaction.Activate(journal);
        }
        fixture.Session.Inputs.RemoveAll(input => input.RelativePath == "GameAssembly.dll");
        fixture.Save(recalculateInputKey: true);
        Assert.Equal("Repair", fixture.Inspect().NextActionText);
        if (journal != null) Assert.True(transaction.Restore(journal).Success);
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => path + " " + FileOps.Hash(path)).ToArray();

    private sealed class PreparedFixture : IDisposable
    {
        public TestGame Game { get; } = new();
        public PreparedSession Session { get; }
        private readonly string inputKey;
        private readonly string melonHash;

        public PreparedFixture(bool skipMelonLoader = false)
        {
            string cache = Path.Combine(Game.Root, ".modlaunch");
            string prepared = Path.Combine(cache, CacheLayout.ModdedFiles);
            string host = Path.Combine(Game.Root, "runtime");
            HostRuntimeTests.WriteRuntime(host, "6.0.36");
            Session = new PreparedSession
            {
                LayoutVersion = 3, PreparedDirectory = prepared, Fingerprint = new string('A', 64),
                AdapterFingerprint = new string('B', 64), PatchEngine = ModPatchBundle.Repository,
                GameVersion = "14.2.7", UnityVersion = "6000.0.43f1", DotnetRoot = host,
                SkipMelonLoader = skipMelonLoader,
                Inputs = Game.Originals.Keys.Select(relative => new FileIdentity(relative, FileOps.Hash(Path.Combine(Game.Root, relative)))).ToList()
            };
            foreach (string relative in new[] { "SCPSL.exe", "GameAssembly.dll", GameFileContract.BundleVersionPath })
            {
                string source = CacheLayout.ModdedFile(relative);
                string path = Path.Combine(prepared, source);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "prepared fixture " + relative);
                Session.Files.Add(new PreparedFile(relative, FileOps.Hash(path), source));
            }
            Session.BundleVersion = new BundleVersionCorrection("14.1.0", Session.GameVersion, "applied",
                Session.Files.Single(file => file.RelativePath == GameFileContract.BundleVersionPath).Sha256);
            inputKey = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Session.Inputs, JsonDefaults.Options)));
            melonHash = "";
            if (!skipMelonLoader)
            {
                byte[] bootstrap = [1, 2, 3];
                string archive = Path.Combine(cache, "downloads", "MelonLoader-0.7.3.x64.zip");
                Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                using (var entry = zip.CreateEntry("version.dll").Open()) entry.Write(bootstrap);
                Session.MelonLoaderArchive = archive;
                Session.BootstrapHash = Convert.ToHexString(SHA256.HashData(bootstrap));
                melonHash = FileOps.Hash(archive);
                string runtimeStage = Path.Combine(Game.Root, "runtime-stage");
                foreach (string relative in new[] { "MelonLoader/net6/MelonLoader.dll", "MelonLoader/net6/Il2CppInterop.Runtime.dll",
                             "MelonLoader/net6/MelonLoader.runtimeconfig.json", "MelonLoader/net6/MelonLoader.deps.json" })
                {
                    string path = Path.Combine(runtimeStage, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "owned fixture " + relative);
                    Session.RuntimeFiles.Add(new PreparedFile(relative, FileOps.Hash(path)));
                }
                RuntimeInstallation.InstallFromStage(Game.Root, runtimeStage, Session.RuntimeFiles, Session.Fingerprint);
            }
            Save();
        }

        public void Save(bool recalculateInputKey = false) => FileOps.WriteJson(Path.Combine(Session.PreparedDirectory, CacheLayout.PreparationManifest),
            new { InputKey = recalculateInputKey
                ? Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Session.Inputs, JsonDefaults.Options))) : inputKey,
                AdapterRepository = "AnomalyLabsIO/SCPSL-ModLaunch", MelonLoaderVersion = "0.7.3", Session });

        public LauncherOverview Inspect(string? root = null) => LauncherInspection.Inspect(root ?? Game.Root, melonHash);
        public void Dispose() => Game.Dispose();
    }
}
