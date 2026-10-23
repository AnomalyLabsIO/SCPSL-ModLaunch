using System.Runtime.InteropServices;

namespace SCPSL.ModLaunch.Core;

/// <summary>Runs only with the directory lock held and no unfinished recovery journal.</summary>
public static class CacheMaintenance
{
    public static void BeforePreparation(string root, Action<string>? log = null)
    {
        string cache = RequireIdle(root);
        CacheLayout.Upgrade(root, log);
        foreach (string file in FilesBelow(cache).Where(x => Path.GetFileName(x).Equals("version.dll", StringComparison.OrdinalIgnoreCase)))
        {
            string hash = FileOps.Hash(file);
            string parked = file + "." + hash[..12] + ".disabled";
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
        if (SessionDirectories(cache).Count() >= 2)
            throw new IOException("Previous loader payloads are still in use. Close the processes holding those files and retry.");
        foreach (string directory in DirectoriesBelow(cache, CacheLayout.Temporary).OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(1))
            TryDeleteTree(cache, directory, log);
        TrimFiles(cache, "logs", 20, "*.log", log);
    }

    public static void AfterSuccessfulPreparation(string root, PreparedSession prepared, Action<string>? log = null)
    {
        string cache = RequireIdle(root);
        CacheLayout.Upgrade(root, log);
        string current = FileOps.SafePath(cache, CacheLayout.ModdedFiles);
        if (!Path.GetFullPath(prepared.PreparedDirectory).Equals(current, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Prepared game files are outside the managed cache.");
        foreach (string directory in DirectoriesBelow(cache, "prepared")) TryDeleteTree(cache, directory, log);
        foreach (string directory in DirectoriesBelow(cache, CacheLayout.Temporary)) TryDeleteTree(cache, directory, log);
        foreach (string directory in DirectoriesBelow(cache, CacheLayout.Tools))
        {
            string name = Path.GetFileName(directory);
            bool unused = name.StartsWith("downloaded-", StringComparison.Ordinal) &&
                !Path.GetFullPath(directory).Equals(Path.GetFullPath(prepared.AdapterDirectory.Length == 0 ? current : prepared.AdapterDirectory), StringComparison.OrdinalIgnoreCase);
            if (name.StartsWith("melonloader-", StringComparison.OrdinalIgnoreCase) || name.Contains(".invalid-", StringComparison.Ordinal) ||
                name.EndsWith(".staging", StringComparison.Ordinal) || unused ||
                name.StartsWith("dotnet-", StringComparison.OrdinalIgnoreCase) && !IsUnder(prepared.DotnetRoot, directory) && !IsUnder(RuntimeEnvironment.GetRuntimeDirectory(), directory))
            {
                TryDeleteTree(cache, directory, log);
                if (unused) TryDeleteFile(cache, FileOps.SafePath(cache, "downloads/SCPSL-ModLaunch-patching-tools-" + name[11..] + ".zip"), log);
            }
        }
        string downloads = FileOps.SafePath(cache, "downloads");
        if (Directory.Exists(downloads))
        {
            string? adapterName = string.IsNullOrWhiteSpace(prepared.AdapterDirectory) ? null : Path.GetFileName(prepared.AdapterDirectory.TrimEnd(Path.DirectorySeparatorChar));
            string? currentArchive = adapterName?.StartsWith("downloaded-", StringComparison.Ordinal) == true
                ? "SCPSL-ModLaunch-patching-tools-" + adapterName[11..] + ".zip" : null;
            foreach (string path in FilesBelow(downloads))
            {
                string name = Path.GetFileName(path);
                bool orphanAdapter = Path.GetDirectoryName(path) == downloads &&
                    name.StartsWith("SCPSL-ModLaunch-patching-tools-", StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.Ordinal) && name != currentArchive;
                if (orphanAdapter || name.EndsWith(".part", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal))
                    TryDeleteFile(cache, path, log);
            }
            if (!IsUnder(prepared.DotnetRoot, cache))
                foreach (string zip in Directory.EnumerateFiles(downloads, "dotnet-runtime-*-win-x64.zip")) TryDeleteFile(cache, zip, log);
            foreach (string file in Directory.EnumerateFiles(downloads, "*.bin").Where(x => Path.GetFileNameWithoutExtension(x).Length == 64 && Path.GetFileNameWithoutExtension(x).All(Uri.IsHexDigit)))
            {
                TryDeleteFile(cache, file, log);
                TryDeleteFile(cache, file + ".sha256", log);
            }
            foreach (string sidecar in Directory.EnumerateFiles(downloads, "*.sha256").Where(x => File.Exists(x[..^7]))
                         .OrderByDescending(x => File.GetLastWriteTimeUtc(x[..^7])).Skip(8))
            {
                TryDeleteFile(cache, sidecar[..^7], log);
                TryDeleteFile(cache, sidecar, log);
            }
        }
        string oldDownloads = FileOps.SafePath(cache, "downloads/adapter-dependencies");
        if (Directory.Exists(oldDownloads)) TryDeleteTree(cache, oldDownloads, log);
        foreach (string directory in DirectoriesBelow(cache, "downloads/patch-definitions").OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(2))
            TryDeleteTree(cache, directory, log);
        foreach (string empty in new[] { "prepared", "sessions", CacheLayout.Temporary })
            DeleteEmpty(FileOps.SafePath(cache, empty));
        BeforePreparation(root, log);
    }

    public static void CleanCompletedSessions(string root, Action<string>? log = null)
    {
        string cache = RequireIdle(root);
        foreach (string directory in SessionDirectories(cache)) TryDeleteTree(cache, directory, log);
        DeleteEmpty(FileOps.SafePath(cache, "sessions"));
        DeleteEmpty(FileOps.SafePath(cache, CacheLayout.Temporary));
    }

    private static IEnumerable<string> SessionDirectories(string cache) =>
        DirectoriesBelow(cache, "sessions").Concat(DirectoriesBelow(cache, CacheLayout.Temporary)
            .Where(x => Path.GetFileName(x).StartsWith("session-", StringComparison.Ordinal)));

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
