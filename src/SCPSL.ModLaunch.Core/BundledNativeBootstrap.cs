using System.IO.Compression;
using System.Security.Cryptography;

namespace SCPSL.ModLaunch.Core;

/// <summary>Selects the bundled stock MelonLoader 0.7.3 bootstrap rebuilt with Dobby PR #13.</summary>
public static class BundledNativeBootstrap
{
    public const string NativeDllSha256 = "18B6DA09BB8976F0036AEAE428F21D95E2C222420A17C8B985AF4B6D59551BF0";
    public const long NativeDllLength = 11_058_688;
    private const long ArchiveLimit = 32L * 1024 * 1024;

    public static void InstallAndSelect(string cache, PreparedSession prepared, Stream? resource, Action<string>? log = null)
    {
        if (resource == null)
            throw new InvalidDataException("This launcher package is missing its bundled PR #13 native bootstrap. Install the complete launcher package.");
        InstallAndSelectCore(cache, prepared, resource, NativeDllSha256, NativeDllLength, log);
    }

    internal static void InstallAndSelectFixture(string cache, PreparedSession prepared, Stream resource, string expectedDllHash,
        long expectedDllLength) => InstallAndSelectCore(cache, prepared, resource, expectedDllHash, expectedDllLength, null);

    private static void InstallAndSelectCore(string cache, PreparedSession prepared, Stream resource, string expectedDllHash,
        long expectedDllLength, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(resource);
        cache = Path.GetFullPath(cache);
        if (File.Exists(FileOps.SafePath(cache, "session.json")))
            throw new InvalidOperationException("Recover the pending session before selecting the bundled native bootstrap.");
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[65536];
        int read;
        while ((read = resource.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (bytes.Length + read > ArchiveLimit)
                throw new InvalidDataException("The bundled native bootstrap archive exceeds its size limit.");
            bytes.Write(buffer, 0, read);
        }
        byte[] contents = bytes.ToArray();
        ValidateArchive(contents, expectedDllHash, expectedDllLength);
        string archiveHash = Convert.ToHexString(SHA256.HashData(contents));
        // Keep the compressed payload only for this launch. The executable already owns a
        // verified copy; session activation stages its DLL under a neutral filename.
        prepared.BootstrapArchiveContents = contents;
        prepared.BootstrapArchive = null;
        prepared.BootstrapArchiveSha256 = archiveHash;
        prepared.BootstrapHash = expectedDllHash;
        log?.Invoke($"Using the bundled MelonLoader 0.7.3 native bootstrap with Dobby PR #13 (SHA-256 {expectedDllHash}).");
    }

    private static void ValidateArchive(byte[] contents, string expectedDllHash, long expectedDllLength)
    {
        using var input = new MemoryStream(contents, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "version.dll")
            throw new InvalidDataException("The bundled native bootstrap archive must contain only version.dll.");
        var entry = archive.Entries[0];
        int type = (entry.ExternalAttributes >> 16) & 0xF000;
        if (entry.Length != expectedDllLength || type is not 0 and not 0x8000 ||
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The bundled native bootstrap has an unexpected size or file type.");
        using var source = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            length += read;
            if (length > expectedDllLength)
                throw new InvalidDataException("The bundled native bootstrap exceeds its expected size.");
            hash.AppendData(buffer, 0, read);
        }
        if (length != expectedDllLength || !FileOps.HashEquals(Convert.ToHexString(hash.GetHashAndReset()), expectedDllHash))
            throw new InvalidDataException("The bundled native bootstrap does not match its pinned SHA-256.");
    }
}
