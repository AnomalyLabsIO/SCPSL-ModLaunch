using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SCPSL.ModLaunch.Core;

/// <summary>Refreshes the private adapter as one complete file set. The caller holds the game directory lock.</summary>
public static class BundledAdapterInstallation
{
    private const string ManifestName = ".bundle.json";
    private const long ArchiveLimit = 32L * 1024 * 1024;
    private const long FileLimit = 64L * 1024 * 1024;
    private const long ExpandedLimit = 128L * 1024 * 1024;

    public static string Install(string cache, Stream archive, Action<string>? checkpoint = null)
    {
        cache = Path.GetFullPath(cache);
        if (File.Exists(FileOps.SafePath(cache, "session.json")))
            throw new InvalidOperationException("The bundled adapter cannot change while a recovery journal exists.");
        string target = FileOps.SafePath(cache, "tools/adapter-bundled");
        string previous = FileOps.SafePath(cache, "tools/adapter-bundled.invalid-previous");
        if (File.Exists(target) || File.Exists(previous)) throw new InvalidDataException("A file occupies a bundled-adapter directory path.");
        // Both directory moves are atomic on the game volume. If termination occurred between
        // them, first reinstate the retained complete installation before considering an update.
        if (!Directory.Exists(target) && Directory.Exists(previous))
        {
            _ = FilesBelow(previous);
            Directory.Move(previous, target);
        }

        using var bytes = ReadBounded(archive);
        string archiveHash = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        bytes.Position = 0;
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read, leaveOpen: true);
        string stage = FileOps.SafePath(cache, "tools/ab-" + Guid.NewGuid().ToString("N")[..8] + ".staging");
        var entries = Validate(zip, stage);
        var expectedFiles = entries.Select(e => new PreparedFile(e.Relative, HashEntry(e.Entry))).ToArray();
        if (Matches(target, archiveHash, expectedFiles)) return target;

        Directory.CreateDirectory(stage);
        var files = new List<PreparedFile>();
        foreach (var (entry, relative) in entries)
        {
            string destination = FileOps.SafePath(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var source = entry.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                CopyEntry(source, output, entry.Length);
                output.Flush(flushToDisk: true);
            }
            files.Add(new PreparedFile(relative, FileOps.Hash(destination)));
        }
        FileOps.WriteJson(FileOps.SafePath(stage, ManifestName), new BundleManifest(1, archiveHash, files));
        if (!Matches(stage, archiveHash, expectedFiles)) throw new InvalidDataException("The extracted bundled adapter failed validation.");
        checkpoint?.Invoke("bundle-staged");

        // Validate existing private trees before moving them; never traverse a reparse point.
        _ = FilesBelow(target);
        _ = FilesBelow(previous);
        if (Directory.Exists(previous))
            Directory.Move(previous, FileOps.SafePath(cache, "tools/adapter-bundled.invalid-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(target)) Directory.Move(target, previous);
        checkpoint?.Invoke("bundle-retired");
        try { Directory.Move(stage, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(target) && Directory.Exists(previous)) Directory.Move(previous, target);
            throw;
        }
        checkpoint?.Invoke("bundle-installed");
        return target;
    }

    private static MemoryStream ReadBounded(Stream source)
    {
        var result = new MemoryStream();
        try
        {
            byte[] buffer = new byte[65536];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (result.Length + read > ArchiveLimit) throw new InvalidDataException("The embedded adapter archive exceeds its size limit.");
                result.Write(buffer, 0, read);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static List<(ZipArchiveEntry Entry, string Relative)> Validate(ZipArchive archive, string root)
    {
        if (archive.Entries.Count > 2048) throw new InvalidDataException("The embedded adapter has too many entries.");
        var files = new List<(ZipArchiveEntry, string)>();
        var paths = new Dictionary<string, (string Case, bool Directory)>(StringComparer.OrdinalIgnoreCase);
        var explicitEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            int type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not 0 and not 0x8000 and not 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The embedded adapter contains a link or special file.");
            bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            string relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
            if (relative.Length == 0) throw new InvalidDataException("The embedded adapter has an empty path.");
            _ = FileOps.SafePath(root, relative);
            if (!explicitEntries.Add(relative)) throw new InvalidDataException("The embedded adapter has duplicate paths.");
            if (Path.GetFileName(relative).Equals("version.dll", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The embedded adapter contains a reserved loader or manifest path.");
            string[] segments = relative.Split('/');
            for (int i = 1; i <= segments.Length; i++)
            {
                string path = string.Join('/', segments.Take(i));
                bool isDirectory = i < segments.Length || directory;
                if (paths.TryGetValue(path, out var existing) && (existing.Case != path || existing.Directory != isDirectory))
                    throw new InvalidDataException("The embedded adapter contains case or file/directory collisions.");
                paths[path] = (path, isDirectory);
            }
            if (directory) continue;
            if (entry.Length < 0 || entry.Length > FileLimit || expanded + entry.Length > ExpandedLimit)
                throw new InvalidDataException("The expanded adapter exceeds its size limit.");
            expanded += entry.Length;
            files.Add((entry, relative));
        }
        if (!files.Any(f => f.Item2 == "SCPSL-ModLaunch.ModPatch.Headless.exe" && f.Item1.Length > 0))
            throw new InvalidDataException("The embedded adapter is missing SCPSL-ModLaunch.ModPatch.Headless.exe.");
        return files;
    }

    private static void CopyEntry(Stream source, Stream destination, long expectedLength)
    {
        byte[] buffer = new byte[65536];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            copied += read;
            if (copied > expectedLength) throw new InvalidDataException("The embedded adapter entry exceeds its declared length.");
            destination.Write(buffer, 0, read);
        }
        if (copied != expectedLength) throw new InvalidDataException("The embedded adapter entry is truncated.");
    }

    private static string HashEntry(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            copied += read;
            if (copied > entry.Length) throw new InvalidDataException("The embedded adapter entry exceeds its declared length.");
            hash.AppendData(buffer, 0, read);
        }
        if (copied != entry.Length) throw new InvalidDataException("The embedded adapter entry is truncated.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool Matches(string directory, string archiveHash, IReadOnlyCollection<PreparedFile> expectedFiles)
    {
        if (!Directory.Exists(directory)) return false;
        var expected = expectedFiles.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var actual = FilesBelow(directory).Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected.Keys.Append(ManifestName))) return false;
        try
        {
            var manifest = FileOps.ReadJson<BundleManifest>(FileOps.SafePath(directory, ManifestName));
            if (manifest.SchemaVersion != 1 || manifest.ArchiveHash != archiveHash || manifest.Files == null || manifest.Files.Count != expected.Count) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                if (file == null || file.RelativePath == null || !seen.Add(file.RelativePath) || !expected.TryGetValue(file.RelativePath, out var expectedFile) ||
                    !FileOps.HashEquals(file.Sha256, expectedFile.Sha256)) return false;
                if (!FileOps.HashEquals(FileOps.Hash(FileOps.SafePath(directory, file.RelativePath)), file.Sha256)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { return false; }
    }

    private static List<string> FilesBelow(string root)
    {
        FileOps.RejectLink(root);
        var result = new List<string>();
        if (!Directory.Exists(root)) return result;
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count != 0)
            foreach (string entry in Directory.EnumerateFileSystemEntries(directories.Pop()))
            {
                FileOps.RejectLink(entry);
                if (Directory.Exists(entry)) directories.Push(entry);
                else result.Add(entry);
            }
        return result;
    }

    private sealed record BundleManifest(int SchemaVersion, string ArchiveHash, List<PreparedFile> Files);
}
