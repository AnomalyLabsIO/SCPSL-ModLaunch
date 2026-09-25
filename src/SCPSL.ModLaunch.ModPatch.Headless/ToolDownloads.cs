using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SCPSL.ModLaunch.ModPatch.Headless;

internal static class ToolDownloads
{
    private static readonly HttpClient Client = CreateClient();
    public static string CacheDirectory { get; set; } = "";
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SCPSL-ModLaunch/0.1.0");
        return client;
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var algorithm = SHA256.Create();
        return Convert.ToHexString(algorithm.ComputeHash(stream));
    }

    public static string Download(string url, string? expectedSha256 = null, CancellationToken ct = default)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || (uri.Host != "raw.githubusercontent.com" && uri.Host != "github.com"))
            throw new InvalidDataException("Dependency download must use an official GitHub HTTPS URL.");
        Directory.CreateDirectory(CacheDirectory);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        string cached = Path.Combine(CacheDirectory, key + ".bin");
        string hashPath = cached + ".sha256";
        if (File.Exists(cached) && File.Exists(hashPath))
        {
            string actual = Hash(cached);
            if (actual == File.ReadAllText(hashPath) && (expectedSha256 == null || actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)))
            {
                // Successful use participates in bounded cache retention without changing identity.
                try { File.SetLastWriteTimeUtc(cached, DateTime.UtcNow); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return cached;
            }
        }
        string temp = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                response.Content.CopyToAsync(output, ct).GetAwaiter().GetResult();
            string hash = Hash(temp);
            if (expectedSha256 != null && !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Dependency checksum mismatch for {uri.AbsolutePath}.");
            File.Move(temp, cached, true);
            File.WriteAllText(hashPath, hash);
            return cached;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static string? PublishedDigest(string repository, string tag, string filename, CancellationToken ct)
    {
        // Old releases have no published digest. If offline, pinned URLs and the validated
        // local cache still permit reuse. A provided digest is always enforced.
        try
        {
            using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            metadataTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var json = JsonDocument.Parse(Client.GetStringAsync(
                $"https://api.github.com/repos/{repository}/releases/tags/{Uri.EscapeDataString(tag)}", metadataTimeout.Token).GetAwaiter().GetResult());
            foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != filename) continue;
                if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind == JsonValueKind.Null) return null;
                var text = digest.GetString();
                if (text?.StartsWith("sha256:", StringComparison.Ordinal) != true || text.Length != 71)
                    throw new InvalidDataException("Unsupported published dependency digest.");
                return text[7..];
            }
            throw new InvalidDataException($"Required dependency asset {filename} is absent from {repository}/{tag}.");
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
