using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace SCPSL.ModLaunch.Core.Provisioning;

internal sealed class ArtifactCache(HttpClient http, Action<string> log)
{
    internal static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        using var algorithm = SHA256.Create();
        return Convert.ToHexString(algorithm.ComputeHash(file));
    }

    internal static bool IsHash(string? value, int length = 64) =>
        value is not null && value.Length == length && value.All(Uri.IsHexDigit);

    internal static bool Matches(string path, string expected, bool sha512 = false)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        using HashAlgorithm algorithm = sha512 ? SHA512.Create() : SHA256.Create();
        var actual = Convert.ToHexString(algorithm.ComputeHash(stream));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException($"Invalid relative path: {relative}");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is ".." or "." || p.Length == 0 || p.TrimEnd(' ', '.') != p || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException($"Invalid relative path: {relative}");
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes its cache directory: {relative}");
        var current = path;
        while (!current.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Symbolic links are not allowed in managed files: {current}");
            current = Path.GetDirectoryName(current) ?? root;
        }
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Symbolic links are not allowed in managed cache: {root}");
        return path;
    }

    internal static async Task WriteJsonAsync<T>(string path, T data, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, data, JsonDefaults.Options, ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options) ?? throw new InvalidDataException($"Empty JSON file: {path}");

    internal async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024)
            throw new InvalidDataException($"Remote manifest is too large: {url}");
        await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
        using var memory = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, budget.Token)) > 0)
        {
            if (memory.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Remote manifest exceeds the size limit.");
            memory.Write(bytes, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    internal async Task<string> DownloadAsync(string url, string destination, string expectedHash, bool sha512, CancellationToken ct,
        long maximumBytes = 512L * 1024 * 1024)
    {
        if (!IsHash(expectedHash, sha512 ? 128 : 64)) throw new InvalidDataException("The artifact has no valid expected digest.");
        if (Matches(destination, expectedHash, sha512)) return destination;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        log($"Downloading {Path.GetFileName(destination)}...");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes)
                throw new InvalidDataException("Download exceeds its size limit.");
            await using (var source = await response.Content.ReadAsStreamAsync(budget.Token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                int count;
                while ((count = await source.ReadAsync(buffer, budget.Token)) > 0)
                {
                    if (output.Length + count > maximumBytes) throw new InvalidDataException("Download exceeds its size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), budget.Token);
                }
                output.Flush(flushToDisk: true);
            }
            if (!Matches(temporary, expectedHash, sha512)) throw new InvalidDataException($"Digest mismatch for {url}; refusing the download.");
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string ExtractVerified(string zipPath, string destination)
    {
        var manifest = Path.Combine(destination, ".extracted.json");
        if (File.Exists(manifest))
        {
            try
            {
                var existing = ReadJson<ExtractionManifest>(manifest);
                if (existing.ArchiveHash == Hash(zipPath) && existing.Files is { Count: > 0 } &&
                    existing.Files.All(x => x is not null && IsHash(x.Sha256) && Matches(SafePath(destination, x.RelativePath), x.Sha256)) &&
                    existing.Files.Select(x => x.RelativePath.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() == existing.Files.Count)
                {
                    var expected = existing.Files.Select(x => x.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                        .Select(x => Path.GetRelativePath(destination, x).Replace('\\', '/'))
                        .All(x => x == ".extracted.json" || expected.Contains(x))) return destination;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { /* Rebuild corrupt cache from the verified archive. */ }
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".staging";
        Directory.CreateDirectory(temporary);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outputs = new List<PreparedFile>();
            long extracted = 0;
            foreach (var entry in archive.Entries)
            {
                // Unix symlinks and all other special files are disallowed, even on Windows.
                var fileType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (fileType is not 0 and not 0x8000 and not 0x4000) throw new InvalidDataException("ZIP contains a special file.");
                var relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
                if (relative.Length == 0) continue;
                var target = SafePath(temporary, relative);
                if (!seen.Add(relative)) throw new InvalidDataException($"Duplicate ZIP entry: {relative}");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
                extracted = checked(extracted + entry.Length);
                if (extracted > 2L * 1024 * 1024 * 1024 || seen.Count > 20000) throw new InvalidDataException("Expanded ZIP exceeds its size limit.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
                outputs.Add(new PreparedFile(relative, Hash(target)));
            }
            if (outputs.Count == 0) throw new InvalidDataException("The ZIP archive is empty.");
            File.WriteAllText(Path.Combine(temporary, ".extracted.json"), JsonSerializer.Serialize(new ExtractionManifest(Hash(zipPath), outputs), JsonDefaults.Options));
            if (Directory.Exists(destination))
            {
                // Never recursively delete a pre-existing cache tree. Move it aside for diagnosis.
                Directory.Move(destination, destination + ".invalid-" + Guid.NewGuid().ToString("N"));
            }
            Directory.Move(temporary, destination);
            return destination;
        }
        finally
        {
            // A failed extraction is intentionally retained for diagnosis; it is never reused.
        }
    }

    // Preparation never extracts the injection proxy. Keeping it only inside the official
    // archive avoids the game's recursive exact-name loader check even after a failed prepare.
    internal static string ExtractMelonRuntime(string zipPath, string stage)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = ValidateArchive(archive, stage);
        var bootstrap = entries.SingleOrDefault(x => x.Relative.Equals("version.dll", StringComparison.OrdinalIgnoreCase));
        if (bootstrap.Entry is null) throw new InvalidDataException("The MelonLoader archive has no version.dll bootstrap.");
        string bootstrapHash;
        using (var stream = bootstrap.Entry.Open())
        using (var hash = SHA256.Create()) bootstrapHash = Convert.ToHexString(hash.ComputeHash(stream));
        foreach (var (entry, relative) in entries)
        {
            if (!relative.StartsWith("MelonLoader/", StringComparison.OrdinalIgnoreCase)) continue;
            // No nested injection proxies are accepted from a changed upstream layout.
            if (Path.GetFileName(relative).Equals("version.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The MelonLoader archive contains an unexpected nested loader proxy.");
            string target = SafePath(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
        return bootstrapHash;
    }

    internal static string BootstrapHash(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = ValidateArchive(archive, Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        var bootstrap = entries.SingleOrDefault(x => x.Relative.Equals("version.dll", StringComparison.OrdinalIgnoreCase));
        if (bootstrap.Entry is null) throw new InvalidDataException("The MelonLoader archive has no version.dll bootstrap.");
        using var stream = bootstrap.Entry.Open();
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    private static List<(ZipArchiveEntry Entry, string Relative)> ValidateArchive(ZipArchive archive, string root)
    {
        var files = new List<(ZipArchiveEntry, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            int type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not 0 and not 0x8000 and not 0x4000) throw new InvalidDataException("ZIP contains a special file.");
            string relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
            if (relative.Length == 0) continue;
            _ = SafePath(root, relative);
            if (!seen.Add(relative)) throw new InvalidDataException($"Duplicate ZIP entry: {relative}");
            if (seen.Count > 20000) throw new InvalidDataException("ZIP contains too many entries.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            expanded = checked(expanded + entry.Length);
            if (expanded > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded ZIP exceeds its size limit.");
            files.Add((entry, relative));
        }
        return files;
    }

    private sealed record ExtractionManifest(string ArchiveHash, List<PreparedFile> Files);
}
