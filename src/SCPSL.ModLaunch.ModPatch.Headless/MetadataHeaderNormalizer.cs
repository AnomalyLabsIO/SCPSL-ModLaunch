namespace SCPSL.ModLaunch.ModPatch.Headless;

public sealed record MetadataHeaderNormalizationResult(
    string MetadataPath,
    int HeaderVersionBefore,
    int HeaderVersionAfter,
    bool HeaderWasNormalized,
    string? NormalizationReason);

/// <summary>
/// Inspects <c>global-metadata.dat</c>'s header version and reports any parser-only normalization
/// needed for structurally-mislabelled metadata, without touching the file on disk. Rewriting the
/// header on disk (esp. for SCP:SL Unity 6000 builds where it's v23-on-a-v31-file) destabilises the
/// game at runtime: Unity's IL2CPP loader reads the header itself and either crashes or boots into
/// broken state when it sees a version it didn't ship the file with.
/// <para>
/// The in-memory parser-only normalization in <see cref="NormalizeBytesForParser(byte[], int?, Action{string}?)"/>
/// covers both v12's v23→v24 case and Unity 6000's v23→v31 case for the dumper without modifying
/// the file Unity will read at runtime.
/// </para>
/// </summary>
public static class MetadataHeaderNormalizer
{
    private const uint MetadataMagic = 0xFAB11BAF;
    private const int VersionOffset = 4;

    /// <summary>
    /// Reads the header version, validates the magic, and reports any structural mislabel
    /// detection for the audit log. Never writes to disk.
    /// </summary>
    public static MetadataHeaderNormalizationResult NormalizeFile(string metadataPath, int? parserTargetVersion = null)
    {
        if (!File.Exists(metadataPath))
            throw new InvalidDataException($"global-metadata.dat not found at {metadataPath}.");

        byte[] metadataBytes = File.ReadAllBytes(metadataPath);
        int headerVersion = ReadValidatedHeaderVersion(metadataBytes, metadataPath);

        // Detect-only: surface what the parser-only normalization would treat the file as,
        // so the audit log records the structural mismatch without changing what's on disk.
        if (TryGetParserNormalization(metadataBytes, parserTargetVersion, out int detectedVersion, out string? reason))
        {
            return new MetadataHeaderNormalizationResult(
                metadataPath,
                headerVersion,
                detectedVersion,
                HeaderWasNormalized: false,
                NormalizationReason: $"parser-only, file unchanged: {reason}");
        }

        return new MetadataHeaderNormalizationResult(
            metadataPath,
            headerVersion,
            headerVersion,
            HeaderWasNormalized: false,
            NormalizationReason: null);
    }

    /// <summary>
    /// In-memory normalisation used by Il2CppDumper when handing
    /// metadata bytes to Il2CppDumper. Returns either the original buffer (when no
    /// structural mislabel applies) or a clone with the header fixed up. The on-disk file
    /// is never touched by this path either; the rewrite only affects what the dumper sees.
    /// </summary>
    public static byte[] NormalizeBytesForParser(byte[] metadataBytes, Action<string>? log = null)
        => NormalizeBytesForParser(metadataBytes, parserTargetVersion: null, log);

    public static byte[] NormalizeBytesForParser(byte[] metadataBytes, int? parserTargetVersion, Action<string>? log = null)
    {
        if (!TryGetParserNormalization(metadataBytes, parserTargetVersion, out int headerVersionAfter, out string? reason))
            return metadataBytes;

        byte[] normalizedBytes = (byte[])metadataBytes.Clone();
        WriteInt32(normalizedBytes, VersionOffset, headerVersionAfter);
        log?.Invoke($"Treating metadata header as v{headerVersionAfter} for Il2CppDumper parsing ({reason}; in-memory only).");
        return normalizedBytes;
    }

    // Il2CppDumper auto-detects sub-versions (24.0-24.5) by inspecting the parsed metadata when
    // header.version == 24. Forcing the header to 24 would collapse that sub-version detection, so
    // target-version normalization intentionally skips the v24 family.
    private const int UnitySubversionDetectableVersion = 24;

    private static int ReadValidatedHeaderVersion(byte[] metadataBytes, string metadataPath)
    {
        if (metadataBytes.Length < 8)
        {
            throw new InvalidDataException(
                $"global-metadata.dat is too small ({metadataBytes.Length} bytes) to contain a valid header at {metadataPath}.");
        }

        uint magic = ReadUInt32(metadataBytes, 0);
        if (magic != MetadataMagic)
        {
            throw new InvalidDataException(
                $"global-metadata.dat has unexpected magic 0x{magic:X8} at {metadataPath}; expected 0x{MetadataMagic:X8}.");
        }

        return ReadInt32(metadataBytes, VersionOffset);
    }

    private static bool TryGetStructuralNormalization(byte[] metadataBytes, out int headerVersionAfter, out string? reason)
    {
        if (ShouldTreatV23MetadataAsV24(metadataBytes))
        {
            headerVersionAfter = 24;
            reason = "Metadata header reports v23, but the type-definition table shape matches Unity 2019 v24 layout.";
            return true;
        }

        headerVersionAfter = 0;
        reason = null;
        return false;
    }

    private static bool TryGetParserNormalization(byte[] metadataBytes, int? parserTargetVersion, out int headerVersionAfter, out string? reason)
    {
        if (TryGetStructuralNormalization(metadataBytes, out headerVersionAfter, out reason))
            return true;

        if (parserTargetVersion is > 0 and not UnitySubversionDetectableVersion
            && TryReadHeaderVersion(metadataBytes, out int headerVersion)
            && headerVersion != parserTargetVersion.Value)
        {
            headerVersionAfter = parserTargetVersion.Value;
            reason = $"metadata header reports v{headerVersion}, but the UnityPlayer.dll identity expects v{parserTargetVersion.Value}";
            return true;
        }

        headerVersionAfter = 0;
        reason = null;
        return false;
    }

    private static bool ShouldTreatV23MetadataAsV24(byte[] metadataBytes)
    {
        if (!IsMetadataVersion(metadataBytes, 23) || metadataBytes.Length < 168)
            return false;

        // Il2CppTypeDefinition struct sizes in the two layouts. If the type-definitions table
        // size is divisible by the v24 size but not the v23 size, the file is structurally v24
        // even though the header reports v23.
        const int v23TypeDefinitionSize = 104;
        const int v24TypeDefinitionSize = 92;

        uint stringLiteralOffset = ReadUInt32(metadataBytes, 8);
        int typeDefinitionsSize = ReadInt32(metadataBytes, 164);

        return stringLiteralOffset == 264
               && typeDefinitionsSize > 0
               && typeDefinitionsSize % v23TypeDefinitionSize != 0
               && typeDefinitionsSize % v24TypeDefinitionSize == 0;
    }

    private static bool IsMetadataVersion(byte[] metadataBytes, int version)
    {
        return metadataBytes.Length >= 240
               && ReadUInt32(metadataBytes, 0) == MetadataMagic
               && ReadInt32(metadataBytes, VersionOffset) == version;
    }

    private static bool TryReadHeaderVersion(byte[] metadataBytes, out int version)
    {
        version = 0;
        if (metadataBytes.Length < 8 || ReadUInt32(metadataBytes, 0) != MetadataMagic)
            return false;

        version = ReadInt32(metadataBytes, VersionOffset);
        return true;
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return BitConverter.ToUInt32(data, offset);
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        return BitConverter.ToInt32(data, offset);
    }

    private static void WriteInt32(byte[] data, int offset, int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Array.Copy(bytes, 0, data, offset, bytes.Length);
    }
}
