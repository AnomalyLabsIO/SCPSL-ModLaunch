using System.Text.Json;

namespace SCPSL.ModLaunch.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record FileIdentity(string RelativePath, string? Sha256);
public sealed record PreparedFile(string RelativePath, string Sha256, string? SourcePath = null);
public sealed record PatchAttempt(string Method, string Status, string? Detail = null);

public sealed class PatchResult
{
    public int SchemaVersion { get; set; } = 1;
    public string AdapterVersion { get; set; } = "0.1.0";
    public string GameVersion { get; set; } = "";
    public string UnityVersion { get; set; } = "";
    public string PatchRevision { get; set; } = "";
    public List<PatchAttempt> Patches { get; set; } = [];
    public List<PreparedFile> Files { get; set; } = [];
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
    public string MelonLoaderArchive { get; set; } = "";
    public string BootstrapHash { get; set; } = "";
    public List<FileIdentity> Inputs { get; set; } = [];
    public List<PreparedFile> Files { get; set; } = [];
    public List<PreparedFile> RuntimeFiles { get; set; } = [];
}

public sealed class ModLaunchSettings
{
    public string MelonLoaderVersion { get; set; } = "0.7.3";
    public string AdapterRepository { get; set; } = "AnomalyLabsIO/SCPSL-ModLaunch";
}
