using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SCPSL.ModLaunch.Core;
using SCPSL.ModLaunch.Core.Provisioning;
using SCPSL.ModLaunch.ModPatch.Headless;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class ForkPatchEngineTests
{
    private const string Manifest = """
        {"versionRanges":[{"versionRange":"14.1.0 - Current","versionFrom":"14.1.0","versionTo":"Current",
        "cleanLauncherPath":"./clean_launchers/6000.0.43f1/SCPSL.exe","metadataVersion":31,
        "methods":{"patchMethods":[{"name":"Fixture$$Run","patchPattern":"48 83 EC ??","patchData":"[RET]"}]}}]}
        """;

    private const string LegacyManifest = """
        {"versionRanges":[{"versionRange":"14.1.0 - Current","versionFrom":"14.1.0","versionTo":"Current",
        "cleanLauncherPath":"./clean_launchers/6000.0.43f1/SCPSL.exe","metadataVersion":31,
        "methods":{"patchMethods":[{"name":"Fixture$$Legacy","patchOffset":0,"patchData":"[RET]"}]}}]}
        """;

    private const string MixedManifest = """
        {"versionRanges":[
        {"versionRange":"14.1.0 - Current","versionFrom":"14.1.0","versionTo":"Current",
        "cleanLauncherPath":"./clean_launchers/6000.0.43f1/SCPSL.exe","metadataVersion":31,
        "methods":{"patchMethods":[{"name":"Fixture$$Run","patchPattern":"48 83 EC ??","patchData":"[RET]"},
        {"name":"Fixture$$Legacy","patchOffset":4,"patchData":"[RET]","patchSize":2}]}},
        {"version":"13.6.9","cleanLauncherPath":"./clean_launchers/2021.3.17f1/SCPSL.exe","metadataVersion":29,
        "methods":{"patchMethods":[{"name":"Fixture$$Exact","patchOffset":0,"patchData":"[RET]"}]}},
        {"gameAssemblySha256":"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
        "cleanLauncherPath":"./clean_launchers/2019.4.16f1/SCPSL.exe","metadataVersion":24,
        "methods":{"patchMethods":[{"name":"Fixture$$Hash","patchPattern":"40 55 56 57","patchData":"[RET]"}]}}]}
        """;

    [Fact]
    public void PatternManifestPinsCleanLaunchersToTheForkRevision()
    {
        var document = ModPatchBundle.PinPatternManifest(Manifest, new string('a', 40));
        Assert.Equal($"https://raw.githubusercontent.com/AnomalyLabsIO/SCPSL-ModPatch/{new string('a', 40)}/Shared_Files/clean_launchers/6000.0.43f1/SCPSL.exe",
            document["versionRanges"]![0]!["cleanLauncherUrl"]!.GetValue<string>());
    }

    [Fact]
    public void StandardManifestPreservesMixedPatchesAndOptionalTargetsWhenPinningLaunchers()
    {
        string revision = new('a', 40);
        var expected = JsonNode.Parse(MixedManifest)!;
        foreach (var profile in expected["versionRanges"]!.AsArray())
            profile!["cleanLauncherUrl"] = $"https://raw.githubusercontent.com/{ModPatchBundle.Repository}/{revision}/Shared_Files/{ModPatchBundle.LauncherPath(profile["cleanLauncherPath"]!.GetValue<string>())}";
        var document = ModPatchBundle.PinPatternManifest(MixedManifest, revision);
        Assert.Equal(expected.ToJsonString(), document.ToJsonString());
    }

    [Theory]
    [InlineData("../SCPSL.exe")]
    [InlineData("clean_launchers/../SCPSL.exe")]
    [InlineData("https://example.com/SCPSL.exe")]
    public void PatternManifestRejectsLauncherPathsOutsideTheVersionedResource(string path)
    {
        var document = JsonNode.Parse(Manifest)!;
        document["versionRanges"]![0]!["cleanLauncherPath"] = path;
        Assert.Throws<InvalidDataException>(() => ModPatchBundle.PinPatternManifest(document.ToJsonString(), new string('a', 40)));
    }

    [Fact]
    public void LegacyOffsetManifestCannotSilentlyReplacePatternResources()
    {
        Assert.Throws<InvalidDataException>(() => ModPatchBundle.PinPatternManifest(LegacyManifest, new string('a', 40)));
    }

    [Theory]
    [InlineData("SCPSL.ModPatch.Cli.exe")]
    [InlineData("patchinfo.json")]
    public void CorruptBundledCodeOrManifestAreRejected(string relative)
    {
        using var game = new TestGame();
        string root = CreateBundle(game.Root);
        _ = ModPatchBundle.Read(root);
        File.AppendAllText(Path.Combine(root, relative), "corrupt");
        Assert.Throws<InvalidDataException>(() => ModPatchBundle.Read(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceUpdatesCacheTheStandardManifestAtAnImmutableForkRevision(bool mixed)
    {
        using var game = new TestGame();
        string manifest = mixed ? MixedManifest : Manifest;
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(uri =>
        {
            requests.Add(uri.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(uri.Host == "api.github.com"
                ? JsonSerializer.Serialize(new { sha = new string('a', 40) }) : manifest) };
        }));
        var service = new PreparationService(_ => { }, http);
        var resources = await service.ResolvePatchResourcesAsync(Path.Combine(game.Root, ".modlaunch"), Path.Combine(game.Root, "adapter.exe"), default);
        Assert.Equal(new string('a', 40), resources.Revision);
        Assert.False(resources.UnavailableOnline);
        Assert.Contains($"https://raw.githubusercontent.com/AnomalyLabsIO/SCPSL-ModPatch/{new string('a', 40)}/Shared_Files/patchinfo.json", requests);
        Assert.DoesNotContain(requests, value => value.Contains("hopperlopip", StringComparison.Ordinal));
        Assert.Equal("patchinfo.json", Path.GetFileName(resources.Path));
        Assert.Equal(FileOps.Hash(resources.Path), resources.Sha256);
        Assert.Equal(ModPatchBundle.PinPatternManifest(manifest, resources.Revision).ToJsonString(),
            JsonNode.Parse(File.ReadAllText(resources.Path))!.ToJsonString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task UnpublishedOrOfflineForkUsesTheVerifiedBundledStandardManifest(HttpStatusCode status, bool mixed)
    {
        using var game = new TestGame();
        string manifest = mixed ? MixedManifest : Manifest;
        CreateBundle(game.Root, manifest);
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(status)));
        var service = new PreparationService(_ => { }, http);
        var resources = await service.ResolvePatchResourcesAsync(Path.Combine(game.Root, ".modlaunch"), Path.Combine(game.Root, "adapter.exe"), default);
        Assert.StartsWith("bundled-", resources.Revision);
        Assert.True(resources.UnavailableOnline);
        Assert.Equal("patchinfo.json", Path.GetFileName(resources.Path));
        Assert.Equal(FileOps.Hash(resources.Path), resources.Sha256);
        Assert.Equal(ModPatchBundle.PinPatternManifest(manifest, new string('a', 40)).ToJsonString(),
            JsonNode.Parse(File.ReadAllText(resources.Path))!.ToJsonString());
    }

    [Fact]
    public async Task OfflineResourcesRejectAChangedBundledStandardManifest()
    {
        using var game = new TestGame();
        string root = CreateBundle(game.Root);
        File.AppendAllText(Path.Combine(root, "patchinfo.json"), "corrupt");
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var service = new PreparationService(_ => { }, http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ResolvePatchResourcesAsync(
            Path.Combine(game.Root, ".modlaunch"), Path.Combine(game.Root, "adapter.exe"), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedLegacyOffsetFeedUsesTheVerifiedBundledPatternManifest(bool mixed)
    {
        using var game = new TestGame();
        string manifest = mixed ? MixedManifest : Manifest;
        CreateBundle(game.Root, manifest);
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(uri =>
        {
            requests.Add(uri.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(uri.Host == "api.github.com"
                    ? JsonSerializer.Serialize(new { sha = new string('c', 40) }) : LegacyManifest)
            };
        }));
        var service = new PreparationService(_ => { }, http);
        var resources = await service.ResolvePatchResourcesAsync(Path.Combine(game.Root, ".modlaunch"), Path.Combine(game.Root, "adapter.exe"), default);
        Assert.Equal("bundled-" + new string('b', 64), resources.Revision);
        Assert.True(resources.UnavailableOnline);
        Assert.Contains($"https://raw.githubusercontent.com/AnomalyLabsIO/SCPSL-ModPatch/{new string('c', 40)}/Shared_Files/patchinfo.json", requests);
        Assert.Equal("patchinfo.json", Path.GetFileName(resources.Path));
        Assert.Equal(FileOps.Hash(resources.Path), resources.Sha256);
        Assert.Equal(ModPatchBundle.PinPatternManifest(manifest, new string('a', 40)).ToJsonString(),
            JsonNode.Parse(File.ReadAllText(resources.Path))!.ToJsonString());
    }

    [Theory]
    [InlineData("SCPSL.ModPatch.Cli.exe")]
    [InlineData("patchinfo.json")]
    public async Task PublishedLegacyOffsetFeedRejectsChangedBundledResources(string relative)
    {
        using var game = new TestGame();
        string root = CreateBundle(game.Root);
        File.AppendAllText(Path.Combine(root, relative), "corrupt");
        using var http = new HttpClient(new Handler(uri => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(uri.Host == "api.github.com"
                ? JsonSerializer.Serialize(new { sha = new string('c', 40) }) : LegacyManifest)
        }));
        var service = new PreparationService(_ => { }, http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ResolvePatchResourcesAsync(
            Path.Combine(game.Root, ".modlaunch"), Path.Combine(game.Root, "adapter.exe"), default));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("launcher")]
    [InlineData("patches")]
    [InlineData("null-entry")]
    [InlineData("empty-entry")]
    [InlineData("numeric-pattern")]
    [InlineData("empty-pattern")]
    public async Task InvalidOnlineManifestDoesNotSilentlyFallBackToTheBundle(string condition)
    {
        using var game = new TestGame();
        CreateBundle(game.Root);
        var manifest = JsonNode.Parse(LegacyManifest)!;
        switch (condition)
        {
            case "empty": manifest["versionRanges"] = new JsonArray(); break;
            case "launcher": manifest["versionRanges"]![0]!["cleanLauncherPath"] = "../SCPSL.exe"; break;
            case "patches": manifest["versionRanges"]![0]!["methods"]!["patchMethods"] = new JsonArray(); break;
            case "null-entry": manifest["versionRanges"]![0]!["methods"]!["patchMethods"]![0] = null; break;
            case "empty-entry": manifest["versionRanges"]![0]!["methods"]!["patchMethods"]![0] = new JsonObject(); break;
            case "numeric-pattern": manifest["versionRanges"]![0]!["methods"]!["patchMethods"]![0]!["patchPattern"] = 42; break;
            case "empty-pattern": manifest["versionRanges"]![0]!["methods"]!["patchMethods"]![0]!["patchPattern"] = ""; break;
        }
        using var http = new HttpClient(new Handler(uri => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(uri.Host == "api.github.com"
                ? JsonSerializer.Serialize(new { sha = new string('a', 40) })
                : manifest.ToJsonString())
        }));
        var service = new PreparationService(_ => { }, http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ResolvePatchResourcesAsync(
            Path.Combine(game.Root, ".modlaunch"), Path.Combine(game.Root, "adapter.exe"), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedLegacyAdapterUsesThePackagedForkEngine(bool patternBundle)
    {
        using var game = new TestGame();
        string cache = Path.Combine(game.Root, ".modlaunch");
        string bundled = Path.Combine(cache, "patching-tools/bundled");
        CreateBundle(bundled);
        string executable = Path.Combine(bundled, "SCPSL-ModLaunch.ModPatch.Headless.exe");
        File.WriteAllText(executable, "bundled helper; never executed");
        byte[] archive;
        using (var stream = new MemoryStream())
        {
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("SCPSL-ModLaunch.ModPatch.Headless.exe").Open()))
                    writer.Write("legacy helper; never executed");
                if (patternBundle)
                    foreach (string relative in new[] { "ModPatch/bundle.json", "ModPatch/SCPSL.ModPatch.Cli.exe", "ModPatch/patchinfo.patterns.json" })
                        using (var writer = new StreamWriter(zip.CreateEntry(relative).Open()))
                            writer.Write("legacy pattern bundle; never executed");
            }
            archive = stream.ToArray();
        }
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive));
        string name = "SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip";
        using var http = new HttpClient(new Handler(uri => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = uri.Host == "api.github.com" ? new StringContent(JsonSerializer.Serialize(new
            {
                draft = false, prerelease = false,
                assets = new[] { new { name, digest = "sha256:" + digest,
                    browser_download_url = "https://github.com/AnomalyLabsIO/SCPSL-ModLaunch/releases/download/v0.1.0/" + name } }
            })) : new ByteArrayContent(archive)
        }));
        var service = new PreparationService(_ => { }, http);
        Assert.Equal(Path.GetFullPath(executable), await service.ResolveAdapterAsync(cache, "AnomalyLabsIO/SCPSL-ModLaunch", default));
    }

    [Fact]
    public void CompleteForkReportVerifiesBothOutputsAndPreservesOtherInputs()
    {
        using var fixture = new ReportFixture();
        var result = fixture.Validate();
        Assert.Equal(new Version(14, 2, 8), result.GameVersion);
        Assert.Equal("applied", Assert.Single(result.Patches).Status);
        Assert.True(result.MetadataNormalizedForParser);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("metadata-mode")]
    [InlineData("profile")]
    [InlineData("unity")]
    [InlineData("warning")]
    [InlineData("prepared")]
    [InlineData("missing")]
    [InlineData("optional")]
    [InlineData("omitted-output")]
    [InlineData("wrong-hash")]
    [InlineData("metadata-write")]
    [InlineData("outside-path")]
    public void InvalidOrPartialForkResultsCannotBeActivated(string condition)
    {
        using var fixture = new ReportFixture();
        var report = fixture.Report;
        switch (condition)
        {
            case "success": report["success"] = false; break;
            case "metadata-mode": report["metadataMode"] = "write"; break;
            case "profile": report["profileIndex"] = 8; break;
            case "unity": report["unityVersion"] = "different"; break;
            case "warning": report["warnings"]!.AsArray().Add("unverified file"); break;
            case "prepared": report["patches"]![0]!["status"] = "prepared"; break;
            case "missing": report["patches"]![0]!["status"] = "skipped-missing"; break;
            case "optional": report["patches"]![0]!["status"] = "skipped-optional"; break;
            case "omitted-output": report["modifiedFiles"]!.AsArray().RemoveAt(0); break;
            case "wrong-hash": report["modifiedFiles"]![0]!["afterSha256"] = new string('f', 64); break;
            case "metadata-write": File.AppendAllText(Path.Combine(fixture.Game.Root, SessionTransaction.RuntimeMetadataPath), "changed"); break;
            case "outside-path": report["modifiedFiles"]![0]!["path"] = Path.Combine(fixture.Game.Root, "..", "other.exe"); break;
        }
        Assert.Throws<InvalidDataException>(() => fixture.Validate());
    }

    [Fact]
    public void MetadataNormalizationOnlyChangesParserClone()
    {
        byte[] bytes = new byte[264];
        BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0);
        BitConverter.GetBytes(23).CopyTo(bytes, 4);
        BitConverter.GetBytes(264).CopyTo(bytes, 8);
        BitConverter.GetBytes(92).CopyTo(bytes, 164);
        var parser = MetadataHeaderNormalizer.NormalizeBytesForParser(bytes);
        Assert.NotSame(bytes, parser);
        Assert.Equal(24, BitConverter.ToInt32(parser, 4));
        Assert.Equal(23, BitConverter.ToInt32(bytes, 4));
    }

    private static string CreateBundle(string parent, string manifest = Manifest)
    {
        string root = Path.Combine(parent, "ModPatch");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ModPatchBundle.Executable), "mock CLI identity; never executed");
        File.WriteAllText(Path.Combine(root, ModPatchBundle.Manifest), manifest);
        FileOps.WriteJson(Path.Combine(root, "bundle.json"), new ModPatchBundle
        {
            SchemaVersion = 1, SourceRepository = ModPatchBundle.Repository,
            SourceRevision = new string('a', 40), SourceSha256 = new string('b', 64),
            Files = [new PreparedFile(ModPatchBundle.Executable, FileOps.Hash(Path.Combine(root, ModPatchBundle.Executable))),
                new PreparedFile(ModPatchBundle.Manifest, FileOps.Hash(Path.Combine(root, ModPatchBundle.Manifest)))]
        });
        return root;
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!));
    }

    private sealed class ReportFixture : IDisposable
    {
        internal TestGame Game { get; } = new();
        internal Dictionary<string, string> Originals { get; }
        internal string LauncherHash { get; }
        internal JsonObject Report { get; }
        internal ReportFixture()
        {
            Originals = Game.Originals.Keys.ToDictionary(name => name, name => FileOps.Hash(Path.Combine(Game.Root, name)));
            foreach (string name in new[] { "SCPSL.exe", "GameAssembly.dll" }) File.AppendAllText(Path.Combine(Game.Root, name), "prepared");
            LauncherHash = FileOps.Hash(Path.Combine(Game.Root, "SCPSL.exe"));
            Report = JsonSerializer.SerializeToNode(new
            {
                schemaVersion = 1, success = true, exitCode = 0, command = "patch", stage = "complete", error = (object?)null,
                metadataMode = "preserve", metadataVersion = 31, metadataNormalizedForParser = true,
                unityVersion = "6000.0.43f1", gameVersion = "14.2.8", profileIndex = 0, warnings = Array.Empty<string>(),
                patches = new[] { new { name = "Fixture$$Run", status = "applied", message = "prepared" } },
                modifiedFiles = new[] { "SCPSL.exe", "GameAssembly.dll" }.Select(name => new
                { path = Path.Combine(Game.Root, name), beforeSha256 = Originals[name], afterSha256 = FileOps.Hash(Path.Combine(Game.Root, name)) })
            })!.AsObject();
        }
        internal ForkPatchEngine.Prepared Validate()
        {
            using var report = JsonDocument.Parse(Report.ToJsonString());
            using var manifest = JsonDocument.Parse(Manifest);
            return ForkPatchEngine.ValidateResult(report.RootElement, manifest.RootElement, Game.Root, "6000.0.43f1", LauncherHash, Originals);
        }
        public void Dispose() => Game.Dispose();
    }
}
