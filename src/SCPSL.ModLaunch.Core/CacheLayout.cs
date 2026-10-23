using System.Text.Json;

namespace SCPSL.ModLaunch.Core;

public static class CacheLayout
{
    public const string ModdedFiles = "modded-game-files";
    public const string OriginalFiles = "original-game-files";
    public const string Tools = "patching-tools";
    public const string Temporary = "temporary";
    public const string PreparationManifest = "preparation.json";
    public const string PatchResults = "patch-results.json";

    public static string ModdedFile(string relative) => relative.Replace('\\', '/') + ".modded";

    public static string OriginalFile(string relative, string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid original file hash.");
        return $"{OriginalFiles}/{relative.Replace('\\', '/')}.{hash[..12].ToUpperInvariant()}.original";
    }

    public static string SessionFolder(string id) => $"{Temporary}/session-{id}";
    public static string AdapterFolder(string hash) => $"{Tools}/downloaded-{hash[..12].ToUpperInvariant()}";
    public static string AdapterDownload(string hash) => $"downloads/SCPSL-ModLaunch-patching-tools-{hash[..12].ToUpperInvariant()}.zip";

    public static void Upgrade(string root, Action<string>? log = null)
    {
        string cache = FileOps.SafePath(root, ".modlaunch");
        if (File.Exists(FileOps.SafePath(cache, "session.json")))
            throw new InvalidOperationException("Recover the pending session before reorganizing saved files.");
        UpgradeOriginals(root, cache, log);
        MoveDirectory(cache, "tools/adapter-bundled", $"{Tools}/bundled");
        MoveDirectory(cache, "tools/adapter-bundled.invalid-previous", $"{Tools}/bundled.invalid-previous");
        foreach (string directory in ChildDirectories(cache, "tools"))
        {
            string name = Path.GetFileName(directory);
            string destination = name.StartsWith("adapter-", StringComparison.Ordinal) && name.Length == 72 && name[8..].All(Uri.IsHexDigit)
                ? AdapterFolder(name[8..]) : $"{Tools}/{name}";
            MoveDirectory(cache, "tools/" + name, destination);
        }
        RemoveEmpty(FileOps.SafePath(cache, "tools"));
        foreach (string directory in ChildDirectories(cache, "resources"))
        {
            string name = Path.GetFileName(directory);
            string label = name.StartsWith("bundled-", StringComparison.Ordinal) ? "bundled-" + name[8..][..Math.Min(12, name.Length - 8)] : name[..Math.Min(12, name.Length)];
            MoveDirectory(cache, "resources/" + name, "downloads/patch-definitions/ModPatch-" + label);
        }
        RemoveEmpty(FileOps.SafePath(cache, "resources"));
        foreach (string directory in ChildDirectories(cache, "work"))
            MoveDirectory(cache, "work/" + Path.GetFileName(directory), $"{Temporary}/preparation-{Path.GetFileName(directory)}");
        RemoveEmpty(FileOps.SafePath(cache, "work"));
        foreach (string file in ChildFiles(cache, "downloads", "adapter-*.zip"))
        {
            string hash = Path.GetFileNameWithoutExtension(file)[8..];
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit)) MoveFile(cache, file, FileOps.SafePath(cache, AdapterDownload(hash)));
        }
    }

    private static void UpgradeOriginals(string root, string cache, Action<string>? log)
    {
        string originals = FileOps.SafePath(cache, "originals");
        if (!Directory.Exists(originals)) return;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in new[] { "SCPSL.exe", "GameAssembly.dll", "UnityPlayer.dll", "SL-AC.dll", SessionTransaction.RuntimeMetadataPath, UnityBundleVersion.RelativePath })
        {
            string path = FileOps.SafePath(root, relative);
            if (File.Exists(path)) names.TryAdd(FileOps.Hash(path), relative);
        }
        foreach (string directory in ChildDirectories(cache, "prepared"))
        {
            string manifest = FileOps.SafePath(directory, "prepared.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
                if (!document.RootElement.TryGetProperty("session", out var session) || !session.TryGetProperty("inputs", out var inputs)) continue;
                foreach (var input in inputs.EnumerateArray())
                    if (input.TryGetProperty("sha256", out var hash) && hash.ValueKind == JsonValueKind.String &&
                        input.TryGetProperty("relativePath", out var relative) && relative.ValueKind == JsonValueKind.String)
                        names.TryAdd(hash.GetString()!, relative.GetString()!);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            { log?.Invoke("Could not identify older original file names from a preparation record: " + ex.Message); }
        }
        foreach (string file in ChildFiles(cache, "originals", "*.bin"))
        {
            string hash = FileOps.Hash(file);
            string relative = names.GetValueOrDefault(hash, "unidentified-game-file");
            MoveFile(cache, file, FileOps.SafePath(cache, OriginalFile(relative, hash)));
        }
        RemoveEmpty(originals);
    }

    private static string[] ChildDirectories(string cache, string relative)
    {
        string path = FileOps.SafePath(cache, relative);
        return Directory.Exists(path) ? Directory.GetDirectories(path).Select(item => FileOps.SafePath(cache, Path.GetRelativePath(cache, item))).ToArray() : [];
    }

    private static string[] ChildFiles(string cache, string relative, string pattern)
    {
        string path = FileOps.SafePath(cache, relative);
        return Directory.Exists(path) ? Directory.GetFiles(path, pattern).Select(item => FileOps.SafePath(cache, Path.GetRelativePath(cache, item))).ToArray() : [];
    }

    private static void MoveDirectory(string cache, string from, string to)
    {
        string source = FileOps.SafePath(cache, from);
        if (!Directory.Exists(source)) return;
        string destination = FileOps.SafePath(cache, to);
        foreach (string entry in Directory.GetFileSystemEntries(source))
        {
            string name = Path.GetFileName(entry);
            string safe = FileOps.SafePath(cache, Path.GetRelativePath(cache, entry));
            if (Directory.Exists(safe)) MoveDirectory(cache, from + "/" + name, to + "/" + name);
            else MoveFile(cache, safe, FileOps.SafePath(destination, name));
        }
        RemoveEmpty(source);
    }

    private static void MoveFile(string cache, string source, string destination)
    {
        source = FileOps.SafePath(cache, Path.GetRelativePath(cache, source));
        destination = FileOps.SafePath(cache, Path.GetRelativePath(cache, destination));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (!FileOps.HashEquals(FileOps.Hash(source), FileOps.Hash(destination)))
                throw new IOException("Conflicting saved files while reorganizing " + Path.GetRelativePath(cache, source));
            File.Delete(source);
        }
        else File.Move(source, destination);
    }

    private static void RemoveEmpty(string path)
    {
        FileOps.RejectLink(path);
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }
}
