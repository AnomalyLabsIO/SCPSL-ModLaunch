using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SCPSL.ModLaunch.Core;
using SCPSL.ModLaunch.Core.Provisioning;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class PreparedCacheTests
{
    [Fact]
    public void CachedParserMetadataCannotBeReusedAsARuntimeOutput()
    {
        using var cache = new CachedPreparation();
        string source = "parser-metadata.bin";
        string path = Path.Combine(cache.Session.PreparedDirectory, source);
        File.WriteAllText(path, "parser-only v31 metadata");
        cache.Session.Files.Add(new PreparedFile(SessionTransaction.RuntimeMetadataPath, FileOps.Hash(path), source));
        Assert.False(cache.Validate());
    }

    [Fact]
    public void CompleteCacheIsAcceptedButChangedGameContentsInvalidateIt()
    {
        using var cache = new CachedPreparation();
        Assert.True(cache.Validate());
        cache.Session.Inputs[0] = cache.Session.Inputs[0] with { Sha256 = new string('b', 64) };
        Assert.False(cache.Validate());
    }

    [Theory]
    [InlineData("SCPSL.exe")]
    [InlineData(UnityBundleVersion.RelativePath)]
    public void OmittedRequiredOutputsCannotBeHiddenByTruncatingTheManifest(string missing)
    {
        using var cache = new CachedPreparation();
        cache.Session.Files.RemoveAll(file => file.RelativePath == missing);
        Assert.False(cache.Validate());
    }

    [Fact]
    public void ChangedPreparedFileIsRejected()
    {
        using var cache = new CachedPreparation();
        File.WriteAllText(cache.PayloadPath("GameAssembly.dll"), "corrupted");
        Assert.False(cache.Validate());
    }

    [Fact]
    public void StaleBundleVersionCannotBeReusedEvenWhenItsHashMatchesTheManifest()
    {
        using var cache = new CachedPreparation();
        string path = cache.PayloadPath(UnityBundleVersion.RelativePath);
        File.WriteAllBytes(path, TestGame.BundleBytes("14.1.0"));
        int index = cache.Session.Files.FindIndex(f => f.RelativePath == UnityBundleVersion.RelativePath);
        cache.Session.Files[index] = cache.Session.Files[index] with { Sha256 = FileOps.Hash(path) };
        Assert.False(cache.Validate());
    }

    [Fact]
    public void OmittedEssentialRuntimeFileCannotBeHiddenByTruncatingItsManifest()
    {
        using var cache = new CachedPreparation();
        cache.Session.RuntimeFiles.RemoveAll(file => file.RelativePath == "MelonLoader/net6/Il2CppInterop.Runtime.dll");
        Assert.False(cache.Validate());
    }

    [Fact]
    public void ChangedInstalledRuntimePreventsOfflineReuse()
    {
        using var cache = new CachedPreparation();
        File.WriteAllText(Path.Combine(cache.GameRoot, "MelonLoader/net6/MelonLoader.dll"), "unexpected code");
        Assert.False(cache.Validate());
    }

    [Fact]
    public void MutableGeneratorConfigDoesNotInvalidatePreparedAssemblies()
    {
        using var cache = new CachedPreparation();
        string config = Path.Combine(cache.GameRoot, RuntimeInstallation.GeneratorConfig);
        File.WriteAllText(config, "[Il2CppAssemblyGenerator]\n# User comment survives\nCustomSetting = true\n");
        Assert.True(cache.Validate());
    }

    [Fact]
    public void MissingHostRuntimeDoesNotFallBackToAnUnverifiedPrivateCopy()
    {
        using var cache = new CachedPreparation();
        File.Delete(Path.Combine(cache.Session.DotnetRoot, "shared/Microsoft.NETCore.App/6.0.36/coreclr.dll"));
        Assert.Throws<InvalidDataException>(() => cache.Validate());
    }

    [Fact]
    public void LegacyPreparationCannotBeReused()
    {
        using var cache = new CachedPreparation();
        cache.Session.LayoutVersion = 0;
        Assert.False(cache.Validate());
    }

    [Theory]
    [InlineData("version.dll")]
    [InlineData("../outside.bin")]
    [InlineData("C:/outside.bin")]
    public void PreparedPayloadMustHaveASafeNeutralSourceName(string source)
    {
        using var cache = new CachedPreparation();
        cache.Session.Files[0] = cache.Session.Files[0] with { SourcePath = source };
        try { Assert.False(cache.Validate()); }
        catch (InvalidDataException) { }
    }

    [Fact]
    public void UnexpectedIdleBootstrapFileIsRejected()
    {
        using var cache = new CachedPreparation();
        File.WriteAllText(Path.Combine(cache.Session.PreparedDirectory, "version.dll"), "unexpected");
        Assert.False(cache.Validate());
    }

    [Fact]
    public void ArchiveAndBootstrapIdentitiesAreBothValidated()
    {
        using var cache = new CachedPreparation();
        cache.Session.BootstrapHash = new string('F', 64);
        Assert.False(cache.Validate());
        cache.Session.BootstrapHash = ArtifactCache.BootstrapHash(cache.Session.MelonLoaderArchive);
        using (var archive = ZipFile.Open(cache.Session.MelonLoaderArchive, ZipArchiveMode.Update))
            archive.CreateEntry("unexpected");
        Assert.False(cache.Validate());
    }

    private sealed class CachedPreparation : IDisposable
    {
        private readonly TestGame game = new();
        public string GameRoot => game.Root;
        public PreparedSession Session { get; }
        private string CacheDirectory => Path.Combine(game.Root, ".modlaunch");
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
                DotnetRoot = Path.Combine(game.Root, "host-dotnet"),
                MelonLoaderArchive = Path.Combine(CacheDirectory, "downloads", "MelonLoader-0.7.3.x64.zip"),
                Inputs = game.Originals.Keys.Select(name => new FileIdentity(name, FileOps.Hash(Path.Combine(game.Root, name)))).ToList()
            };
            Session.PreparedDirectory = PreparedDirectory;
            InputKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Session.Inputs, JsonDefaults.Options))));
            foreach (var path in new[] { "SCPSL.exe", "GameAssembly.dll", UnityBundleVersion.RelativePath })
                Session.Files.Add(WriteFixture(Session.PreparedDirectory, path, CacheLayout.ModdedFile(path)));
            string stage = Path.Combine(game.Root, "runtime-stage");
            foreach (var path in new[] { "MelonLoader/net6/MelonLoader.dll", "MelonLoader/net6/Il2CppInterop.Runtime.dll", "MelonLoader/net6/MelonLoader.runtimeconfig.json", "MelonLoader/net6/MelonLoader.deps.json", RuntimeInstallation.GeneratorConfig })
                Session.RuntimeFiles.Add(WriteFixture(stage, path));
            RuntimeInstallation.InstallFromStage(game.Root, stage, Session.RuntimeFiles, Session.Fingerprint);
            HostRuntimeTests.WriteRuntime(Session.DotnetRoot, "6.0.36");
            Directory.CreateDirectory(Path.GetDirectoryName(Session.MelonLoaderArchive)!);
            using (var archive = ZipFile.Open(Session.MelonLoaderArchive, ZipArchiveMode.Create))
            using (var stream = new StreamWriter(archive.CreateEntry("version.dll").Open())) stream.Write("verified bootstrap");
            ArchiveHash = FileOps.Hash(Session.MelonLoaderArchive);
            Session.BootstrapHash = ArtifactCache.BootstrapHash(Session.MelonLoaderArchive);
        }

        private static PreparedFile WriteFixture(string root, string relative, string? source = null)
        {
            string path = Path.Combine(root, source ?? relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (relative == UnityBundleVersion.RelativePath) File.WriteAllBytes(path, TestGame.BundleBytes("14.2.7"));
            else if (relative == RuntimeInstallation.GeneratorConfig) File.WriteAllText(path, "[Il2CppAssemblyGenerator]\n");
            else File.WriteAllText(path, "validated fixture " + relative);
            return new PreparedFile(relative, FileOps.Hash(path), source);
        }

        public string PayloadPath(string relative) => Path.Combine(Session.PreparedDirectory, Session.Files.Single(file => file.RelativePath == relative).SourcePath!);
        public bool Validate() => PreparationService.ValidateSession(Session, PreparedDirectory, CacheDirectory, InputKey, ArchiveHash);
        public void Dispose() => game.Dispose();
    }
}
