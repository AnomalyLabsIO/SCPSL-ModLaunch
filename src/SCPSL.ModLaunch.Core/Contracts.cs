using System.Text.Json;

namespace SCPSL.ModLaunch.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record FileIdentity(string RelativePath, string? Sha256);
public sealed record PreparedFile(string RelativePath, string Sha256, string? SourcePath = null);
public sealed record PatchAttempt(string Method, string Status, string? Detail = null);

public static class GameFileContract
{
    public const string BundleVersionPath = "SCPSL_Data/globalgamemanagers";
    public const string BundleVersionPatchName = "PlayerSettings.bundleVersion";
}

/// <summary>Verified ModPatch output identity; ModLaunch does not parse or edit Unity settings.</summary>
public sealed record BundleVersionCorrection(string Before, string After, string Status, string Sha256)
{
    public bool Matches(string gameVersion, string sha256) =>
        Version.TryParse(Before, out var before) && before.Major >= 10 && before.Build >= 0 && before.Revision < 0 &&
        Version.TryParse(gameVersion, out var after) && after.Major >= 10 && after.Build >= 0 && after.Revision < 0 &&
        After == gameVersion && Status == (Before == After ? "already-correct" : "applied") &&
        Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit) &&
        string.Equals(Sha256, sha256, StringComparison.OrdinalIgnoreCase);
}

public sealed class PatchResult
{
    public int SchemaVersion { get; set; } = 1;
    public string AdapterVersion { get; set; } = "0.3.0";
    public string GameVersion { get; set; } = "";
    public string UnityVersion { get; set; } = "";
    public string PatchRevision { get; set; } = "";
    public List<PatchAttempt> Patches { get; set; } = [];
    public List<PreparedFile> Files { get; set; } = [];
    public BundleVersionCorrection? BundleVersion { get; set; }
    public bool InteropGenerated { get; set; }
}

public sealed class PreparedSession
{
    // Missing on old manifests: they must be prepared again rather than mistaken for layout 2.
    public int LayoutVersion { get; set; }
    public string PreparedDirectory { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public string UnityVersion { get; set; } = "";
    public string DotnetRoot { get; set; } = "";
    public string AdapterDirectory { get; set; } = "";
    public string? PatchEngine { get; set; }
    public string? AdapterFingerprint { get; set; }
    public bool SkipMelonLoader { get; set; }
    public string MelonLoaderArchive { get; set; } = "";
    public string BootstrapHash { get; set; } = "";
    // Optional launch-only native bootstrap selection; the official runtime archive stays separate.
    public string? BootstrapArchive { get; set; }
    public string? BootstrapArchiveSha256 { get; set; }
    public List<FileIdentity> Inputs { get; set; } = [];
    public List<PreparedFile> Files { get; set; } = [];
    public List<PreparedFile> RuntimeFiles { get; set; } = [];
    public BundleVersionCorrection? BundleVersion { get; set; }
}

public sealed class ModLaunchSettings
{
    public string MelonLoaderVersion { get; set; } = "0.7.3";
    public string AdapterRepository { get; set; } = "AnomalyLabsIO/SCPSL-ModLaunch";
}
