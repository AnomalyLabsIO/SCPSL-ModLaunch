using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SCPSL.ModLaunch.Core.Provisioning;

/// <summary>Prepares immutable session inputs entirely below the private cache directory.</summary>
public sealed class PreparationService
{
    private const string ModPatchRepository = "hopperlopip/SCPSL-ModPatch";
    private const string MelonVersion = "0.7.3";
    private const string MelonHash = "5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416";
    private const string AdapterAsset = "SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip";
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly ArtifactCache _artifacts;

    public PreparationService(Action<string> log) : this(log, CreateClient()) { }

    // An injectable transport lets tests exercise invalid/new/offline resource behavior without the network.
    public PreparationService(Action<string> log, HttpClient http)
    {
        _log = log;
        _http = http;
        _artifacts = new ArtifactCache(http, log);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SCPSL-ModLaunch/0.1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<PreparedSession> PrepareAsync(string gameDirectory, string cacheDirectory, ModLaunchSettings settings, CancellationToken ct)
    {
        if (settings.MelonLoaderVersion != MelonVersion)
            throw new InvalidOperationException($"This launcher build targets MelonLoader {MelonVersion}. Update the melonLoaderVersion setting to match.");
        ValidateRepository(settings.AdapterRepository);
        gameDirectory = Path.GetFullPath(gameDirectory);
        cacheDirectory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var hostRuntime = HostRuntime.Resolve();
        await ProbeRuntimeAsync(hostRuntime.DotnetRoot, ct);
        var inputs = CaptureInputs(gameDirectory);
        var inputKey = ComputeHash(JsonSerializer.Serialize(inputs, JsonDefaults.Options));
        _log("Checking the adapter and upstream patch resources...");
        string adapter;
        PatchResources resources;
        try
        {
            adapter = await ResolveAdapterAsync(cacheDirectory, settings.AdapterRepository, ct);
            resources = await ResolvePatchResourcesAsync(cacheDirectory, ct);
        }
        catch (Exception ex) when (IsUnavailable(ex, ct))
        {
            _log($"Update check is unavailable: {ex.Message}");
            var offline = FindOfflineSession(cacheDirectory, inputKey, settings);
            if (offline is null)
                throw new IOException("The update service is unavailable and no complete, validated preparation exists for these exact game files. Connect to the internet and retry.", ex);
            offline.DotnetRoot = hostRuntime.DotnetRoot;
            _log("Using the complete validated preparation for these unchanged game files.");
            return offline;
        }
        var adapterHash = HashTree(Path.GetDirectoryName(adapter)!);
        var fingerprint = ComputeHash($"layout-2\n{inputKey}\n{resources.Revision}\n{resources.Sha256}\n{adapterHash}\n{settings.MelonLoaderVersion}");
        var preparedDirectory = ArtifactCache.SafePath(cacheDirectory, $"prepared/{fingerprint}");
        var manifestPath = Path.Combine(preparedDirectory, "prepared.json");
        if (Directory.Exists(preparedDirectory))
        {
            try
            {
                if (File.Exists(manifestPath))
                {
                    var manifest = ReadManifest(manifestPath);
                    manifest.Session.DotnetRoot = hostRuntime.DotnetRoot;
                    if (manifest.InputKey == inputKey && manifest.AdapterRepository == settings.AdapterRepository &&
                        manifest.Session.Fingerprint == fingerprint && ValidateSession(manifest.Session, preparedDirectory, cacheDirectory, inputKey))
                    {
                        manifest.Session.DotnetRoot = hostRuntime.DotnetRoot;
                        _log("Reusing verified prepared files; no patching or assembly regeneration is needed.");
                        return manifest.Session;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { _log("Cached preparation is invalid: " + ex.Message); }
            _log("Cached preparation is incomplete or changed; preparing it again.");
            Directory.Move(preparedDirectory, preparedDirectory + ".invalid-" + Guid.NewGuid().ToString("N"));
        }

        CheckFreeSpace(cacheDirectory, gameDirectory, inputs);
        var dotnetRoot = hostRuntime.DotnetRoot;
        var melonZip = await _artifacts.DownloadAsync(
            $"https://github.com/LavaGang/MelonLoader/releases/download/v{MelonVersion}/MelonLoader.x64.zip",
            ArtifactCache.SafePath(cacheDirectory, $"downloads/MelonLoader-{MelonVersion}.x64.zip"), MelonHash, false, ct);
        var work = ArtifactCache.SafePath(cacheDirectory, $"work/{Guid.NewGuid().ToString("N")[..8]}");
        var stage = Path.Combine(work, "game");
        Directory.CreateDirectory(stage);
        var bootstrapHash = ArtifactCache.ExtractMelonRuntime(melonZip, stage);
        RequireMelonLayout(stage);
        const string generatorConfig = "MelonLoader/Dependencies/Il2CppAssemblyGenerator/Config.cfg";
        string existingGeneratorConfig = ArtifactCache.SafePath(gameDirectory, generatorConfig);
        if (File.Exists(existingGeneratorConfig))
            FileOps.CopyVerified(existingGeneratorConfig, ArtifactCache.SafePath(stage, generatorConfig), ArtifactCache.Hash(existingGeneratorConfig));
        _log("Creating staged copies of the game files...");
        foreach (var input in inputs.Where(x => x.Sha256 is not null))
        {
            ct.ThrowIfCancellationRequested();
            CopyVerified(ArtifactCache.SafePath(gameDirectory, input.RelativePath), ArtifactCache.SafePath(stage, input.RelativePath), input.Sha256!);
        }
        var reportPath = Path.Combine(work, "patch-result.json");
        _log("Running the headless ModPatch adapter against staged copies...");
        await ToolRunner.RunAsync(adapter,
            ["prepare", "--game-dir", stage, "--patch-info", resources.Path, "--result", reportPath,
             "--patch-revision", resources.Revision, "--cache-dir", ArtifactCache.SafePath(cacheDirectory, "downloads/adapter-dependencies")],
            work, dotnetRoot, line => _log("ModPatch: " + line), TimeSpan.FromMinutes(30), ct);
        var report = ArtifactCache.ReadJson<PatchResult>(reportPath);
        ValidateReport(report, stage, resources.Revision);
        // Runtime metadata is never replaced with the parser-normalized temporary copy.
        var metadata = inputs.Single(x => x.RelativePath.EndsWith("global-metadata.dat", StringComparison.Ordinal));
        if (!ArtifactCache.Matches(ArtifactCache.SafePath(stage, metadata.RelativePath), metadata.Sha256!))
            throw new InvalidDataException("The adapter changed runtime metadata. Only its private parser copy may be normalized.");

        var runtimeFiles = new List<PreparedFile>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(stage, "MelonLoader"), "*", SearchOption.AllDirectories))
        {
            var relative = Relative(stage, path);
            if (IsGenerationScratch(relative)) continue;
            ArtifactCache.SafePath(stage, relative);
            runtimeFiles.Add(new PreparedFile(relative, ArtifactCache.Hash(path)));
        }
        RuntimeInstallation.InstallFromStage(gameDirectory, stage, runtimeFiles, fingerprint, _log);
        if (!RuntimeInstallation.ValidateInstalled(gameDirectory, runtimeFiles, fingerprint))
            throw new InvalidDataException("The persistent MelonLoader runtime did not validate after preparation.");

        // The published preparation contains only patched game payloads. Generated assemblies
        // and downloads now live in the single owned game-root MelonLoader directory.
        string publication = Path.Combine(work, "ready");
        string payloads = Path.Combine(publication, "payloads");
        Directory.CreateDirectory(payloads);
        var outputs = new List<PreparedFile>();
        foreach (var output in report.Files.Where(x => !x.RelativePath.StartsWith("MelonLoader/", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(x => x.RelativePath, StringComparer.Ordinal))
        {
            string relative = output.RelativePath.Replace('\\', '/');
            string sourcePath = $"{outputs.Count:D3}.bin";
            CopyVerified(ArtifactCache.SafePath(stage, relative), ArtifactCache.SafePath(payloads, sourcePath), output.Sha256);
            outputs.Add(new PreparedFile(relative, output.Sha256, sourcePath));
        }
        var session = new PreparedSession
        {
            LayoutVersion = 2,
            PreparedDirectory = Path.Combine(preparedDirectory, "payloads"),
            Fingerprint = fingerprint,
            GameVersion = report.GameVersion,
            UnityVersion = report.UnityVersion,
            DotnetRoot = dotnetRoot,
            AdapterDirectory = Path.GetDirectoryName(adapter)!,
            MelonLoaderArchive = melonZip,
            BootstrapHash = bootstrapHash,
            Inputs = inputs,
            Files = outputs,
            RuntimeFiles = runtimeFiles.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToList()
        };
        var outputManifest = new PreparationManifest(inputKey, settings.AdapterRepository, MelonVersion, session);
        await ArtifactCache.WriteJsonAsync(Path.Combine(publication, "prepared.json"), outputManifest, ct);
        CopyVerified(reportPath, Path.Combine(publication, "patch-result.json"), ArtifactCache.Hash(reportPath));
        Directory.CreateDirectory(Path.GetDirectoryName(preparedDirectory)!);
        Directory.Move(publication, preparedDirectory);
        DeleteWorkDirectory(cacheDirectory, work);
        _log($"Prepared SCP:SL {report.GameVersion} / Unity {report.UnityVersion}.");
        return session;
    }

    internal async Task<string> ResolveAdapterAsync(string cacheDirectory, string repository, CancellationToken ct)
    {
        string? json = null;
        try { json = await _artifacts.GetTextAsync($"https://api.github.com/repos/{repository}/releases/latest", ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _log("No companion release is published yet; checking the bundled adapter.");
        }
        catch (Exception ex) when (IsUnavailable(ex, ct))
        {
            var bundled = FindBundledAdapter(cacheDirectory);
            if (bundled is null) throw;
            _log("Adapter update service unavailable; using the packaged adapter.");
            return bundled;
        }
        if (json is null)
            return FindBundledAdapter(cacheDirectory) ?? throw new InvalidOperationException("No published or bundled headless adapter was found. Use a complete SCPSL-ModLaunch release package.");
        using var release = JsonDocument.Parse(json);
        if (release.RootElement.GetProperty("draft").GetBoolean() || release.RootElement.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("The adapter update channel returned an unpublished or prerelease asset.");
        var assets = release.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var candidates = assets.Where(x => x.GetProperty("name").GetString() == AdapterAsset).ToArray();
        if (candidates.Length == 0 && FindBundledAdapter(cacheDirectory) is { } fallback)
        {
            _log("The latest release has no companion asset; using the packaged adapter.");
            return fallback;
        }
        if (candidates.Length != 1) throw new InvalidDataException("The latest adapter release does not contain exactly one expected Windows x64 companion asset.");
        var asset = candidates[0];
        var url = RequiredAssetUrl(asset, repository);
        string? digest = null;
        if (asset.TryGetProperty("digest", out var property) && property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } apiDigest && apiDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = apiDigest[7..];
        if (!ArtifactCache.IsHash(digest))
        {
            var checksums = assets.Where(x => x.GetProperty("name").GetString() == AdapterAsset + ".sha256").ToArray();
            if (checksums.Length != 1) throw new InvalidDataException("The adapter release has no SHA-256 digest or checksum sidecar.");
            var checksum = await _artifacts.GetTextAsync(RequiredAssetUrl(checksums[0], repository), ct);
            var tokens = checksum.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length is < 1 or > 2 || !ArtifactCache.IsHash(tokens[0]) ||
                (tokens.Length == 2 && tokens[1].TrimStart('*') != AdapterAsset)) throw new InvalidDataException("Invalid adapter checksum sidecar.");
            digest = tokens[0];
        }
        var zip = await _artifacts.DownloadAsync(url, ArtifactCache.SafePath(cacheDirectory, $"downloads/adapter-{digest}.zip"), digest!, false, ct);
        var directory = ArtifactCache.ExtractVerified(zip, ArtifactCache.SafePath(cacheDirectory, $"tools/adapter-{digest}"));
        var adapter = ArtifactCache.SafePath(directory, "SCPSL-ModLaunch.ModPatch.Headless.exe");
        if (!File.Exists(adapter)) throw new InvalidDataException("The companion archive is missing SCPSL-ModLaunch.ModPatch.Headless.exe.");
        return adapter;
    }

    private static string RequiredAssetUrl(JsonElement asset, string repository)
    {
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https" || parsed.Host != "github.com" ||
            !parsed.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("Adapter release asset points outside its configured GitHub repository.");
        return url;
    }

    private static string? FindBundledAdapter(string cacheDirectory)
    {
        foreach (var root in new[] { ArtifactCache.SafePath(cacheDirectory, "tools/adapter-bundled"), Path.Combine(AppContext.BaseDirectory, "adapter") })
        {
            var path = ArtifactCache.SafePath(root, "SCPSL-ModLaunch.ModPatch.Headless.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private async Task<PatchResources> ResolvePatchResourcesAsync(string cacheDirectory, CancellationToken ct)
    {
        using var commit = JsonDocument.Parse(await _artifacts.GetTextAsync($"https://api.github.com/repos/{ModPatchRepository}/commits/master", ct));
        var revision = commit.RootElement.GetProperty("sha").GetString();
        if (!ArtifactCache.IsHash(revision, 40)) throw new InvalidDataException("Upstream did not provide an immutable patch revision.");
        var text = await _artifacts.GetTextAsync($"https://raw.githubusercontent.com/{ModPatchRepository}/{revision}/Shared_Files/patchinfo.json", ct);
        var document = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("Missing patch definitions.");
        if (document["versionRanges"] is not JsonArray ranges || ranges.Count == 0) throw new InvalidDataException("Patch definitions contain no supported versions.");
        foreach (var node in ranges)
        {
            var profile = node?.AsObject() ?? throw new InvalidDataException("Invalid patch profile.");
            var url = profile["cleanLauncherUrl"]?.GetValue<string>();
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "raw.githubusercontent.com")
                throw new InvalidDataException("A patch profile has no supported upstream clean launcher URL.");
            var prefix = $"/{ModPatchRepository}/refs/heads/master/Shared_Files/clean_launchers/";
            var alternate = $"/{ModPatchRepository}/master/Shared_Files/clean_launchers/";
            string suffix;
            if (uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) suffix = uri.AbsolutePath[prefix.Length..];
            else if (uri.AbsolutePath.StartsWith(alternate, StringComparison.Ordinal)) suffix = uri.AbsolutePath[alternate.Length..];
            else throw new InvalidDataException("An upstream clean launcher path changed unexpectedly; an adapter update is required.");
            if (suffix.Contains('%') || suffix.Contains("..", StringComparison.Ordinal) || suffix.Contains('\\') || suffix.Split('/').Length != 2 || !suffix.EndsWith("/SCPSL.exe", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid upstream clean launcher path.");
            profile["cleanLauncherUrl"] = $"https://raw.githubusercontent.com/{ModPatchRepository}/{revision}/Shared_Files/clean_launchers/{suffix}";
        }
        var path = ArtifactCache.SafePath(cacheDirectory, $"resources/{revision}/patchinfo.json");
        await ArtifactCache.WriteJsonAsync(path, document, ct);
        return new PatchResources(revision!, path, ArtifactCache.Hash(path));
    }

    private async Task ProbeRuntimeAsync(string dotnetRoot, CancellationToken ct)
    {
        var runtime = HostRuntime.ResolveFromRoot(dotnetRoot);
        var executable = ArtifactCache.SafePath(dotnetRoot, "dotnet.exe");
        var runtimes = await ToolRunner.RunAsync(executable, ["--list-runtimes"], dotnetRoot, dotnetRoot, _ => { }, TimeSpan.FromMinutes(1), ct);
        if (!runtimes.Contains($"Microsoft.NETCore.App {runtime.Version} [", StringComparison.Ordinal))
            throw new InvalidOperationException($"The selected Windows x64 .NET runtime could not enumerate Microsoft.NETCore.App {runtime.Version}.");
    }

    private static List<FileIdentity> CaptureInputs(string gameDirectory)
    {
        var inputs = new List<FileIdentity>();
        foreach (var relative in new[] { "SCPSL.exe", "GameAssembly.dll", "UnityPlayer.dll", "SCPSL_Data/il2cpp_data/Metadata/global-metadata.dat", "SL-AC.dll", "SCPSL_Data/globalgamemanagers" })
        {
            var path = ArtifactCache.SafePath(gameDirectory, relative);
            if (!File.Exists(path) && relative is not "SL-AC.dll")
                throw new FileNotFoundException($"Required game file is missing: {relative}", path);
            inputs.Add(new FileIdentity(relative, File.Exists(path) ? ArtifactCache.Hash(path) : null));
        }
        return inputs;
    }

    private static void ValidateReport(PatchResult result, string stage, string revision)
    {
        if (result.SchemaVersion != 1 || result.PatchRevision != revision || string.IsNullOrWhiteSpace(result.GameVersion) || string.IsNullOrWhiteSpace(result.UnityVersion) || result.Files.Count == 0 || result.Patches.Count == 0)
            throw new InvalidDataException("The adapter returned an incomplete or incompatible preparation report.");
        if (!result.Patches.Any(x => x.Status == "applied") || result.Patches.Any(x =>
                x.Status is not "applied" and not "alternative-selected" and not "alternative-not-needed" &&
                !(x.Method == UnityBundleVersion.PatchName && x.Status == "already-correct")))
            throw new InvalidDataException("One or more required patches were not applied.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in result.Files)
        {
            var relative = file.RelativePath.Replace('\\', '/');
            if (!paths.Add(relative) || !ArtifactCache.IsHash(file.Sha256) || !ArtifactCache.Matches(ArtifactCache.SafePath(stage, relative), file.Sha256))
                throw new InvalidDataException($"The adapter output is duplicate, absent or invalid: {relative}");
            if (relative.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("UserData/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("UserLibs/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(".modlaunch/", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("version.dll", StringComparison.OrdinalIgnoreCase) || relative.Equals("SL-AC.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The adapter attempted to manage a reserved user directory: {relative}");
        }
        if (!paths.Contains("SCPSL.exe") || !paths.Contains("GameAssembly.dll"))
            throw new InvalidDataException("The adapter did not report both the clean launcher and patched GameAssembly.dll.");
        if (!paths.Contains(UnityBundleVersion.RelativePath))
            throw new InvalidDataException("The adapter did not report the required globalgamemanagers version fix. Update the headless adapter.");
        if (UnityBundleVersion.Read(ArtifactCache.SafePath(stage, UnityBundleVersion.RelativePath)) != result.GameVersion)
            throw new InvalidDataException("The staged Unity bundle version does not match the detected game version.");
    }

    private PreparedSession? FindOfflineSession(string cacheDirectory, string inputKey, ModLaunchSettings settings)
    {
        var root = ArtifactCache.SafePath(cacheDirectory, "prepared");
        if (!Directory.Exists(root)) return null;
        foreach (var directory in Directory.EnumerateDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            if (!ArtifactCache.IsHash(Path.GetFileName(directory))) continue;
            var path = ArtifactCache.SafePath(directory, "prepared.json");
            if (!File.Exists(path)) continue;
            try
            {
                var manifest = ReadManifest(path);
                manifest.Session.DotnetRoot = HostRuntime.Resolve().DotnetRoot;
                if (manifest.InputKey == inputKey && manifest.MelonLoaderVersion == settings.MelonLoaderVersion && manifest.AdapterRepository == settings.AdapterRepository &&
                    ValidateSession(manifest.Session, directory, cacheDirectory, inputKey)) return manifest.Session;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { _log("Skipping invalid cached preparation: " + ex.Message); }
        }
        return null;
    }

    internal static bool ValidateSession(PreparedSession session, string preparedDirectory, string cacheDirectory, string inputKey,
        string expectedMelonHash = MelonHash)
    {
        if (session.LayoutVersion != 2 || !ArtifactCache.IsHash(session.Fingerprint) || Path.GetFileName(preparedDirectory) != session.Fingerprint ||
            session.PreparedDirectory != Path.Combine(preparedDirectory, "payloads") ||
            session.MelonLoaderArchive != ArtifactCache.SafePath(cacheDirectory, $"downloads/MelonLoader-{MelonVersion}.x64.zip") ||
            !ArtifactCache.IsHash(session.BootstrapHash) || !ArtifactCache.Matches(session.MelonLoaderArchive, expectedMelonHash) ||
            !FileOps.HashEquals(ArtifactCache.BootstrapHash(session.MelonLoaderArchive), session.BootstrapHash) ||
            session.Files is null || session.Inputs is null || session.RuntimeFiles is null || session.Files.Count == 0 ||
            ComputeHash(JsonSerializer.Serialize(session.Inputs, JsonDefaults.Options)) != inputKey) return false;
        _ = HostRuntime.ResolveFromRoot(session.DotnetRoot);
        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in session.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.RelativePath) || !outputPaths.Add(file.RelativePath.Replace('\\', '/')) || !ArtifactCache.IsHash(file.Sha256) ||
                string.IsNullOrWhiteSpace(file.SourcePath) || !file.SourcePath.EndsWith(".bin", StringComparison.Ordinal) || !sourcePaths.Add(file.SourcePath) ||
                !ArtifactCache.Matches(ArtifactCache.SafePath(session.PreparedDirectory, file.SourcePath), file.Sha256)) return false;
            string logical = file.RelativePath.Replace('\\', '/');
            _ = ArtifactCache.SafePath(preparedDirectory, logical);
            if (logical.Equals("version.dll", StringComparison.OrdinalIgnoreCase) || logical.Equals("SL-AC.dll", StringComparison.OrdinalIgnoreCase) ||
                new[] { "MelonLoader/", "Mods/", "Plugins/", "UserData/", "UserLibs/", ".modlaunch/" }
                    .Any(prefix => logical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        }
        foreach (var required in new[] { "SCPSL.exe", "GameAssembly.dll", UnityBundleVersion.RelativePath })
            if (!outputPaths.Contains(required)) return false;
        try
        {
            var bundle = session.Files.Single(file => file.RelativePath.Equals(UnityBundleVersion.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (UnityBundleVersion.Read(ArtifactCache.SafePath(session.PreparedDirectory, bundle.SourcePath!)) != session.GameVersion)
                return false;
        }
        catch (InvalidDataException) { return false; }
        var runtimePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in session.RuntimeFiles)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.RelativePath) || !runtimePaths.Add(file.RelativePath.Replace('\\', '/')) || !ArtifactCache.IsHash(file.Sha256) ||
                !file.RelativePath.StartsWith("MelonLoader/", StringComparison.Ordinal) || file.SourcePath is not null || IsGenerationScratch(file.RelativePath)) return false;
        }
        foreach (var required in new[] { "MelonLoader/net6/MelonLoader.dll", "MelonLoader/net6/Il2CppInterop.Runtime.dll",
                     "MelonLoader/net6/MelonLoader.runtimeconfig.json", "MelonLoader/net6/MelonLoader.deps.json" })
            if (!runtimePaths.Contains(required)) return false;
        if (!Directory.EnumerateFiles(session.PreparedDirectory, "*", SearchOption.AllDirectories)
                .All(x => sourcePaths.Contains(Relative(session.PreparedDirectory, x)))) return false;
        return RuntimeInstallation.ValidateInstalled(Directory.GetParent(cacheDirectory)!.FullName, session.RuntimeFiles, session.Fingerprint);
    }

    private static PreparationManifest ReadManifest(string path)
    {
        var manifest = ArtifactCache.ReadJson<PreparationManifest>(path);
        if (manifest.Session is null || !ArtifactCache.IsHash(manifest.InputKey) || string.IsNullOrWhiteSpace(manifest.AdapterRepository) || string.IsNullOrWhiteSpace(manifest.MelonLoaderVersion))
            throw new InvalidDataException("Cached preparation manifest is incomplete.");
        return manifest;
    }

    private static void RequireMelonLayout(string root)
    {
        foreach (var relative in new[] { "MelonLoader/net6/MelonLoader.dll", "MelonLoader/net6/Il2CppInterop.Runtime.dll", "MelonLoader/net6/MelonLoader.runtimeconfig.json", "MelonLoader/net6/MelonLoader.deps.json" })
            if (!File.Exists(ArtifactCache.SafePath(root, relative))) throw new InvalidDataException($"The official MelonLoader archive is missing {relative}.");
    }

    internal static bool IsGenerationScratch(string relative) =>
        relative.Contains("/ModLaunchTemp/", StringComparison.OrdinalIgnoreCase) ||
        relative.Contains("/Cpp2IL_Temp/", StringComparison.OrdinalIgnoreCase) ||
        relative.Contains("/cpp2il_out/", StringComparison.OrdinalIgnoreCase) ||
        relative.Contains("/Il2CppInterop/Il2CppAssemblies/", StringComparison.OrdinalIgnoreCase);

    private static void DeleteWorkDirectory(string cacheDirectory, string work)
    {
        string verified = ArtifactCache.SafePath(cacheDirectory, Relative(cacheDirectory, work));
        if (!Path.GetDirectoryName(verified)!.Equals(ArtifactCache.SafePath(cacheDirectory, "work"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Refusing cleanup outside the preparation work directory.");
        foreach (string entry in Directory.EnumerateFileSystemEntries(verified, "*", SearchOption.AllDirectories))
            _ = ArtifactCache.SafePath(verified, Relative(verified, entry));
        Directory.Delete(verified, recursive: true);
    }

    private static void CopyVerified(string source, string destination, string expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
        File.SetAttributes(destination, FileAttributes.Normal);
        if (!ArtifactCache.Matches(destination, expected)) throw new IOException($"File changed while being staged: {source}");
    }

    private static string HashTree(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(x => Path.GetFileName(x) is not ".extracted.json" and not ".bundle.json")
            .Select(x => new PreparedFile(Relative(root, x), ArtifactCache.Hash(ArtifactCache.SafePath(root, Relative(root, x)))))
            .OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
        return ComputeHash(JsonSerializer.Serialize(files, JsonDefaults.Options));
    }

    private static void CheckFreeSpace(string cacheDirectory, string gameDirectory, List<FileIdentity> inputs)
    {
        var total = inputs.Where(x => x.Sha256 is not null).Sum(x => new FileInfo(ArtifactCache.SafePath(gameDirectory, x.RelativePath)).Length);
        var required = checked(total * 4 + 768L * 1024 * 1024);
        var drive = new DriveInfo(Path.GetPathRoot(cacheDirectory)!);
        if (drive.IsReady && drive.AvailableFreeSpace < required)
            throw new IOException($"Insufficient free space for safe preparation and backups; at least {required / (1024 * 1024)} MiB is needed.");
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string ComputeHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsUnavailable(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static void ValidateRepository(string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(x => x.Length == 0 || x is "." or ".." || x.Any(c => c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '-' and not '_' and not '.')))
            throw new ArgumentException("AdapterRepository must be a GitHub owner/repository name.", nameof(repository));
    }

    private sealed record PatchResources(string Revision, string Path, string Sha256);
    private sealed record PreparationManifest(string InputKey, string AdapterRepository, string MelonLoaderVersion, PreparedSession Session);
}
