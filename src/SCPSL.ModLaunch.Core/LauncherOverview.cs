using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using SCPSL.ModLaunch.Core.Provisioning;

namespace SCPSL.ModLaunch.Core;

public enum LauncherNextAction { PatchGame, Repair, Ready }

public sealed record LauncherOverview(string GameDirectory, string? GameVersion, string? UnityVersion,
    string? MelonLoaderVersion, string ModPatchStatus, LauncherNextAction NextAction,
    bool HasSession, bool IsActive, bool SkipMelonLoader, IReadOnlyList<string> Details)
{
    public string NextActionText => NextAction switch
    {
        LauncherNextAction.PatchGame => "Patch game",
        LauncherNextAction.Repair => "Repair",
        _ => "Ready"
    };
}

/// <summary>Reads existing preparation/session evidence without preparing, patching, or reorganizing files.</summary>
public static class LauncherInspection
{
    public static LauncherOverview Inspect(string gameDirectory) => InspectCore(gameDirectory, null);

    internal static LauncherOverview Inspect(string gameDirectory, string expectedMelonHash) => InspectCore(gameDirectory, expectedMelonHash);

    private static LauncherOverview InspectCore(string gameDirectory, string? expectedMelonHash)
    {
        string root = Path.GetFullPath(gameDirectory);
        string? melonVersion = null;
        bool hasSession = false, active = false;
        var details = new List<string>();
        try
        {
            FileOps.RejectLink(root);
            melonVersion = ReadMelonVersion(root);
            string cache = FileOps.SafePath(root, ".modlaunch");
            string journalPath = FileOps.SafePath(cache, "session.json");
            hasSession = File.Exists(journalPath);
            SessionJournal? journal = null;
            if (hasSession)
            {
                var transaction = new SessionTransaction(root);
                journal = transaction.LoadPending();
                try { transaction.VerifyActive(journal!); active = true; }
                catch (Exception error) when (IsInspectionFailure(error))
                { details.Add("The saved session requires restoration: " + error.Message); }
            }
            string preparedDirectory = FileOps.SafePath(cache, CacheLayout.ModdedFiles);
            string manifestPath = FileOps.SafePath(preparedDirectory, CacheLayout.PreparationManifest);
            if (!File.Exists(manifestPath))
            {
                if (hasSession || Directory.Exists(preparedDirectory))
                    return Result("Preparation record unavailable", LauncherNextAction.Repair);
                // No saved identities exist yet: only check presence here. Preparation hashes inputs.
                PreparationService.CheckInputFiles(root);
                RuntimeInstallation.Preflight(root);
                details.Add("The game version will be detected by ModPatch during preparation.");
                details.Add("Patch state from other tools is not verified without a matching preparation record.");
                return Result("Not prepared", LauncherNextAction.PatchGame);
            }
            var manifest = FileOps.ReadJson<SavedPreparation>(manifestPath);
            var session = manifest.Session ?? throw new InvalidDataException("The saved preparation is incomplete.");
            if (session.PatchEngine != ModPatchBundle.Repository || string.IsNullOrWhiteSpace(manifest.AdapterRepository) ||
                string.IsNullOrWhiteSpace(manifest.MelonLoaderVersion) ||
                !(expectedMelonHash is null
                    ? PreparationService.ValidateSession(session, preparedDirectory, cache, manifest.InputKey)
                    : PreparationService.ValidateSession(session, preparedDirectory, cache, manifest.InputKey, expectedMelonHash)))
                throw new InvalidDataException("The saved preparation or installed runtime no longer matches its verified files.");
            if (journal != null)
            {
                if (!active || journal.Fingerprint != session.Fingerprint)
                    return Result("Recovery required", LauncherNextAction.Repair);
                var originals = journal.Entries.Where(entry => !entry.RelativePath.Equals("version.dll", StringComparison.OrdinalIgnoreCase) &&
                        !entry.RelativePath.Equals(GameFileContract.BootConfigPath, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(entry => entry.RelativePath, entry => entry.OriginalHash, StringComparer.OrdinalIgnoreCase);
                var inputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (session.Inputs.Count != originals.Count || session.Inputs.Any(input =>
                        !inputPaths.Add(input.RelativePath) || !originals.TryGetValue(input.RelativePath, out string? hash) || !FileOps.HashEquals(hash, input.Sha256)))
                    throw new InvalidDataException("The preparation inputs disagree with the active session's original backups.");
            }
            else
            {
                var current = PreparationService.CaptureInputs(root, null);
                string actualInputKey = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(current, JsonDefaults.Options)));
                if (actualInputKey != manifest.InputKey)
                    throw new InvalidDataException("The game files changed after preparation. Preparation must be checked again.");
                RuntimeInstallation.Preflight(root, session.SkipMelonLoader);
            }
            string settingsPath = FileOps.SafePath(cache, "settings.json");
            var settings = File.Exists(settingsPath) ? FileOps.ReadJson<ModLaunchSettings>(settingsPath) : new ModLaunchSettings();
            if (settings.AdapterRepository != manifest.AdapterRepository ||
                (!session.SkipMelonLoader && settings.MelonLoaderVersion != manifest.MelonLoaderVersion))
                throw new InvalidDataException("Launcher settings changed after this preparation was built.");
            if (session.SkipMelonLoader)
            {
                details.Add("Existing preparation skips MelonLoader; normal launch requires preparation.");
                return Result(active ? "Active without MelonLoader" : "Prepared without MelonLoader",
                    LauncherNextAction.PatchGame, session);
            }
            details.Add(active ? "Modded files are active and the original backups are verified." :
                "Original game files are active; verified ModPatch outputs are ready to apply.");
            return Result(active ? "Active" : "Prepared", LauncherNextAction.Ready, session);
        }
        catch (Exception error) when (IsInspectionFailure(error))
        {
            details.Add(error.Message);
            return Result(hasSession ? "Recovery required" : "Preparation needs attention", LauncherNextAction.Repair);
        }

        LauncherOverview Result(string status, LauncherNextAction next, PreparedSession? session = null) =>
            new(root, session?.GameVersion, session?.UnityVersion, melonVersion, status, next,
                hasSession, active, session?.SkipMelonLoader ?? false, details.ToArray());
    }

    private static string? ReadMelonVersion(string root)
    {
        string path = FileOps.SafePath(root, "MelonLoader/net6/MelonLoader.dll");
        if (!File.Exists(path)) return null;
        // FileVersionInfo invokes native version.dll. An active MelonLoader proxy can
        // occupy that name beside the launcher, so read managed metadata directly.
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return "Unknown";
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) return "Unknown";
            Version version = metadata.GetAssemblyDefinition().Version;
            return version.Revision == 0 ? version.ToString(3) : version.ToString();
        }
        catch (BadImageFormatException) { return "Unknown"; }
    }

    private static bool IsInspectionFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or InvalidOperationException or ArgumentException or JsonException or System.ComponentModel.Win32Exception;

    private sealed record SavedPreparation(string InputKey, string AdapterRepository, string MelonLoaderVersion, PreparedSession? Session);
}
