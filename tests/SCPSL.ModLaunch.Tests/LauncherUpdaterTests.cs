using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SCPSL.ModLaunch.Core;
using SCPSL.ModLaunch.Core.Provisioning;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class LauncherUpdaterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadIsVerifiedAndStagedUntilCleanup(bool apiDigest)
    {
        using var fixture = new Fixture(apiDigest: apiDigest);
        using var updater = fixture.Updater();
        await updater.CheckAsync();
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Executable));
        Assert.True(File.Exists(fixture.Pending));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.Staged));
        Assert.Equal(apiDigest ? 2 : 3, fixture.Requests.Count);
        Assert.True(updater.TryApplyPending());
        Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.Executable));
        Assert.Equal(fixture.Executable, updater.AppliedExecutablePath);
        Assert.False(File.Exists(fixture.Pending));
        Assert.Equal(fixture.Original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(fixture.Updates, "previous-*.bin"))));
    }

    [Theory]
    [InlineData("no-release")]
    [InlineData("missing-exe")]
    [InlineData("duplicate-exe")]
    [InlineData("prerelease")]
    [InlineData("draft")]
    [InlineData("equal-version")]
    [InlineData("older-version")]
    [InlineData("bad-tag")]
    [InlineData("invalid-json")]
    [InlineData("empty-json")]
    [InlineData("offline")]
    [InlineData("rate-limited")]
    [InlineData("size-overflow")]
    public async Task UnavailableOrUnusableReleaseNeverBlocksTheLauncher(string scenario)
    {
        using var fixture = new Fixture();
        switch (scenario)
        {
            case "no-release": fixture.Status = HttpStatusCode.NotFound; break;
            case "missing-exe": fixture.Release["assets"] = new JsonArray(); break;
            case "duplicate-exe": fixture.Assets.Add(JsonNode.Parse(fixture.Asset.ToJsonString())); break;
            case "prerelease": fixture.Release["prerelease"] = true; break;
            case "draft": fixture.Release["draft"] = true; break;
            case "equal-version": fixture.Release["tag_name"] = "v0.1.0"; break;
            case "older-version": fixture.Release["tag_name"] = "v0.0.9"; break;
            case "bad-tag": fixture.Release["tag_name"] = "nightly"; break;
            case "invalid-json": fixture.JsonOverride = "not json"; break;
            case "empty-json": fixture.JsonOverride = "{}"; break;
            case "offline": fixture.NetworkError = true; break;
            case "rate-limited": fixture.Status = HttpStatusCode.Forbidden; break;
            case "size-overflow": fixture.Asset["size"] = JsonNode.Parse("9223372036854775808"); break;
        }
        using var updater = fixture.Updater();
        await updater.CheckAsync();
        Assert.False(updater.TryApplyPending());
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Executable));
        Assert.False(File.Exists(fixture.Pending));
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData("corrupt-download")]
    [InlineData("missing-checksum")]
    [InlineData("wrong-checksum-name")]
    [InlineData("external-url")]
    [InlineData("wrong-release-url")]
    [InlineData("wrong-length")]
    [InlineData("oversized")]
    [InlineData("wrong-executable")]
    public async Task InvalidAssetCannotReplaceTheLauncher(string scenario)
    {
        using var fixture = new Fixture(apiDigest: scenario is not ("missing-checksum" or "wrong-checksum-name"));
        switch (scenario)
        {
            case "corrupt-download": fixture.ResponseBytes = [99]; break;
            case "missing-checksum": fixture.Assets.RemoveAt(1); break;
            case "wrong-checksum-name": fixture.Checksum = fixture.Digest + "  unrelated.exe\n"; break;
            case "external-url": fixture.Asset["browser_download_url"] = "https://example.invalid/launcher.exe"; break;
            case "wrong-release-url": fixture.Asset["browser_download_url"] = Fixture.AssetUrl("v0.1.2", LauncherInstallation.ExecutableName); break;
            case "wrong-length": fixture.Asset["size"] = fixture.Payload.Length + 1; break;
            case "oversized": fixture.Asset["size"] = 65L * 1024 * 1024; break;
        }
        using var updater = fixture.Updater(validate: scenario == "wrong-executable"
            ? (_, _) => throw new InvalidDataException("wrong product/version") : null);
        await updater.CheckAsync();
        Assert.False(File.Exists(fixture.Pending));
        Assert.False(updater.TryApplyPending());
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Executable));
        if (Directory.Exists(fixture.Updates)) Assert.Empty(Directory.GetFiles(fixture.Updates, "*.part"));
    }

    [Fact]
    public async Task AlreadyValidatedDownloadSurvivesOfflineOrMissingNewRelease()
    {
        using var fixture = new Fixture();
        using var updater = fixture.Updater();
        await updater.CheckAsync();
        fixture.NetworkError = true;
        await updater.CheckAsync();
        fixture.NetworkError = false;
        fixture.Release["tag_name"] = "v0.1.2";
        fixture.Release["assets"] = new JsonArray();
        await updater.CheckAsync();
        Assert.True(File.Exists(fixture.Pending));
        Assert.True(updater.TryApplyPending());
        Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.Executable));
    }

    [Fact]
    public async Task RepeatedCheckDoesNotDownloadTheSameVersionAgain()
    {
        using var fixture = new Fixture();
        using var updater = fixture.Updater();
        await updater.CheckAsync();
        await updater.CheckAsync();
        Assert.Equal(3, fixture.Requests.Count); // release, EXE, release
        Assert.Single(fixture.Requests.Where(url => url.EndsWith(".exe", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpdateCanCompleteAfterFailedPreparationWithGameRecoveryStillPending()
    {
        using var fixture = new Fixture(installed: true);
        fixture.Game.Write(".modlaunch/session.json", Encoding.UTF8.GetBytes("pending game recovery"));
        fixture.Delay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var updater = fixture.Updater();
        Task check = updater.CheckAsync();
        Assert.False(check.IsCompleted);
        try { throw new InvalidDataException("simulated failed game preparation"); }
        catch (InvalidDataException) { fixture.Delay.SetResult(true); }
        finally { await check; Assert.True(updater.TryApplyPending()); }
        Assert.Equal("pending game recovery", File.ReadAllText(Path.Combine(fixture.Game.Root, ".modlaunch/session.json")));
        foreach (var (name, original) in fixture.Game.Originals) Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Game.Root, name)));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.Executable));
    }

    [Fact]
    public async Task SlowNetworkHasAnIndependentBoundedTimeout()
    {
        using var fixture = new Fixture();
        fixture.Delay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var updater = fixture.Updater(timeout: TimeSpan.FromMilliseconds(100));
        await updater.CheckAsync();
        Assert.Contains(fixture.Logs, text => text.Contains("check skipped", StringComparison.Ordinal));
        Assert.False(File.Exists(fixture.Pending));
    }

    [Fact]
    public async Task InstalledUpdatePreservesShortcutAndRecordsNewOwnership()
    {
        using var fixture = new Fixture(installed: true);
        using var updater = fixture.Updater();
        await updater.CheckAsync();
        Assert.True(updater.TryApplyPending());
        var owner = FileOps.ReadJson<LauncherInstallationOwnership>(fixture.Ownership);
        Assert.Equal(fixture.Digest, owner.ExecutableSha256);
        Assert.Null(owner.PendingExecutableSha256);
        Assert.Equal(FileOps.Hash(fixture.Shortcut), owner.ShortcutSha256);
        Assert.Equal("custom shortcut arguments and icon", File.ReadAllText(fixture.Shortcut));
        Assert.False(File.Exists(Path.Combine(fixture.Game.Root, LauncherInstallation.ExecutableName)));
    }

    [Fact]
    public async Task FirstInstallCanRelocateBeforeApplyingTheDownloadedUpdate()
    {
        using var fixture = new Fixture();
        using var updater = fixture.Updater();
        await updater.CheckAsync();
        string installed = Path.Combine(fixture.Game.Root, ".modlaunch", LauncherInstallation.ExecutableName);
        File.Move(fixture.Executable, installed);
        Assert.True(updater.TryApplyPending());
        Assert.Equal(installed, updater.AppliedExecutablePath);
        Assert.Equal(fixture.Payload, File.ReadAllBytes(installed));
        Assert.Equal(fixture.Digest, FileOps.ReadJson<LauncherInstallationOwnership>(fixture.Ownership).ExecutableSha256);
    }

    [Theory]
    [InlineData("update-journal-written")]
    [InlineData("update-ownership-prepared")]
    [InlineData("update-executable-replaced")]
    [InlineData("update-ownership-completed")]
    public async Task InterruptedUpdatesCanResumeWithoutLeavingTheTargetMissing(string stop)
    {
        using var fixture = new Fixture(installed: true);
        using (var updater = fixture.Updater(checkpoint: point => { if (point == stop) throw new InjectedFailure(); }))
        {
            await updater.CheckAsync();
            Assert.Throws<InjectedFailure>(() => updater.TryApplyPending());
        }
        Assert.True(File.Exists(fixture.Executable));
        Assert.True(File.Exists(fixture.Pending));
        bool alreadyApplied = FileOps.HashEquals(FileOps.Hash(fixture.Executable), fixture.Digest);
        using var next = fixture.Updater(version: alreadyApplied ? new Version(0, 1, 1) : new Version(0, 1, 0));
        Assert.Equal(!alreadyApplied, next.TryApplyPending());
        Assert.False(File.Exists(fixture.Pending));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.Executable));
        var owner = FileOps.ReadJson<LauncherInstallationOwnership>(fixture.Ownership);
        Assert.Equal(fixture.Digest, owner.ExecutableSha256);
        Assert.Null(owner.PendingExecutableSha256);
    }

    [Fact]
    public async Task DeferredRootUpdateFollowsFirstInstallationRelocation()
    {
        using var fixture = new Fixture();
        using (var interrupted = fixture.Updater(checkpoint: _ => throw new InjectedFailure()))
        {
            await interrupted.CheckAsync();
            Assert.Throws<InjectedFailure>(() => interrupted.TryApplyPending());
        }
        string installed = Path.Combine(fixture.Game.Root, ".modlaunch", LauncherInstallation.ExecutableName);
        File.Move(fixture.Executable, installed);
        using var updater = fixture.Updater(executable: installed);
        Assert.True(updater.TryApplyPending());
        Assert.Equal(fixture.Payload, File.ReadAllBytes(installed));
        Assert.False(File.Exists(fixture.Pending));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("payload")]
    [InlineData("ownership")]
    [InlineData("journal")]
    public async Task UnexpectedChangesAreRetainedWithoutOverwritingAnything(string changed)
    {
        using var fixture = new Fixture(installed: true);
        using var updater = fixture.Updater();
        await updater.CheckAsync();
        switch (changed)
        {
            case "target": File.WriteAllText(fixture.Executable, "someone else's launcher"); break;
            case "payload": File.WriteAllText(fixture.Staged, "corrupt download"); break;
            case "ownership": FileOps.WriteJson(fixture.Ownership, new LauncherInstallationOwnership { ExecutableSha256 = new string('0', 64) }); break;
            case "journal": File.WriteAllText(fixture.Pending, "{\"version\":null}"); break;
        }
        string targetHash = FileOps.Hash(fixture.Executable);
        Assert.False(updater.TryApplyPending());
        Assert.Equal(targetHash, FileOps.Hash(fixture.Executable));
        Assert.True(File.Exists(fixture.Pending));
        Assert.True(File.Exists(fixture.Staged));
    }

    [Fact]
    public async Task LockedExecutableDefersAndRetries()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing flags are enforced only on Windows.
        using var fixture = new Fixture(installed: true);
        using var updater = fixture.Updater();
        await updater.CheckAsync();
        using (var held = new FileStream(fixture.Executable, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(updater.TryApplyPending());
            Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Executable));
            Assert.True(File.Exists(fixture.Pending));
        }
        Assert.True(updater.TryApplyPending());
        Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.Executable));
    }

    [Fact]
    public void UpdaterCanLocateTheInstallationEvenWhenTheGameIsMissing()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Game.Root, "SCPSL.exe"));
        Assert.Equal(fixture.Game.Root, LauncherInstallation.ResolveGameDirectory(fixture.Executable, requireGameFiles: false));
        Assert.Throws<InvalidDataException>(() => LauncherInstallation.ResolveGameDirectory(fixture.Executable));
    }

    [Theory]
    [InlineData("v0.10.2", "0.10.2.0")]
    [InlineData("1.0.0", "1.0.0.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    public void NumericReleaseTagsMatchWindowsFileVersions(string tag, string expected)
    {
        Assert.True(LauncherUpdater.TryReleaseVersion(tag, out var version));
        Assert.Equal(new Version(expected), version);
        Assert.True(new Version(0, 10, 2) > new Version(0, 9, 9));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-beta.1")]
    [InlineData("1.2.3+other")]
    [InlineData("65536.1.0")]
    [InlineData("999999999999999999999.1.0")]
    public void NonReleaseTagsAreIgnored(string? tag) => Assert.False(LauncherUpdater.TryReleaseVersion(tag, out _));

    [Fact]
    public void TextFileIsNotAnExecutableEvenWithAMatchingHash()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => LauncherUpdater.ValidateExecutable(fixture.Executable, new Version(0, 1, 0)));
    }

    [Fact]
    public async Task ReleaseWithoutCompanionUsesTheBundledAdapter()
    {
        using var fixture = new Fixture();
        const string relative = ".modlaunch/tools/adapter-bundled/SCPSL-ModLaunch.ModPatch.Headless.exe";
        fixture.Game.Write(relative, Encoding.UTF8.GetBytes("bundled adapter"));
        var service = new PreparationService(fixture.Logs.Add, fixture.Http);
        string adapter = await service.ResolveAdapterAsync(Path.Combine(fixture.Game.Root, ".modlaunch"), LauncherUpdater.Repository, CancellationToken.None);
        Assert.Equal(Path.GetFullPath(Path.Combine(fixture.Game.Root, relative)), adapter);
        Assert.Contains(fixture.Logs, text => text.Contains("no companion asset", StringComparison.Ordinal));
    }

    private sealed class InjectedFailure : Exception;

    private sealed class Fixture : IDisposable
    {
        public TestGame Game { get; } = new();
        public string Executable { get; }
        public string Updates => Path.Combine(Game.Root, ".modlaunch/updates");
        public string Pending => Path.Combine(Updates, "pending.json");
        public string Staged => Path.Combine(Updates, "launcher-" + Digest + ".bin");
        public string Ownership => Path.Combine(Game.Root, ".modlaunch/launcher-installation.json");
        public string Shortcut => Path.Combine(Game.Root, LauncherInstallation.ShortcutName);
        public byte[] Original { get; } = Encoding.UTF8.GetBytes("original launcher fixture");
        public byte[] Payload { get; } = Encoding.UTF8.GetBytes("release launcher fixture");
        public string Digest => Convert.ToHexString(SHA256.HashData(Payload));
        public List<string> Requests { get; } = [];
        public List<string> Logs { get; } = [];
        public HttpClient Http { get; }
        public JsonObject Release { get; }
        public JsonArray Assets => Release["assets"]!.AsArray();
        public JsonObject Asset => Assets[0]!.AsObject();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool NetworkError { get; set; }
        public string? JsonOverride { get; set; }
        public string Checksum { get; set; }
        public byte[]? ResponseBytes { get; set; }
        public TaskCompletionSource<bool>? Delay { get; set; }

        public Fixture(bool installed = false, bool apiDigest = true)
        {
            Executable = Path.Combine(Game.Root, installed ? ".modlaunch/" + LauncherInstallation.ExecutableName : LauncherInstallation.ExecutableName);
            Directory.CreateDirectory(Path.GetDirectoryName(Executable)!);
            File.WriteAllBytes(Executable, Original);
            File.WriteAllText(Shortcut, "custom shortcut arguments and icon");
            if (installed) FileOps.WriteJson(Ownership, new LauncherInstallationOwnership
            {
                ExecutableSha256 = FileOps.Hash(Executable), ShortcutSha256 = FileOps.Hash(Shortcut)
            });
            const string tag = "v0.1.1";
            Release = new JsonObject
            {
                ["draft"] = false, ["prerelease"] = false, ["tag_name"] = tag,
                ["assets"] = new JsonArray(new JsonObject
                {
                    ["name"] = LauncherInstallation.ExecutableName, ["size"] = Payload.Length,
                    ["digest"] = apiDigest ? "sha256:" + Digest : null,
                    ["browser_download_url"] = AssetUrl(tag, LauncherInstallation.ExecutableName)
                })
            };
            if (!apiDigest) Assets.Add(new JsonObject
            {
                ["name"] = LauncherInstallation.ExecutableName + ".sha256",
                ["browser_download_url"] = AssetUrl(tag, LauncherInstallation.ExecutableName + ".sha256")
            });
            Checksum = Digest.ToLowerInvariant() + "  " + LauncherInstallation.ExecutableName + "\n";
            Http = new HttpClient(new Handler(async (request, ct) =>
            {
                string url = request.RequestUri!.AbsoluteUri;
                Requests.Add(url);
                if (Delay is not null) await Delay.Task.WaitAsync(ct);
                if (NetworkError) throw new HttpRequestException("offline fixture");
                if (url.EndsWith("/releases/latest", StringComparison.Ordinal))
                    return new HttpResponseMessage(Status) { Content = new StringContent(JsonOverride ?? Release.ToJsonString()) };
                if (url.EndsWith(".sha256", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Checksum) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ResponseBytes ?? Payload) };
            }));
        }

        public static string AssetUrl(string tag, string name) => $"https://github.com/{LauncherUpdater.Repository}/releases/download/{tag}/{name}";

        public LauncherUpdater Updater(Action<string>? checkpoint = null, Action<string, Version>? validate = null,
            TimeSpan? timeout = null, Version? version = null, string? executable = null) => new(Game.Root, executable ?? Executable,
                version ?? new Version(0, 1, 0), Http, Logs.Add, validate ?? ((path, _) => Assert.Equal(Payload, File.ReadAllBytes(path))), checkpoint, timeout);

        public void Dispose() { Http.Dispose(); Game.Dispose(); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
