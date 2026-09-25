using System.Runtime.InteropServices;

namespace SCPSL.ModLaunch.Core;

/// <summary>Runs only with the directory lock held and no unfinished recovery journal.</summary>
public static class CacheMaintenance
{
    public static void BeforePreparation(string root, Action<string>? log = null)
    {
        string cache = RequireIdle(root);
        // Older releases retained active-looking bootstrap names inside private caches.
        // Preserve their bytes while making them harmless, including failed staging work.
        foreach (string file in FilesBelow(cache).Where(x => Path.GetFileName(x).Equals("version.dll", StringComparison.OrdinalIgnoreCase)))
        {
            string hash = FileOps.Hash(file);
            string parked = file + "." + hash[..12] + ".bin";
            FileOps.RejectLink(parked);
            if (File.Exists(parked))
            {
                if (!FileOps.HashEquals(FileOps.Hash(parked), hash)) throw new IOException("Conflicting retired loader payload: " + parked);
                File.Delete(file);
            }
            else File.Move(file, parked);
            log?.Invoke("Disabled an idle cached loader payload: " + Path.GetRelativePath(cache, file));
        }
        CleanCompletedSessions(root, log);
        if (DirectoriesBelow(cache, "sessions").Count() >= 2)
            throw new IOException("Previous loader payloads are still in use. Close the processes holding those files and retry; ModLaunch will not accumulate more retired copies.");
        // Keep the most recent failed preparation for diagnosis until another succeeds.
        foreach (string directory in DirectoriesBelow(cache, "work").OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(1)) TryDeleteTree(cache, directory, log);
        TrimFiles(cache, "logs", 20, "*.log", log);
    }

