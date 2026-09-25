using System.Text;
using System.Text.Json;
using Tomlet;
using Tomlet.Models;

namespace SCPSL.ModLaunch.Core;

public sealed class RuntimeOwnership
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> OwnedPaths { get; set; } = [];
    public string Fingerprint { get; set; } = "";
    public List<PreparedFile> Files { get; set; } = [];
}

public static class RuntimeInstallation
{
    public const string GeneratorConfig = "MelonLoader/Dependencies/Il2CppAssemblyGenerator/Config.cfg";
    private static readonly string[] ProxyNames = ["version.dll", "winhttp.dll", "winmm.dll", "dinput8.dll", "dsound.dll", "dxgi.dll", "d3d11.dll", "opengl32.dll", "xinput1_3.dll", "xinput9_1_0.dll", "doorstop_config.ini"];

    public static void Preflight(string root)
    {
        foreach (string proxy in ProxyNames)
            if (File.Exists(FileOps.SafePath(root, proxy))) throw new InvalidDataException($"An existing loader/hook conflicts with SCPSL-ModLaunch: {proxy}. Begin with a clean Steam installation.");
        if (Directory.Exists(FileOps.SafePath(root, "BepInEx"))) throw new InvalidDataException("An existing BepInEx installation conflicts with SCPSL-ModLaunch.");
        string marker = FileOps.SafePath(root, ".modlaunch/runtime-owner.json");
        if (Directory.Exists(FileOps.SafePath(root, "MelonLoader")) && !File.Exists(marker))
            throw new InvalidDataException("An existing MelonLoader installation is not owned by SCPSL-ModLaunch. Remove its runtime before using this tool; preserve your user folders.");
    }

    public static void InstallPersistentFiles(string root, PreparedSession prepared, Action<string>? log = null)
    {
        if (!ValidateInstalled(root, prepared.RuntimeFiles, prepared.Fingerprint))
            throw new InvalidDataException("The installed MelonLoader runtime changed after preparation. Restart ModLaunch to repair it.");
        log?.Invoke("Using the installed MelonLoader runtime and generated assemblies.");
        foreach (string folder in new[] { "Mods", "Plugins", "UserData", "UserLibs" }) Directory.CreateDirectory(FileOps.SafePath(root, folder));
    }

    public static void InstallFromStage(string root, string stage, IReadOnlyList<PreparedFile> files, string fingerprint, Action<string>? log = null)
    {
        string marker = FileOps.SafePath(root, ".modlaunch/runtime-owner.json");
        RuntimeOwnership ownership = ReadOwnership(root);
        var previous = ownership.Files.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var owned = ownership.OwnedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incoming = new Dictionary<string, PreparedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string target = RuntimePath(root, file.RelativePath);
            if (!incoming.TryAdd(file.RelativePath, file) || !IsHash(file.Sha256)) throw new InvalidDataException("Invalid or duplicate runtime output.");
            if (File.Exists(target) && !owned.Contains(file.RelativePath)) throw new InvalidDataException($"A runtime output conflicts with an unowned file: {file.RelativePath}");
            string source = FileOps.SafePath(stage, file.SourcePath ?? file.RelativePath);
            if (!FileOps.HashEquals(FileOps.Hash(source), file.Sha256)) throw new InvalidDataException($"Corrupted prepared runtime: {file.RelativePath}");
        }
        bool sameFingerprint = ownership.Fingerprint == fingerprint;
        // Claim paths before writes, retain old retirement identities, and mark complete only
        // after installation. A partial installation remains dormant and can be repaired.
        var merged = new Dictionary<string, PreparedFile>(previous, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) merged[file.RelativePath] = file with { SourcePath = null };
        ownership.SchemaVersion = 2;
        ownership.Fingerprint = "";
        ownership.Files = merged.Values.ToList();
        ownership.OwnedPaths = owned.Union(incoming.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        FileOps.WriteJson(marker, ownership);
        log?.Invoke($"Installing {files.Count} persistent MelonLoader runtime/generated files...");
        foreach (var file in files)
        {
            string target = RuntimePath(root, file.RelativePath);
            string source = FileOps.SafePath(stage, file.SourcePath ?? file.RelativePath);
            if (IsMutable(file.RelativePath) && File.Exists(target))
            {
                if (!sameFingerprint) MergeGenerationConfig(source, target);
                continue;
            }
            if (File.Exists(target) && FileOps.HashEquals(FileOps.Hash(target), file.Sha256)) continue;
            FileOps.CopyVerified(source, target, file.Sha256);
        }
        foreach (string stale in owned.Except(incoming.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            string target = RuntimePath(root, stale);
            if (IsMutable(stale)) continue;
            if (File.Exists(target))
            {
                if (!previous.TryGetValue(stale, out var old) || !IsHash(old.Sha256) || !FileOps.HashEquals(FileOps.Hash(target), old.Sha256))
                {
                    log?.Invoke($"Preserving changed or unverified retired runtime file: {stale}");
                    continue;
                }
                File.Delete(target);
            }
            ownership.OwnedPaths.RemoveAll(x => x.Equals(stale, StringComparison.OrdinalIgnoreCase));
            ownership.Files.RemoveAll(x => x.RelativePath.Equals(stale, StringComparison.OrdinalIgnoreCase));
        }
        ownership.Fingerprint = fingerprint;
        FileOps.WriteJson(marker, ownership);
        foreach (string folder in new[] { "Mods", "Plugins", "UserData", "UserLibs" }) Directory.CreateDirectory(FileOps.SafePath(root, folder));
    }

    public static bool ValidateInstalled(string root, IReadOnlyList<PreparedFile> files, string fingerprint)
    {
        if (files.Count == 0 || string.IsNullOrWhiteSpace(fingerprint)) return false;
        RuntimeOwnership owner = ReadOwnership(root);
        if (owner.SchemaVersion != 2 || owner.Fingerprint != fingerprint) return false;
        var owned = owner.OwnedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string path = RuntimePath(root, file.RelativePath);
            if (!seen.Add(file.RelativePath) || !owned.Contains(file.RelativePath) || !IsHash(file.Sha256) || !File.Exists(path)) return false;
            if (!IsMutable(file.RelativePath) && !FileOps.HashEquals(FileOps.Hash(path), file.Sha256)) return false;
        }
        foreach (string required in new[] { "MelonLoader/net6/MelonLoader.dll", "MelonLoader/net6/Il2CppInterop.Runtime.dll", "MelonLoader/net6/MelonLoader.runtimeconfig.json", "MelonLoader/net6/MelonLoader.deps.json" })
            if (!seen.Contains(required)) return false;
        return true;
    }

