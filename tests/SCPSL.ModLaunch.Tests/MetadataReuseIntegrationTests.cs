using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCPSL.ModLaunch.Core;
using SCPSL.ModLaunch.Core.Provisioning;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class MetadataReuseIntegrationTests
{
    [Fact]
    public void CompletePreparationReusesPersistedMetadataWithoutFullFileReads()
    {
        using var preparation = new CachedPreparation();
        var verification = new FileVerificationCache(preparation.GameRoot);
        Assert.True(preparation.Validate(verification));
        Assert.True(verification.HashedFiles > 0);
        verification.Save();

        var nextLaunch = new FileVerificationCache(preparation.GameRoot);
        Assert.True(preparation.Validate(nextLaunch));
        Assert.Equal(0, nextLaunch.HashedFiles);
        Assert.True(nextLaunch.CachedFiles > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameSizeChangedPayloadOrRuntimeIsRejectedAfterItsTimestampChanges(bool runtime)
    {
        using var preparation = new CachedPreparation();
        var verification = new FileVerificationCache(preparation.GameRoot);
        Assert.True(preparation.Validate(verification));
        long initialHashes = verification.HashedFiles;
        string path = runtime ? preparation.RuntimePath : preparation.PayloadPath("GameAssembly.dll");
        DateTime priorTimestamp = File.GetLastWriteTimeUtc(path);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] ^= 1;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, priorTimestamp.AddSeconds(2));

        Assert.False(preparation.Validate(verification));
        Assert.Equal(initialHashes + 1, verification.HashedFiles);
        // Reusing the newly measured hash must still compare it to the expected identity.
        Assert.False(preparation.Validate(verification));
        Assert.Equal(initialHashes + 1, verification.HashedFiles);
    }

    [Fact]
    public void TimestampOnlyChangesKeepTheSamePreparationAndRefreshTheirMetadata()
    {
        using var preparation = new CachedPreparation();
        var verification = new FileVerificationCache(preparation.GameRoot);
        Assert.True(preparation.Validate(verification));
        long initialHashes = verification.HashedFiles;
        foreach (string path in new[] { preparation.RuntimePath, preparation.PayloadPath("GameAssembly.dll") })
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));

        Assert.True(preparation.Validate(verification));
        Assert.Equal(initialHashes + 2, verification.HashedFiles);
        verification.Save();
        var nextLaunch = new FileVerificationCache(preparation.GameRoot);
        Assert.True(preparation.Validate(nextLaunch));
        Assert.Equal(0, nextLaunch.HashedFiles);
    }

    [Fact]
    public void WarmMetadataDoesNotHideUnexpectedPreparedFiles()
    {
        using var preparation = new CachedPreparation();
        var verification = new FileVerificationCache(preparation.GameRoot);
        Assert.True(preparation.Validate(verification));
        File.WriteAllText(Path.Combine(preparation.Session.PreparedDirectory, "unexpected.bin"), "unexpected payload");

        Assert.False(preparation.Validate(verification));
    }

    [Fact]
    public void WarmMetadataStillRejectsAnExpectedIdentityThatDoesNotMatchContents()
    {
        using var preparation = new CachedPreparation();
        var verification = new FileVerificationCache(preparation.GameRoot);
        Assert.True(preparation.Validate(verification));
        long initialHashes = verification.HashedFiles;
        int index = preparation.Session.Files.FindIndex(file => file.RelativePath == "GameAssembly.dll");
        preparation.Session.Files[index] = preparation.Session.Files[index] with { Sha256 = new string('F', 64) };

        Assert.False(preparation.Validate(verification));
        Assert.Equal(initialHashes, verification.HashedFiles);
    }

    private sealed class CachedPreparation : IDisposable
    {
        private readonly TestGame game = new();
        public string GameRoot => game.Root;
        public string RuntimePath => Path.Combine(GameRoot, "MelonLoader/net6/MelonLoader.dll");
        public PreparedSession Session { get; }
        private string CacheDirectory => Path.Combine(GameRoot, ".modlaunch");
        private string PreparedDirectory => Path.Combine(CacheDirectory, CacheLayout.ModdedFiles);
        private string InputKey { get; }
        private string ArchiveHash { get; }

        public CachedPreparation()
        {
            Session = new PreparedSession
            {
                LayoutVersion = 3,
                Fingerprint = new string('A', 64),
                GameVersion = "14.2.7",
                DotnetRoot = Path.Combine(GameRoot, "host-dotnet"),
                MelonLoaderArchive = Path.Combine(CacheDirectory, "downloads", "MelonLoader-0.7.3.x64.zip"),
                Inputs = game.Originals.Keys.Select(relative => new FileIdentity(relative, FileOps.Hash(Path.Combine(GameRoot, relative)))).ToList()
            };
            Session.PreparedDirectory = PreparedDirectory;
            InputKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Session.Inputs, JsonDefaults.Options))));
            foreach (string relative in new[] { "SCPSL.exe", "GameAssembly.dll", GameFileContract.BundleVersionPath })
                Session.Files.Add(WriteFixture(Session.PreparedDirectory, relative, CacheLayout.ModdedFile(relative)));
            Session.BundleVersion = new BundleVersionCorrection("14.1.0", Session.GameVersion, "applied",
                Session.Files.Single(file => file.RelativePath == GameFileContract.BundleVersionPath).Sha256);
            string stage = Path.Combine(GameRoot, "runtime-stage");
            foreach (string relative in new[] { "MelonLoader/net6/MelonLoader.dll", "MelonLoader/net6/Il2CppInterop.Runtime.dll",
                         "MelonLoader/net6/MelonLoader.runtimeconfig.json", "MelonLoader/net6/MelonLoader.deps.json" })
                Session.RuntimeFiles.Add(WriteFixture(stage, relative));
            RuntimeInstallation.InstallFromStage(GameRoot, stage, Session.RuntimeFiles, Session.Fingerprint);
            HostRuntimeTests.WriteRuntime(Session.DotnetRoot, "6.0.36");
            Directory.CreateDirectory(Path.GetDirectoryName(Session.MelonLoaderArchive)!);
            using (var archive = ZipFile.Open(Session.MelonLoaderArchive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("version.dll").Open()))
                writer.Write("verified bootstrap");
            ArchiveHash = FileOps.Hash(Session.MelonLoaderArchive);
            Session.BootstrapHash = ArtifactCache.BootstrapHash(Session.MelonLoaderArchive);
        }

        private static PreparedFile WriteFixture(string root, string relative, string? source = null)
        {
            string path = Path.Combine(root, source ?? relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (relative == GameFileContract.BundleVersionPath) File.WriteAllBytes(path, TestGame.BundleBytes("14.2.7"));
            else File.WriteAllText(path, "validated fixture " + relative);
            return new PreparedFile(relative, FileOps.Hash(path), source);
        }

        public string PayloadPath(string relative) => Path.Combine(Session.PreparedDirectory, Session.Files.Single(file => file.RelativePath == relative).SourcePath!);
        public bool Validate(FileVerificationCache verification) =>
            PreparationService.ValidateSession(Session, PreparedDirectory, CacheDirectory, InputKey, ArchiveHash, verification);
        public void Dispose() => game.Dispose();
    }
}
