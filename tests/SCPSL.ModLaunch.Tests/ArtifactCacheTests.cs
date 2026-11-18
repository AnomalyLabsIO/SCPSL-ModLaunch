using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCPSL.ModLaunch.Core;
using SCPSL.ModLaunch.Core.Provisioning;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class ArtifactCacheTests
{
    [Fact]
    public async Task ValidCachedDownloadDoesNotNeedNetwork()
    {
        using var game = new TestGame();
        byte[] content = [10, 20, 30];
        string destination = Path.Combine(game.Root, "download.zip");
        File.WriteAllBytes(destination, content);
        using var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
        using var http = new HttpClient(handler);
        var cache = new ArtifactCache(http, _ => { });
        await cache.DownloadAsync("https://example.invalid/file.zip", destination, Convert.ToHexString(SHA256.HashData(content)), false, CancellationToken.None);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(content, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task CorruptDownloadDoesNotReplaceExistingCachedArtifact()
    {
        using var game = new TestGame();
        byte[] existing = [10, 20, 30];
        string destination = Path.Combine(game.Root, "download.zip");
        File.WriteAllBytes(destination, existing);
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var http = new HttpClient(handler);
        var cache = new ArtifactCache(http, _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.DownloadAsync("https://example.invalid/file.zip", destination,
            Convert.ToHexString(SHA256.HashData(new byte[] { 4, 5, 6 })), false, CancellationToken.None));
        Assert.Equal(existing, File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(game.Root, "*.part"));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/escape.dll")]
    [InlineData("C:/escape.dll")]
    [InlineData("directory/../../escape.dll")]
    [InlineData("directory/escape.dll.")]
    public void TraversingZipPathsNeverCreatePublishedExtraction(string entry)
    {
        using var game = new TestGame();
        string zip = MakeZip(game.Root, [(entry, new byte[] { 1, 2, 3 })]);
        string destination = Path.Combine(game.Root, "extract");
        Assert.ThrowsAny<Exception>(() => ArtifactCache.ExtractVerified(zip, destination));
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(game.Root, "escape.dll")));
    }

    [Fact]
    public void DuplicateZipNamesAreRejectedCaseInsensitively()
    {
        using var game = new TestGame();
        string zip = MakeZip(game.Root, [("payload.dll", new byte[] { 1 }), ("PAYLOAD.dll", new byte[] { 2 })]);
        Assert.Throws<InvalidDataException>(() => ArtifactCache.ExtractVerified(zip, Path.Combine(game.Root, "extract")));
    }

    [Fact]
    public void SymbolicLinkZipEntryIsRejected()
    {
        using var game = new TestGame();
        string zip = Path.Combine(game.Root, "link.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link.dll");
            entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using var output = new StreamWriter(entry.Open());
            output.Write("../outside.dll");
        }
        Assert.Throws<InvalidDataException>(() => ArtifactCache.ExtractVerified(zip, Path.Combine(game.Root, "extract")));
    }

    [Fact]
    public void CorruptExtractedFileIsReplacedFromVerifiedArchiveAndOldTreePreserved()
    {
        using var game = new TestGame();
        string zip = MakeZip(game.Root, [("nested/payload.dll", new byte[] { 1, 2, 3 })]);
        string destination = Path.Combine(game.Root, "extract");
        ArtifactCache.ExtractVerified(zip, destination);
        File.WriteAllBytes(Path.Combine(destination, "nested", "payload.dll"), [99]);
        ArtifactCache.ExtractVerified(zip, destination);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(destination, "nested", "payload.dll")));
        string retained = Assert.Single(Directory.GetDirectories(game.Root, "extract.invalid-*"));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(Path.Combine(retained, "nested", "payload.dll")));
    }

    [Fact]
    public void ExtractedInventoryRemainsVerifiableWithoutItsConsumedArchive()
    {
        using var game = new TestGame();
        string zip = MakeZip(game.Root, [("nested/payload.dll", new byte[] { 1, 2, 3 })]);
        string digest = FileOps.Hash(zip);
        string destination = Path.Combine(game.Root, "extract");
        ArtifactCache.ExtractVerified(zip, destination);
        File.Delete(zip);

        Assert.True(ArtifactCache.IsVerifiedExtraction(destination, digest));
        Assert.False(ArtifactCache.IsVerifiedExtraction(destination, new string('F', 64)));
        File.WriteAllBytes(Path.Combine(destination, "nested", "payload.dll"), [99]);
        Assert.False(ArtifactCache.IsVerifiedExtraction(destination, digest));
    }

    [Fact]
    public async Task AdapterDownloadIsDiscardedAndOnlyFetchedAgainWhenExtractedFilesChange()
    {
        using var game = new TestGame();
        var payloads = new Dictionary<string, byte[]>
        {
            [ModPatchBundle.Executable] = Encoding.UTF8.GetBytes("fixture patch engine; never executed"),
            [ModPatchBundle.Manifest] = Encoding.UTF8.GetBytes("{}")
        };
        var bundle = new ModPatchBundle
        {
            SchemaVersion = 2, SourceRepository = ModPatchBundle.Repository, SourceRevision = new string('A', 40),
            SourceSha256 = new string('B', 64), TargetFramework = "net6.0-windows", RuntimeIdentifier = "win-x64",
            EntryPoint = ModPatchBundle.Executable, SelfContained = false, SingleFile = true,
            Capabilities = [ModPatchBundle.BundleVersionCapability],
            Files = payloads.Select(pair => new PreparedFile(pair.Key, Convert.ToHexString(SHA256.HashData(pair.Value)))).ToList()
        };
        var files = payloads.Select(pair => ("ModPatch/" + pair.Key, pair.Value)).ToList();
        files.Add(("ModPatch/bundle.json", JsonSerializer.SerializeToUtf8Bytes(bundle, JsonDefaults.Options)));
        byte[] helper = Encoding.UTF8.GetBytes("fixture headless helper; never executed");
        files.Add(("SCPSL-ModLaunch.ModPatch.Headless.exe", helper));
        byte[] archive = File.ReadAllBytes(MakeZip(game.Root, files));
        string digest = Convert.ToHexString(SHA256.HashData(archive));
        const string asset = "SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip";
        int downloads = 0;
        using var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    draft = false, prerelease = false,
                    assets = new[] { new { name = asset, digest = "sha256:" + digest,
                        browser_download_url = "https://github.com/AnomalyLabsIO/SCPSL-ModLaunch/releases/download/v0.1.0/" + asset } }
                })) };
            downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        });
        using var http = new HttpClient(handler);
        var service = new PreparationService(_ => { }, http);
        string cache = Path.Combine(game.Root, ".modlaunch");
        string adapter = await service.ResolveAdapterAsync(cache, "AnomalyLabsIO/SCPSL-ModLaunch", default);
        Assert.False(File.Exists(Path.Combine(cache, CacheLayout.AdapterDownload(digest))));
        Assert.Equal(adapter, await service.ResolveAdapterAsync(cache, "AnomalyLabsIO/SCPSL-ModLaunch", default));
        Assert.Equal(1, downloads);

        File.WriteAllText(adapter, "changed helper");
        Assert.Equal(adapter, await service.ResolveAdapterAsync(cache, "AnomalyLabsIO/SCPSL-ModLaunch", default));
        Assert.Equal(2, downloads);
        Assert.Equal(helper, File.ReadAllBytes(adapter));
        Assert.False(File.Exists(Path.Combine(cache, CacheLayout.AdapterDownload(digest))));
    }

    [Fact]
    public void MelonPreparationExtractsRuntimeWithoutEverWritingAnIdleBootstrap()
    {
        using var game = new TestGame();
        byte[] bootstrap = [1, 2, 3];
        string zip = MakeZip(game.Root, [("version.dll", bootstrap), ("MelonLoader/net6/MelonLoader.dll", new byte[] { 4, 5 }), ("README.txt", new byte[] { 6 })]);
        string stage = Path.Combine(game.Root, "stage");
        string hash = ArtifactCache.ExtractMelonRuntime(zip, stage);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bootstrap)), hash);
        Assert.Equal(hash, ArtifactCache.BootstrapHash(zip));
        Assert.False(File.Exists(Path.Combine(stage, "version.dll")));
        Assert.False(File.Exists(Path.Combine(stage, "README.txt")));
        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(Path.Combine(stage, "MelonLoader/net6/MelonLoader.dll")));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("MelonLoader/version.dll")]
    [InlineData("VERSION.DLL")]
    public void InvalidMelonArchiveNeverLeavesAnIdleBootstrap(string invalid)
    {
        using var game = new TestGame();
        string zip = MakeZip(game.Root, [("version.dll", new byte[] { 1 }), ("MelonLoader/net6/MelonLoader.dll", new byte[] { 2 }), (invalid, new byte[] { 3 })]);
        string stage = Path.Combine(game.Root, "stage");
        Assert.ThrowsAny<Exception>(() => ArtifactCache.ExtractMelonRuntime(zip, stage));
        Assert.False(File.Exists(Path.Combine(stage, "version.dll")));
        if (Directory.Exists(stage)) Assert.Empty(Directory.EnumerateFiles(stage, "version.dll", SearchOption.AllDirectories));
    }

    [Fact]
    public void ScratchClassificationKeepsFinalAssembliesAndGeneratorDownloads()
    {
        Assert.True(PreparationService.IsGenerationScratch("MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/Assembly-CSharp.dll"));
        Assert.True(PreparationService.IsGenerationScratch("MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/Cpp2IL_Temp/hostpolicy.dll"));
        Assert.True(PreparationService.IsGenerationScratch("MelonLoader/Dependencies/Il2CppAssemblyGenerator/ModLaunchTemp/metadata.dat"));
        Assert.False(PreparationService.IsGenerationScratch("MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll"));
        Assert.False(PreparationService.IsGenerationScratch("MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/Cpp2IL.exe"));
        Assert.False(PreparationService.IsGenerationScratch("MelonLoader/Dependencies/Il2CppAssemblyGenerator/UnityDependencies/UnityEngine.dll"));
    }

    private static string MakeZip(string root, IEnumerable<(string Name, byte[] Data)> files)
    {
        string path = Path.Combine(root, "fixture.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in files)
        {
            using var output = archive.CreateEntry(name).Open();
            output.Write(data);
        }
        return path;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(send(request));
        }
    }
}