    public static bool IsMutable(string path) => path.Equals(GeneratorConfig, StringComparison.OrdinalIgnoreCase);

    /// <summary>Records only the outputs explicitly listed by a completed owned generator run.</summary>
    public static bool CaptureGeneratedFiles(string root, Action<string>? log = null)
    {
        try
        {
            if (File.Exists(FileOps.SafePath(root, ".modlaunch/session.json")) || File.Exists(FileOps.SafePath(root, "version.dll")))
                throw new InvalidOperationException("Generated-file ownership can only be captured after successful restoration.");
            string marker = FileOps.SafePath(root, ".modlaunch/runtime-owner.json");
            if (!File.Exists(marker)) return false;
            RuntimeOwnership owner = ReadOwnership(root);
            if (owner.SchemaVersion != 2 || string.IsNullOrWhiteSpace(owner.Fingerprint)) return false;
            var owned = owner.OwnedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var identities = owner.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
            // A marker alone cannot authorize adoption from an unrelated or incomplete loader.
            foreach (string required in new[] { "MelonLoader/net6/MelonLoader.dll", "MelonLoader/net6/Il2CppInterop.Runtime.dll", "MelonLoader/net6/MelonLoader.runtimeconfig.json", "MelonLoader/net6/MelonLoader.deps.json" })
            {
                if (!owned.Contains(required) || !identities.TryGetValue(required, out var identity)) return false;
                string path = RuntimePath(root, required);
                if (!File.Exists(path) || !FileOps.HashEquals(FileOps.Hash(path), identity.Sha256)) return false;
            }
            string configPath = RuntimePath(root, GeneratorConfig);
            if (!File.Exists(configPath)) return false;
            var config = TomlParser.ParseFile(configPath);
            if (!config.TryGetValue("Il2CppAssemblyGenerator", out var section) || section is not TomlTable generator ||
                !generator.TryGetValue("OldFiles", out var value) || value is not TomlArray oldFiles || oldFiles.Count is 0 or > 2000) return false;
            var outputs = new Dictionary<string, PreparedFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in oldFiles.ArrayValues)
            {
                if (item is not TomlString name || string.IsNullOrWhiteSpace(name.Value) || name.Value.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                    throw new InvalidDataException("Generator OldFiles must contain only simple filenames.");
                string relative = "MelonLoader/Il2CppAssemblies/" + name.Value;
                string path = RuntimePath(root, relative);
                if (!File.Exists(path)) throw new InvalidDataException($"Generator output is missing: {name.Value}.");
                if (!outputs.TryAdd(relative, new PreparedFile(relative, FileOps.Hash(path))))
                    throw new InvalidDataException("Generator OldFiles contains duplicate filenames.");
            }
            // Do not refresh known immutable hashes: a file changed outside the generator must
            // retain its prior identity so later retirement cannot erase somebody else's edits.
            foreach (var output in outputs.Values)
                if (!owned.Contains(output.RelativePath))
                {
                    owned.Add(output.RelativePath);
                    identities[output.RelativePath] = output;
                }
            if (owned.Add(GeneratorConfig)) identities[GeneratorConfig] = new PreparedFile(GeneratorConfig, FileOps.Hash(configPath));
            owner.OwnedPaths = owned.OrderBy(path => path, StringComparer.Ordinal).ToList();
            owner.Files = identities.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
            FileOps.WriteJson(marker, owner);
            log?.Invoke($"Recorded ownership of {outputs.Count} generated assembly files.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException or Tomlet.Exceptions.TomlException)
        {
            log?.Invoke("Generated-file ownership was left unchanged: " + ex.Message);
            return false;
        }
    }

    private static void MergeGenerationConfig(string source, string target)
    {
        var prepared = TomlParser.ParseFile(source);
        var existing = TomlParser.ParseFile(target);
        const string section = "Il2CppAssemblyGenerator";
        if (!prepared.ContainsKey(section)) return;
        if (!existing.ContainsKey(section)) existing.PutValue(section, prepared.GetSubTable(section));
        else
        {
            var from = prepared.GetSubTable(section);
            var to = existing.GetSubTable(section);
            foreach (string key in new[] { "GameAssemblyHash", "UnityVersion", "DumperVersion", "DumperSCRSVersion", "OldFiles" })
                if (from.TryGetValue(key, out var value)) to.PutValue(key, value);
        }
        FileOps.WriteBytes(target, Encoding.UTF8.GetBytes(existing.SerializedValue));
    }

    private static RuntimeOwnership ReadOwnership(string root)
    {
        string marker = FileOps.SafePath(root, ".modlaunch/runtime-owner.json");
        var owner = File.Exists(marker) ? FileOps.ReadJson<RuntimeOwnership>(marker) : new RuntimeOwnership();
        if (owner.SchemaVersion is not 1 and not 2 || owner.OwnedPaths is null || owner.Files is null)
            throw new InvalidDataException("Unknown or incomplete runtime ownership format.");
        var ownedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in owner.OwnedPaths)
        {
            _ = RuntimePath(root, path);
            if (!ownedPaths.Add(path)) throw new InvalidDataException("Duplicate runtime ownership path.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in owner.Files)
        {
            if (file is null) throw new InvalidDataException("Incomplete runtime ownership identity.");
            _ = RuntimePath(root, file.RelativePath);
            if (!paths.Add(file.RelativePath) || !IsHash(file.Sha256)) throw new InvalidDataException("Invalid runtime ownership identity.");
        }
        if (owner.SchemaVersion == 1) owner.Files = FindLegacyHashes(root, owner.OwnedPaths);
        return owner;
    }

    private static List<PreparedFile> FindLegacyHashes(string root, List<string> owned)
    {
        var found = new Dictionary<string, PreparedFile>(StringComparer.OrdinalIgnoreCase);
        string cache = FileOps.SafePath(root, ".modlaunch/prepared");
        if (!Directory.Exists(cache)) return [];
        foreach (string directory in Directory.EnumerateDirectories(cache))
        {
            string manifest = FileOps.SafePath(cache, Path.GetFileName(directory) + "/prepared.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                var session = document.RootElement.GetProperty("session").Deserialize<PreparedSession>(JsonDefaults.Options);
                foreach (var file in session?.Files ?? [])
                    if (owned.Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase) && IsHash(file.Sha256))
                    {
                        string target = RuntimePath(root, file.RelativePath);
                        if (File.Exists(target) && FileOps.HashEquals(FileOps.Hash(target), file.Sha256)) found[file.RelativePath] = file with { SourcePath = null };
                    }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidDataException) { }
        }
        return found.Values.ToList();
    }

    private static string RuntimePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || !relative.StartsWith("MelonLoader/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid runtime ownership path.");
        return FileOps.SafePath(root, relative);
    }

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}
