using System.Security.Cryptography;
using System.Text.Json;

namespace SCPSL.ModLaunch.Core;

public static class FileOps
{
    public static string Hash(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var algorithm = SHA256.Create();
        return Convert.ToHexString(algorithm.ComputeHash(input));
    }

    public static bool HashEquals(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException($"Unsafe relative path: {relative}");
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException($"Unsafe relative path: {relative}");
        string absoluteRoot = Path.GetFullPath(root);
        RejectLink(absoluteRoot);
        string current = absoluteRoot;
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            RejectLink(current);
        }
        return current;
    }

    public static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Links/reparse points are not supported for managed files: {path}");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    public static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options)
        ?? throw new InvalidDataException($"Empty JSON document: {path}");

    public static void WriteJson<T>(string path, T value) => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options));

    public static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectLink(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void CopyVerified(string source, string destination, string expectedHash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        RejectLink(source);
        RejectLink(destination);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (!HashEquals(Hash(temp), expectedHash)) throw new InvalidDataException($"Hash mismatch copying {source}.");
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