    public static void AfterSuccessfulPreparation(string root, PreparedSession prepared, Action<string>? log = null)
    {
        string cache = RequireIdle(root);
        string current = Path.GetFullPath(prepared.PreparedDirectory);
        string generations = FileOps.SafePath(cache, "prepared");
        if (!current.StartsWith(generations + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Prepared payloads are outside the managed cache.");
        foreach (string directory in DirectoriesBelow(cache, "prepared"))
            if (!current.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) TryDeleteTree(cache, directory, log);
        foreach (string directory in DirectoriesBelow(cache, "work")) TryDeleteTree(cache, directory, log);
        foreach (string directory in DirectoriesBelow(cache, "tools"))
        {
            string name = Path.GetFileName(directory);
            if (name.StartsWith("melonloader-", StringComparison.OrdinalIgnoreCase) || name.Contains(".invalid-", StringComparison.Ordinal) || name.EndsWith(".staging", StringComparison.Ordinal))
                TryDeleteTree(cache, directory, log);
            else if (name.StartsWith("dotnet-", StringComparison.OrdinalIgnoreCase) && !IsUnder(prepared.DotnetRoot, directory) && !IsUnder(RuntimeEnvironment.GetRuntimeDirectory(), directory))
                TryDeleteTree(cache, directory, log);
        }
        if (!string.IsNullOrWhiteSpace(prepared.AdapterDirectory))
        {
            foreach (string directory in DirectoriesBelow(cache, "tools"))
            {
                string name = Path.GetFileName(directory);
                if (!name.StartsWith("adapter-", StringComparison.Ordinal) || name.Length != 72 || !name[8..].All(Uri.IsHexDigit) ||
                    Path.GetFullPath(directory).Equals(Path.GetFullPath(prepared.AdapterDirectory), StringComparison.OrdinalIgnoreCase)) continue;
                TryDeleteTree(cache, directory, log);
                TryDeleteFile(cache, FileOps.SafePath(cache, "downloads/" + name + ".zip"), log);
            }
        }
        if (!IsUnder(prepared.DotnetRoot, cache))
        {
            string downloads = FileOps.SafePath(cache, "downloads");
            if (Directory.Exists(downloads))
                foreach (string zip in Directory.EnumerateFiles(downloads, "dotnet-runtime-*-win-x64.zip")) TryDeleteFile(cache, zip, log);
        }
        string downloadRoot = FileOps.SafePath(cache, "downloads");
        if (Directory.Exists(downloadRoot))
        {
            string? adapterName = string.IsNullOrWhiteSpace(prepared.AdapterDirectory) ? null : Path.GetFileName(prepared.AdapterDirectory.TrimEnd(Path.DirectorySeparatorChar));
            foreach (string path in FilesBelow(downloadRoot))
            {
                string name = Path.GetFileName(path);
                bool orphanAdapter = Path.GetDirectoryName(path) == downloadRoot && name.StartsWith("adapter-", StringComparison.Ordinal) &&
                    name.EndsWith(".zip", StringComparison.Ordinal) && name.Length == 76 && name[8..^4].All(Uri.IsHexDigit) && name != adapterName + ".zip";
                if (orphanAdapter || name.EndsWith(".part", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal)) TryDeleteFile(cache, path, log);
            }
            string dependencies = FileOps.SafePath(cache, "downloads/adapter-dependencies");
            if (Directory.Exists(dependencies))
                foreach (string file in Directory.EnumerateFiles(dependencies, "*.bin").Where(x => Path.GetFileNameWithoutExtension(x).Length == 64 && Path.GetFileNameWithoutExtension(x).All(Uri.IsHexDigit))
                             .OrderByDescending(File.GetLastWriteTimeUtc).Skip(8))
                {
                    TryDeleteFile(cache, file, log);
                    TryDeleteFile(cache, file + ".sha256", log);
                }
        }
        foreach (string directory in DirectoriesBelow(cache, "resources").OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(2)) TryDeleteTree(cache, directory, log);
        BeforePreparation(root, log);
    }

    public static void CleanCompletedSessions(string root, Action<string>? log = null)
    {
        string cache = RequireIdle(root);
        foreach (string directory in DirectoriesBelow(cache, "sessions")) TryDeleteTree(cache, directory, log);
    }

    private static string RequireIdle(string root)
    {
        string cache = FileOps.SafePath(root, ".modlaunch");
        if (File.Exists(FileOps.SafePath(cache, "session.json"))) throw new InvalidOperationException("Cache cleanup is forbidden while a recovery journal exists.");
        return cache;
    }

    private static IEnumerable<string> DirectoriesBelow(string cache, string relative)
    {
        string parent = FileOps.SafePath(cache, relative);
        if (!Directory.Exists(parent)) return [];
        return Directory.EnumerateDirectories(parent).Select(x => FileOps.SafePath(cache, Path.GetRelativePath(cache, x))).ToArray();
    }

    private static List<string> FilesBelow(string path)
    {
        FileOps.RejectLink(path);
        var result = new List<string>();
        if (!Directory.Exists(path)) return result;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                FileOps.RejectLink(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else result.Add(entry);
            }
        return result;
    }

    private static void TryDeleteTree(string cache, string directory, Action<string>? log)
    {
        directory = FileOps.SafePath(cache, Path.GetRelativePath(cache, directory));
        // Validate the entire target tree before deleting; never recurse through a link.
        foreach (string file in FilesBelow(directory)) TryDeleteFile(cache, file, log);
        DeleteEmpty(directory);
    }

    private static void DeleteEmpty(string directory)
    {
        if (!Directory.Exists(directory)) return;
        FileOps.RejectLink(directory);
        foreach (string child in Directory.EnumerateDirectories(directory)) DeleteEmpty(child);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static void TryDeleteFile(string cache, string path, Action<string>? log)
    {
        path = FileOps.SafePath(cache, Path.GetRelativePath(cache, path));
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { log?.Invoke($"Cleanup deferred until the next launch: {Path.GetRelativePath(cache, path)} ({ex.Message})"); }
    }

    private static void TrimFiles(string cache, string relative, int retain, string pattern, Action<string>? log)
    {
        string parent = FileOps.SafePath(cache, relative);
        if (!Directory.Exists(parent)) return;
        foreach (string file in Directory.EnumerateFiles(parent, pattern).OrderByDescending(File.GetLastWriteTimeUtc).Skip(retain)) TryDeleteFile(cache, file, log);
    }

    private static bool IsUnder(string path, string parent) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
        .StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
