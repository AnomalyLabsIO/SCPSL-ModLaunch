using System.Text.Json;
using SCPSL.ModLaunch.Core;

namespace SCPSL.ModLaunch.ModPatch.Headless;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var result = new PatchResult();
        string? resultPath = null;
        try
        {
            var options = ParseArguments(args);
            string staging = Path.GetFullPath(Required(options, "--game-dir"));
            string patchPath = Path.GetFullPath(Required(options, "--patch-info"));
            resultPath = Path.GetFullPath(Required(options, "--result"));
            result.PatchRevision = options.GetValueOrDefault("--patch-revision", "");
            ToolDownloads.CacheDirectory = Path.GetFullPath(options.GetValueOrDefault("--cache-dir", Path.Combine(staging, ".adapter-work", "downloads")));
            foreach (string relative in new[] { "SCPSL.exe", "GameAssembly.dll", "UnityPlayer.dll", "SCPSL_Data/il2cpp_data/Metadata/global-metadata.dat", UnityBundleVersion.RelativePath })
                RequireRegularFile(staging, relative);
            string metadataPath = Path.Combine(staging, "SCPSL_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            string originalMetadataHash = ToolDownloads.Hash(metadataPath);
            var game = await ForkPatchEngine.PrepareAsync(staging, patchPath, result.PatchRevision);
            var gameVersion = game.GameVersion;
            result.GameVersion = gameVersion.ToString();
            result.UnityVersion = game.UnityVersion;
            result.Patches.AddRange(game.Patches);
            Console.WriteLine($"Prepared SCP:SL {gameVersion}, Unity {game.UnityVersion} with the ModPatch pattern engine.");
            string bundlePath = Path.Combine(staging, UnityBundleVersion.RelativePath);
            string previousBundleVersion = UnityBundleVersion.Rewrite(bundlePath, gameVersion);
            result.Patches.Add(new PatchAttempt(UnityBundleVersion.PatchName,
                previousBundleVersion == result.GameVersion ? "already-correct" : "applied",
                $"{previousBundleVersion} -> {result.GameVersion} in {UnityBundleVersion.RelativePath}"));
            Console.WriteLine($"Unity bundle version: {previousBundleVersion} -> {result.GameVersion}.");
            if (game.MetadataNormalizedForParser)
            {
                Console.WriteLine("Runtime metadata requires a parser-only normalization; generating compatible MelonLoader assemblies.");
                result.InteropGenerated = MelonInteropAssemblyGenerator.TryGenerate(staging,
                    Path.Combine(staging, "GameAssembly.dll"), metadataPath, game.UnityVersion, game.MetadataVersion, CancellationToken.None);
            }
            if (!ToolDownloads.Hash(metadataPath).Equals(originalMetadataHash, StringComparison.Ordinal))
                throw new InvalidDataException("Runtime metadata changed unexpectedly; staging is invalid.");
            result.Files.Add(new PreparedFile("SCPSL.exe", ToolDownloads.Hash(Path.Combine(staging, "SCPSL.exe"))));
            result.Files.Add(new PreparedFile("GameAssembly.dll", ToolDownloads.Hash(Path.Combine(staging, "GameAssembly.dll"))));
            result.Files.Add(new PreparedFile(UnityBundleVersion.RelativePath, ToolDownloads.Hash(bundlePath)));
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, JsonDefaults.Options));
            Console.WriteLine($"Preparation complete: {result.Files.Count} output files; runtime metadata unchanged.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ModPatch preparation failed: {ex}");
            result.Patches.Add(new PatchAttempt("prepare", "failed", ex.Message));
            result.Files.Clear();
            if (resultPath != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
                    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, JsonDefaults.Options));
                }
                catch (Exception reportError) { Console.Error.WriteLine($"Could not write failure report: {reportError.Message}"); }
            }
            return 1;
        }
    }

    private static void RequireRegularFile(string staging, string relative)
    {
        string path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new FileNotFoundException($"Staging input missing: {relative}.");
        for (var entry = new FileInfo(path) as FileSystemInfo; entry != null; entry = Directory.GetParent(entry.FullName))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Staging paths must not contain symbolic links: {entry.FullName}.");
            if (Path.GetFullPath(entry.FullName).TrimEnd(Path.DirectorySeparatorChar).Equals(staging.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        if (args.Length == 0 || args[0] != "prepare" || args.Length % 2 != 1)
            throw new ArgumentException("Usage: prepare --game-dir <staging> --patch-info <json> --result <json> [--patch-revision <sha>] [--cache-dir <path>]");
        string[] known = ["--game-dir", "--patch-info", "--result", "--patch-revision", "--cache-dir"];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
            if (!known.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException($"Unknown or repeated argument: {args[i]}.");
        return options;
    }

    private static string Required(Dictionary<string, string> options, string name) => options.TryGetValue(name, out var value)
        && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing {name}.");
}
