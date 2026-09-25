using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Il2CppDumper;

namespace SCPSL.ModLaunch.ModPatch.Headless;

internal sealed class StaticGame : IDisposable
{
    private readonly MemoryStream _binaryStream;
    private readonly MemoryStream _metadataStream;
    private readonly Dictionary<string, int[]> _methods;
    private readonly int[] _starts;
    public byte[] Binary { get; }
    public string UnityVersion { get; }
    public int ParserMetadataVersion { get; }
    public bool NeedsInteropGeneration { get; }

    public StaticGame(string directory, IReadOnlyCollection<VersionProfile> profiles)
    {
        string playerPath = Path.Combine(directory, "UnityPlayer.dll");
        string product = FileVersionInfo.GetVersionInfo(playerPath).ProductVersion ?? "";
        UnityVersion = Regex.Match(product, @"^\d+\.\d+\.\d+[abfp]\d+").Value;
        if (UnityVersion.Length == 0) throw new InvalidDataException("UnityPlayer.dll has no supported Unity version identity.");
        var applicable = profiles.Where(p => LauncherMatches(p, UnityVersion)).ToArray();
        int[] versions = applicable.Select(p => p.MetadataVersion).Distinct().ToArray();
        if (versions.Length != 1 || versions[0] is not (24 or 29 or 31))
            throw new InvalidDataException($"Unsupported or ambiguous Unity/metadata profile: {UnityVersion}.");
        ParserMetadataVersion = versions[0];
        Binary = File.ReadAllBytes(Path.Combine(directory, "GameAssembly.dll"));
        byte[] runtimeMetadata = File.ReadAllBytes(Path.Combine(directory, "SCPSL_Data", "il2cpp_data", "Metadata", "global-metadata.dat"));
        _ = MetadataHeaderNormalizer.NormalizeFile(Path.Combine(directory, "SCPSL_Data", "il2cpp_data", "Metadata", "global-metadata.dat"), ParserMetadataVersion);
        byte[] parserMetadata = MetadataHeaderNormalizer.NormalizeBytesForParser(runtimeMetadata, ParserMetadataVersion, Console.WriteLine);
        NeedsInteropGeneration = !parserMetadata.AsSpan().SequenceEqual(runtimeMetadata);
        // BinaryStream constructs a BinaryWriter even during reads. Give the parser detached
        // mutable copies so its implementation can never change the bytes we later patch.
        _binaryStream = new MemoryStream((byte[])Binary.Clone(), writable: true);
        _metadataStream = new MemoryStream((byte[])parserMetadata.Clone(), writable: true);
        if (Binary.Length < 64 || Binary[0] != 'M' || Binary[1] != 'Z') throw new InvalidDataException("GameAssembly.dll is not a Windows PE file.");
        int peOffset = BitConverter.ToInt32(Binary, 0x3c);
        if (peOffset < 0 || peOffset > Binary.Length - 6 || BitConverter.ToUInt16(Binary, peOffset + 4) != 0x8664)
            throw new InvalidDataException("Only Windows x64 GameAssembly is supported.");
        var metadata = new Metadata(_metadataStream);
        var pe = new PE(_binaryStream);
        pe.SetProperties(ParserMetadataVersion == 24 ? metadata.Version : ParserMetadataVersion, metadata.metadataUsagesCount);
        if (pe.CheckDump()) throw new InvalidDataException("Dumped GameAssembly inputs are unsupported; use verified original game files.");
        if (!pe.PlusSearch(metadata.methodDefs.Count(m => m.methodIndex >= 0), metadata.typeDefs.Length, metadata.imageDefs.Length))
            throw new InvalidDataException("GameAssembly cannot be parsed statically. Protected or unsupported game build; no code was executed.");
        string dumpDir = Path.Combine(directory, ".adapter-work", "symbols");
        Directory.CreateDirectory(dumpDir);
        var generator = new StructGenerator(new Il2CppExecutor(metadata, pe));
        generator.WriteScript(dumpDir + Path.DirectorySeparatorChar);
        using var script = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dumpDir, "script.json")));
        var methods = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var method in script.RootElement.GetProperty("ScriptMethod").EnumerateArray())
        {
            string name = method.GetProperty("Name").GetString() ?? "";
            ulong address = method.GetProperty("Address").GetUInt64();
            if (address == 0) continue;
            long offset = checked((long)pe.MapVATR(address + pe.ImageBase));
            if (offset <= 0 || offset >= Binary.Length) throw new InvalidDataException($"Invalid method mapping for {name}.");
            if (!methods.TryGetValue(name, out var entries)) methods[name] = entries = [];
            entries.Add((int)offset);
        }
        _methods = methods.ToDictionary(k => k.Key, k => k.Value.OrderBy(value => value).ToArray(), StringComparer.Ordinal);
        _starts = _methods.Values.SelectMany(v => v).Distinct().OrderBy(value => value).ToArray();
        Directory.Delete(dumpDir, recursive: true);
    }

    public static bool LauncherMatches(VersionProfile profile, string unityVersion) =>
        profile.CleanLauncherPath.Replace('\\', '/').Split('/').Contains(unityVersion, StringComparer.Ordinal);

    public int Method(string name)
    {
        if (!_methods.TryGetValue(name, out var entries)) return -1;
        if (entries.Length != 1) throw new InvalidDataException($"Ambiguous method: {name} ({entries.Length} distinct bodies).");
        return entries[0];
    }

    public int MethodEnd(int start)
    {
        int i = Array.BinarySearch(_starts, start);
        return i >= 0 && i + 1 < _starts.Length ? _starts[i + 1] : Binary.Length;
    }

    public VersionProfile ResolveProfile(IReadOnlyCollection<VersionProfile> profiles, out Version gameVersion)
    {
        var matches = new List<(VersionProfile Profile, Version Version)>();
        foreach (var profile in profiles.Where(p => LauncherMatches(p, UnityVersion)))
        {
            var definition = profile.Methods.GameVersionMethod ?? throw new InvalidDataException("Missing game version method.");
            int start = Method(definition.Name);
            if (start < 0) throw new InvalidDataException("Required game version method is missing.");
            Version? version = definition.AutoFindOffsets ? ReadDynamicVersion(start) : ReadFixedVersion(start, definition);
            Console.WriteLine($"Version probe {definition.Name} at 0x{start:X}..0x{MethodEnd(start):X}: {version?.ToString() ?? "not recognized"}.");
            if (version != null && profile.Contains(version)) matches.Add((profile, version));
        }
        if (matches.Count != 1) throw new InvalidDataException($"Game version selection is missing or ambiguous ({matches.Count} matching profiles).");
        gameVersion = matches[0].Version;
        return matches[0].Profile;
    }

    private Version? ReadFixedVersion(int start, VersionMethod definition)
    {
        int end = Math.Min(MethodEnd(start), start + 4096);
        int[] offsets = [definition.MajorOffset, definition.MinorOffset, definition.RevisionOffset, definition.TypeOffset];
        if (offsets.Any(o => o < 0 || (long)start + o >= end)) return null;
        if (Binary[start + definition.TypeOffset] > 8) return null;
        return new Version(Binary[start + definition.MajorOffset], Binary[start + definition.MinorOffset], Binary[start + definition.RevisionOffset]);
    }

    private Version? ReadDynamicVersion(int start)
    {
        int end = Math.Min(MethodEnd(start), start + 4096);
        ReadOnlySpan<byte> marker = [0x48, 0x8B, 0x88, 0xB8, 0, 0, 0];
        var candidates = new HashSet<Version>();
        for (int cursor = start; cursor <= end - marker.Length; cursor++)
        {
            if (!Binary.AsSpan(cursor, marker.Length).SequenceEqual(marker) || !PatchSafety.IsBoundary(Binary, start, cursor, end)) continue;
            var values = new byte[4];
            int search = cursor + marker.Length;
            bool valid = true;
            byte[] fields = [0, 1, 2, 4];
            for (int i = 0; i < fields.Length; i++)
            {
                byte[] pattern = fields[i] == 0 ? [0xC6, 0x01] : [0xC6, 0x41, fields[i]];
                bool found = false;
                for (; search + pattern.Length < end && search < cursor + 256; search++)
                {
                    if (!Binary.AsSpan(search, pattern.Length).SequenceEqual(pattern) || !PatchSafety.IsBoundary(Binary, start, search, end)) continue;
                    values[i] = Binary[search + pattern.Length];
                    search += pattern.Length + 1;
                    found = true;
                    break;
                }
                if (!found) { valid = false; break; }
            }
            if (valid && values[0] > 0 && values[3] <= 8) candidates.Add(new Version(values[0], values[1], values[2]));
        }
        return candidates.Count == 1 ? candidates.Single() : null;
    }

    public void Dispose() { _binaryStream.Dispose(); _metadataStream.Dispose(); }
}
