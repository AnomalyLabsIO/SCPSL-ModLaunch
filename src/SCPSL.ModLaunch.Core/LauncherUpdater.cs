using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SCPSL.ModLaunch.Core.Provisioning;

namespace SCPSL.ModLaunch.Core;

/// <summary>Stages release assets independently of game preparation, then atomically replaces the launcher.</summary>
public sealed class LauncherUpdater : IDisposable
{
    public const string Repository = "AnomalyLabsIO/SCPSL-ModLaunch";
    private const string AssetName = LauncherInstallation.ExecutableName;
    private const long MaximumExecutableBytes = 64L * 1024 * 1024;
    private readonly string root, processPath, processHash, updates;
    private readonly Version currentVersion;
    private readonly HttpClient http;
    private readonly bool ownsClient;
    private readonly Action<string> log;
    private readonly Action<string, Version> validateExecutable;
    private readonly Action<string>? checkpoint;
    private readonly TimeSpan timeout;
    private string StatePath => FileOps.SafePath(updates, "pending.json");
    public string? AppliedExecutablePath { get; private set; }

    public LauncherUpdater(string gameDirectory, string executablePath, Version version, Action<string> log)
        : this(gameDirectory, executablePath, version, CreateClient(), log, ownsClient: true) { }

    internal LauncherUpdater(string gameDirectory, string executablePath, Version version, HttpClient http,
        Action<string> log, Action<string, Version>? validateExecutable = null,
        Action<string>? checkpoint = null, TimeSpan? timeout = null, bool ownsClient = false)
    {
        root = Path.GetFullPath(gameDirectory);
        processPath = Path.GetFullPath(executablePath);
        if (!string.Equals(processPath, FileOps.SafePath(root, AssetName), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(processPath, FileOps.SafePath(root, ".modlaunch/" + AssetName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected launcher update target.");
        FileOps.RejectLink(processPath);
        processHash = FileOps.Hash(processPath);
        currentVersion = Normalize(version);
        updates = FileOps.SafePath(root, ".modlaunch/updates");
        this.http = http;
        this.log = log;
        this.validateExecutable = validateExecutable ?? ValidateExecutable;
        this.checkpoint = checkpoint;
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        this.ownsClient = ownsClient;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SCPSL-ModLaunch-updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // This task catches its own failures and is not cancelled by failed game preparation.
    public async Task CheckAsync()
    {
        using var budget = new CancellationTokenSource(timeout);
        try
        {
            var pending = ReadPending();
            if (pending?.Target is not null)
            {
                log("A previous launcher update still needs reconciliation; keeping its recovery files.");
                return;
            }
            log("Checking for a newer SCPSL-ModLaunch release...");
            var cache = new ArtifactCache(http, log);
            string json;
            try { json = await cache.GetTextAsync($"https://api.github.com/repos/{Repository}/releases/latest", budget.Token); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                log("No launcher release is published yet; continuing with this version.");
                return;
            }
            using var release = JsonDocument.Parse(json);
            var document = release.RootElement;
            if (document.GetProperty("draft").GetBoolean() || document.GetProperty("prerelease").GetBoolean()) return;
            string tag = document.GetProperty("tag_name").GetString() ?? "";
            if (!TryReleaseVersion(tag, out var version))
            {
                log("The latest launcher release has an unsupported version tag; skipping it.");
                return;
            }
            if (version <= currentVersion)
            {
                log("The launcher is already current; no newer stable release is available.");
                return;
            }
            if (pending is not null && version <= ParseVersion(pending.Version)) return;
            var assets = document.GetProperty("assets").EnumerateArray().ToArray();
            var matches = assets.Where(asset => asset.GetProperty("name").GetString() == AssetName).ToArray();
            if (matches.Length != 1)
            {
                log($"The latest release does not contain exactly one {AssetName}; continuing with this version.");
                return;
            }
            var asset = matches[0];
            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumExecutableBytes) throw new InvalidDataException("Launcher release asset size is invalid.");
            string url = AssetUrl(asset, tag, AssetName);
            string? digest = null;
            if (asset.TryGetProperty("digest", out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { } text && text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = text[7..];
            if (!ArtifactCache.IsHash(digest))
            {
                var checksums = assets.Where(a => a.GetProperty("name").GetString() == AssetName + ".sha256").ToArray();
                if (checksums.Length != 1) throw new InvalidDataException("Launcher release has no SHA-256 digest or checksum file.");
                string checksum = await cache.GetTextAsync(AssetUrl(checksums[0], tag, AssetName + ".sha256"), budget.Token);
                var fields = checksum.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length is < 1 or > 2 || !ArtifactCache.IsHash(fields[0]) ||
                    (fields.Length == 2 && fields[1].TrimStart('*') != AssetName))
                    throw new InvalidDataException("Invalid launcher checksum file.");
                digest = fields[0];
            }
            string payload = Payload(digest!);
            await cache.DownloadAsync(url, payload, digest!, false, budget.Token, MaximumExecutableBytes);
            if (new FileInfo(payload).Length != size) throw new InvalidDataException("Launcher release asset length does not match its manifest.");
            validateExecutable(payload, version);
            FileOps.WriteJson(StatePath, new PendingLauncherUpdate { Version = version.ToString(), Sha256 = digest! });
            log($"SCPSL-ModLaunch {DisplayVersion(version)} is downloaded and will be installed for the next launch.");
        }
        catch (Exception ex) when (Recoverable(ex)) { log("Launcher update check skipped: " + ex.Message); }
    }

    // Called with the game-directory lock held, at startup and again during final cleanup.
    // File.Replace keeps a runnable target present, even if this process stops during publication.
    public bool TryApplyPending()
    {
        try
        {
            var pending = ReadPending();
            if (pending is null) { CleanRetiredFiles(); return false; }
            Version version = ParseVersion(pending.Version);
            if (pending.Target is not null)
            {
                string target = Target(pending.Target);
                // First-run cleanup may relocate the old EXE after a deferred update.
                if (!File.Exists(target) && pending.Target == AssetName)
                {
                    string installed = FileOps.SafePath(root, ".modlaunch/" + AssetName);
                    if (Matches(installed, pending.PreviousSha256!) && Matches(installed, processHash))
                    {
                        pending.Target = ".modlaunch/" + AssetName;
                        target = installed;
                    }
                }
                if (Matches(target, pending.Sha256))
                {
                    LauncherInstallation.RecordExecutableUpdate(root, target, pending.PreviousSha256!, pending.Sha256, completed: true);
                    File.Delete(StatePath);
                    log($"Completed the interrupted launcher update to {DisplayVersion(version)}.");
                    CleanRetiredFiles();
                    return false;
                }
                if (!Matches(target, pending.PreviousSha256!))
                    throw new IOException("The launcher changed during an interrupted update; retained its backup and pending update.");
            }
            if (version <= currentVersion)
            {
                File.Delete(StatePath);
                CleanRetiredFiles();
                return false;
            }
            string replacement = Payload(pending.Sha256);
            if (!Matches(replacement, pending.Sha256)) throw new InvalidDataException("The staged launcher update is missing or changed.");
            validateExecutable(replacement, version);
            string actualTarget = File.Exists(processPath) ? processPath : FileOps.SafePath(root, ".modlaunch/" + AssetName);
            if (!Matches(actualTarget, processHash)) throw new IOException("The running launcher's file changed; the update was not installed.");
            if (pending.Target is not null && !string.Equals(actualTarget, Target(pending.Target), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The interrupted update belongs to a different launcher location.");
            string backup = FileOps.SafePath(updates, "previous-" + processHash + ".bin");
            if (File.Exists(backup))
            {
                if (!Matches(backup, processHash)) throw new IOException("The launcher update backup changed unexpectedly.");
                File.Delete(backup);
            }
            pending.Target = string.Equals(actualTarget, FileOps.SafePath(root, AssetName), StringComparison.OrdinalIgnoreCase)
                ? AssetName : ".modlaunch/" + AssetName;
            pending.PreviousSha256 = processHash;
            FileOps.WriteJson(StatePath, pending);
            checkpoint?.Invoke("update-journal-written");
            LauncherInstallation.RecordExecutableUpdate(root, actualTarget, processHash, pending.Sha256, completed: false);
            checkpoint?.Invoke("update-ownership-prepared");
            if (!Matches(actualTarget, processHash)) throw new IOException("The launcher changed before update replacement.");
            File.Replace(replacement, actualTarget, backup);
            checkpoint?.Invoke("update-executable-replaced");
            if (!Matches(actualTarget, pending.Sha256) || !Matches(backup, processHash))
                throw new IOException("Launcher update replacement could not be verified.");
            LauncherInstallation.RecordExecutableUpdate(root, actualTarget, processHash, pending.Sha256, completed: true);
            checkpoint?.Invoke("update-ownership-completed");
            File.Delete(StatePath);
            AppliedExecutablePath = actualTarget;
            log($"Installed SCPSL-ModLaunch {DisplayVersion(version)} for the next launch.");
            return true;
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            log("Launcher update deferred; the current launcher and recovery files were retained: " + ex.Message);
            return false;
        }
    }

    private PendingLauncherUpdate? ReadPending()
    {
        if (!File.Exists(StatePath)) return null;
        if (new FileInfo(StatePath).Length > 16384) throw new InvalidDataException("Launcher update state is too large.");
        var state = FileOps.ReadJson<PendingLauncherUpdate>(StatePath);
        if (state.SchemaVersion != 1 || !ArtifactCache.IsHash(state.Sha256) || !TryReleaseVersion(state.Version, out _) ||
            ((state.Target is null) != (state.PreviousSha256 is null)) ||
            (state.PreviousSha256 is not null && !ArtifactCache.IsHash(state.PreviousSha256)))
            throw new InvalidDataException("Invalid launcher update state.");
        if (state.Target is not null) _ = Target(state.Target);
        return state;
    }

    private string Target(string relative)
    {
        if (relative != AssetName && relative != ".modlaunch/" + AssetName)
            throw new InvalidDataException("Unexpected launcher update target.");
        return FileOps.SafePath(root, relative);
    }

    private string Payload(string digest) => FileOps.SafePath(updates, "launcher-" + digest.ToUpperInvariant() + ".bin");
    private static bool Matches(string path, string hash) { FileOps.RejectLink(path); return File.Exists(path) && FileOps.HashEquals(FileOps.Hash(path), hash); }

    private void CleanRetiredFiles()
    {
        if (!Directory.Exists(updates)) return;
        foreach (string path in Directory.EnumerateFiles(updates, "*.bin"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string? hash = name.StartsWith("previous-", StringComparison.Ordinal) ? name[9..] : name.StartsWith("launcher-", StringComparison.Ordinal) ? name[9..] : null;
            if (!ArtifactCache.IsHash(hash)) continue;
            try { if (Matches(FileOps.SafePath(updates, Path.GetFileName(path)), hash!)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* An older process may still have its backup open. */ }
        }
    }

    private static string AssetUrl(JsonElement asset, string tag, string name)
    {
        string url = asset.GetProperty("browser_download_url").GetString() ?? "";
        var expected = new Uri($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var actual) || actual != expected)
            throw new InvalidDataException("Launcher asset points outside the expected GitHub release.");
        return url;
    }

    internal static bool TryReleaseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrEmpty(text)) return false;
        string value = text.StartsWith('v') ? text[1..] : text;
        var fields = value.Split('.');
        if (fields.Length is < 3 or > 4 || fields.Any(f => f.Length == 0 || f.Any(c => c is < '0' or > '9')) ||
            !Version.TryParse(value, out var parsed) || fields.Any(f => int.Parse(f) > ushort.MaxValue)) return false;
        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    private static string DisplayVersion(Version value) => value.ToString(value.Revision > 0 ? 4 : 3);
    private static Version ParseVersion(string value) => TryReleaseVersion(value, out var version) ? version : throw new InvalidDataException("Invalid launcher version.");

    internal static void ValidateExecutable(string path, Version expectedVersion)
    {
        using (var input = File.OpenRead(path))
        using (var reader = new BinaryReader(input))
        {
            if (input.Length < 256 || input.Length > MaximumExecutableBytes || reader.ReadUInt16() != 0x5a4d)
                throw new InvalidDataException("Launcher update is not a Windows executable.");
            input.Position = 0x3c;
            int offset = reader.ReadInt32();
            if (offset < 64 || offset > input.Length - 26) throw new InvalidDataException("Invalid launcher PE header.");
            input.Position = offset;
            if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
                throw new InvalidDataException("Launcher update is not a Windows x64 executable.");
            input.Position = offset + 22;
            if ((reader.ReadUInt16() & 0x2000) != 0 || reader.ReadUInt16() != 0x20b)
                throw new InvalidDataException("Launcher update is not an x64 application.");
        }
        var info = FileVersionInfo.GetVersionInfo(path);
        var fileVersion = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        if (info.ProductName != "SCPSL-ModLaunch" || fileVersion != Normalize(expectedVersion))
            throw new InvalidDataException("Launcher executable identity/version does not match its release tag.");
    }

    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or
        OperationCanceledException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or
        NotSupportedException or FormatException or OverflowException or System.ComponentModel.Win32Exception;

    public void Dispose() { if (ownsClient) http.Dispose(); }
}

internal sealed class PendingLauncherUpdate
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? Target { get; set; }
    public string? PreviousSha256 { get; set; }
}
