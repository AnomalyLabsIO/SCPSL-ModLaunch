using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SCPSL.ModLaunch.Core;

/// <summary>Identity and inputs of the separately built MIT ModPatch CLI.</summary>
public sealed class ModPatchBundle
{
    public const string Repository = "AnomalyLabsIO/SCPSL-ModPatch";
    public const string Executable = "SCPSL.ModPatch.Cli.exe";
    public const string Manifest = "patchinfo.json";
    public int SchemaVersion { get; set; }
    public string SourceRepository { get; set; } = "";
    public string SourceRevision { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public List<PreparedFile> Files { get; set; } = [];

    public static ModPatchBundle Read(string directory)
    {
        var bundle = FileOps.ReadJson<ModPatchBundle>(FileOps.SafePath(directory, "bundle.json"));
        if (bundle.SchemaVersion != 1 || bundle.SourceRepository != Repository ||
            !IsHash(bundle.SourceRevision, 40) || !IsHash(bundle.SourceSha256, 64))
            throw new InvalidDataException("The ModPatch CLI bundle has an unsupported identity.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in bundle.Files)
            if (!paths.Add(file.RelativePath) || !IsHash(file.Sha256, 64) ||
                !File.Exists(FileOps.SafePath(directory, file.RelativePath)) ||
                !FileOps.Hash(FileOps.SafePath(directory, file.RelativePath)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The ModPatch CLI bundle is incomplete or changed: {file.RelativePath}.");
        if (!paths.Contains(Executable) || !paths.Contains(Manifest))
            throw new InvalidDataException("The ModPatch CLI or pattern manifest is missing from the bundle.");
        return bundle;
    }

    public static string LauncherPath(string manifestPath)
    {
        string path = manifestPath.Replace('\\', '/');
        if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (!Regex.IsMatch(path, @"^clean_launchers/[0-9]+\.[0-9]+\.[0-9]+[abfp][0-9]+/SCPSL\.exe$"))
            throw new InvalidDataException("The pattern manifest has an unsupported clean launcher path.");
        return path;
    }

    public static JsonObject PinPatternManifest(string text, string revision)
    {
        if (!TryPinPatternManifest(text, revision, out var document))
            throw new InvalidDataException("Expected the ModPatch pattern manifest, not the legacy offset feed.");
        return document;
    }

    internal static bool TryPinPatternManifest(string text, string revision, out JsonObject document)
    {
        if (!IsHash(revision, 40)) throw new InvalidDataException("ModPatch resources require an immutable source revision.");
        document = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("Missing pattern manifest.");
        if (document["versionRanges"] is not JsonArray ranges || ranges.Count == 0)
            throw new InvalidDataException("The pattern manifest has no patch targets.");
        bool hasPatterns = false;
        foreach (var node in ranges)
        {
            var profile = node?.AsObject() ?? throw new InvalidDataException("Invalid pattern target.");
            string launcher = LauncherPath(profile["cleanLauncherPath"]?.GetValue<string>() ?? "");
            // The fork retains upstream launcher paths in its source manifest. Resolve assets
            // from the same fork revision as the feed; never follow mutable embedded URLs.
            profile["cleanLauncherUrl"] = $"https://raw.githubusercontent.com/{Repository}/{revision}/Shared_Files/{launcher}";
            if (profile["methods"]?["patchMethods"] is not JsonArray patches || patches.Count == 0)
                throw new InvalidDataException("A pattern target has no patches.");
            foreach (var patchNode in patches)
            {
                if (patchNode is not JsonObject patch ||
                    patch["name"] is not JsonValue name || !name.TryGetValue<string>(out var methodName) || string.IsNullOrWhiteSpace(methodName) ||
                    patch["patchData"] is not JsonValue data || !data.TryGetValue<string>(out var patchData) || string.IsNullOrWhiteSpace(patchData))
                    throw new InvalidDataException("A patch target has an invalid patch entry.");
                if (patch["patchPattern"] != null)
                {
                    if (patch["patchPattern"] is not JsonValue pattern || !pattern.TryGetValue<string>(out var bytes) || string.IsNullOrWhiteSpace(bytes))
                        throw new InvalidDataException("A patch target has an invalid byte pattern.");
                    hasPatterns = true;
                }
            }
        }
        return hasPatterns;
    }

    private static bool IsHash(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
}
