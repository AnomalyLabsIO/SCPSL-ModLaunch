using System.Security.Cryptography;
using System.Text.Json;

namespace SCPSL.ModLaunch.Core;

/// <summary>
/// Reuses previously computed SHA-256 values for ordinary update detection. Matching metadata
/// is a heuristic, not proof against same-size changes with preserved timestamps. Transactional
/// copy, restoration and deletion checks must continue to read the complete contents.
/// The caller holds the game directory lock for the lifetime of this cache.
/// </summary>
public sealed class FileVerificationCache
{
    private const int SchemaVersion = 1;
    private const int MaximumFiles = 16384;
    private const long MaximumDocumentBytes = 8 * 1024 * 1024;
    private readonly string root;
    private readonly string cachePath;
    private readonly Action<string>? log;
    private readonly Dictionary<string, VerifiedFile> files = new(StringComparer.OrdinalIgnoreCase);
    private bool dirty;

    public long CachedFiles { get; private set; }
    public long HashedFiles { get; private set; }
    public long HashedBytes { get; private set; }

    public FileVerificationCache(string gameDirectory, Action<string>? log = null)
    {
        root = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar);
        cachePath = Path.Combine(root, ".modlaunch", "file-verification.json");
        this.log = log;
        Load();
    }

    public string Hash(string path)
    {
        string? relative = ManagedRelative(path);
        if (relative is null) return HashFresh(path);
        string fullPath = FileOps.SafePath(root, relative);
        FileStamp stamp = ReadStamp(fullPath);
        if (files.TryGetValue(relative, out var previous) && previous.Stamp == stamp)
        {
            CachedFiles++;
            return previous.Sha256;
        }
        return HashFresh(fullPath);
    }

    public bool Matches(string path, string expectedHash)
    {
        if (!File.Exists(path))
        {
            Invalidate(path);
            return false;
        }
        return FileOps.HashEquals(Hash(path), expectedHash);
    }

    /// <summary>Always reads the contents and records metadata belonging to that verified read.</summary>
    public string HashFresh(string path)
    {
        string? relative = ManagedRelative(path);
        string fullPath = relative is null ? Path.GetFullPath(path) : FileOps.SafePath(root, relative);
        Invalidate(fullPath);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            FileStamp before = ReadStamp(fullPath);
            using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var algorithm = SHA256.Create();
            string digest = Convert.ToHexString(algorithm.ComputeHash(input));
            HashedFiles++;
            HashedBytes += input.Length;
            FileStamp after = ReadStamp(fullPath);
            if (before != after || input.Length != after.Length) continue;
            if (relative is not null && files.Count < MaximumFiles)
            {
                files[relative] = new VerifiedFile(relative, after.Length, after.LastWriteTimeUtcTicks,
                    after.CreationTimeUtcTicks, digest);
                dirty = true;
            }
            return digest;
        }
        throw new IOException("File changed while its contents were being verified: " + fullPath);
    }

    public void Invalidate(string path)
    {
        string? relative = ManagedRelative(path);
        if (relative is not null && files.Remove(relative)) dirty = true;
    }

    /// <summary>Cache persistence is optional; failure must never block successful restoration.</summary>
    public void Save()
    {
        // Completed sessions and superseded generations must not accumulate stale stamps.
        foreach (var relative in files.Keys.ToArray())
            if (!File.Exists(Path.Combine(root, relative)))
            {
                files.Remove(relative);
                dirty = true;
            }
        if (!dirty) return;
        try
        {
            _ = FileOps.SafePath(root, ".modlaunch/file-verification.json");
            FileOps.WriteJson(cachePath, new VerificationDocument(SchemaVersion, root,
                files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList()));
            dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log?.Invoke("File verification metadata could not be saved; later launches will hash again: " + ex.Message);
        }
    }

    public void LogSummary() => log?.Invoke($"File verification cache: {CachedFiles} metadata reuses, {HashedFiles} full hashes ({HashedBytes / (1024d * 1024):F1} MiB read).");

    private void Load()
    {
        try
        {
            _ = FileOps.SafePath(root, ".modlaunch/file-verification.json");
            if (!File.Exists(cachePath)) return;
            if (new FileInfo(cachePath).Length > MaximumDocumentBytes) throw new InvalidDataException("Metadata cache exceeds its size limit.");
            var document = FileOps.ReadJson<VerificationDocument>(cachePath);
            if (document.SchemaVersion != SchemaVersion || document.GameDirectory != root || document.Files is null || document.Files.Count > MaximumFiles)
                throw new InvalidDataException("Metadata cache has an incompatible schema or game directory.");
            // Validate the entire document before adopting any record. Do not stat each cached
            // path here; reuse validates its current path and metadata when requested.
            var loaded = new Dictionary<string, VerifiedFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in document.Files)
            {
                if (file is null || !IsSafeRelative(file.RelativePath) || file.Length < 0 ||
                    file.LastWriteTimeUtcTicks < DateTime.MinValue.Ticks || file.LastWriteTimeUtcTicks > DateTime.MaxValue.Ticks ||
                    file.CreationTimeUtcTicks < DateTime.MinValue.Ticks || file.CreationTimeUtcTicks > DateTime.MaxValue.Ticks ||
                    file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
                    !loaded.TryAdd(file.RelativePath, file)) throw new InvalidDataException("Metadata cache contains an invalid or duplicate record.");
            }
            foreach (var item in loaded) files.Add(item.Key, item.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            files.Clear();
            log?.Invoke("File verification metadata is unavailable; performing full verification: " + ex.Message);
        }
    }

    private string? ManagedRelative(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.Equals(cachePath, StringComparison.OrdinalIgnoreCase)) return null;
        string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        // Session payloads are unique, temporary and always verified in full by transactions.
        return relative.StartsWith(".modlaunch/sessions/", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(".modlaunch/temporary/", StringComparison.OrdinalIgnoreCase) ? null : relative;
    }

    private static bool IsSafeRelative(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\')) return false;
        return relative.Split('/').All(part => part is not "" and not "." and not ".." &&
            !part.EndsWith('.') && !part.EndsWith(' ') && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    private static FileStamp ReadStamp(string path)
    {
        FileOps.RejectLink(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("File to verify is missing.", path);
        return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
    }

    private readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks, long CreationTimeUtcTicks);
    private sealed record VerifiedFile(string RelativePath, long Length, long LastWriteTimeUtcTicks, long CreationTimeUtcTicks, string Sha256)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public FileStamp Stamp => new(Length, LastWriteTimeUtcTicks, CreationTimeUtcTicks);
    }
    private sealed record VerificationDocument(int SchemaVersion, string GameDirectory, List<VerifiedFile> Files);
}
