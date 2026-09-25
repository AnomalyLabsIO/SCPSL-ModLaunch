using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            var patchInfo = PatchInfo.Read(patchPath);
            using var game = new StaticGame(staging, patchInfo.VersionRanges);
            var profile = game.ResolveProfile(patchInfo.VersionRanges, out var gameVersion);
            result.GameVersion = gameVersion.ToString();
            result.UnityVersion = game.UnityVersion;
            Console.WriteLine($"Preparing SCP:SL {gameVersion}, Unity {game.UnityVersion} with {profile.VersionRange}.");
            ApplyPatches(game, profile, result);

            string launcherUrl = profile.CleanLauncherUrl;
            if (!Regex.IsMatch(launcherUrl, @"^https://raw\.githubusercontent\.com/hopperlopip/SCPSL-ModPatch/[0-9a-fA-F]{40}/Shared_Files/clean_launchers/[^/]+/SCPSL\.exe$"))
                throw new InvalidDataException("Clean launcher URL must be pinned to an upstream commit by the launcher.");
            if (!new Uri(launcherUrl).Segments.Contains(game.UnityVersion + "/", StringComparer.Ordinal))
                throw new InvalidDataException("Clean launcher URL does not match UnityPlayer.dll identity.");
            string? knownLauncherHash = launcherUrl is
                "https://raw.githubusercontent.com/hopperlopip/SCPSL-ModPatch/374b0f578bb07373c4ec0ce2f91fc2d55b7216ca/Shared_Files/clean_launchers/6000.0.43f1/SCPSL.exe" or
                "https://raw.githubusercontent.com/hopperlopip/SCPSL-ModPatch/1786a7f4d6ff8d1c7f3e4d1aefe48484334ab718/Shared_Files/clean_launchers/6000.0.43f1/SCPSL.exe"
                ? "34E3F55AF5DA555873B52ECF3AA9547680BC761DCD2022E22283D90ACDCFC64C" : null;
            string cleanLauncher = ToolDownloads.Download(launcherUrl, knownLauncherHash);
            ValidateCleanLauncher(cleanLauncher, game.UnityVersion);
            if (ToolDownloads.Hash(cleanLauncher) == ToolDownloads.Hash(Path.Combine(staging, "SCPSL.exe")))
                throw new InvalidDataException("SCPSL.exe already matches the modded clean launcher. Verify original game files in Steam before preparation.");
            await File.WriteAllBytesAsync(Path.Combine(staging, "GameAssembly.dll"), game.Binary);
            File.Copy(cleanLauncher, Path.Combine(staging, "SCPSL.exe"), true);
            string bundlePath = Path.Combine(staging, UnityBundleVersion.RelativePath);
            string previousBundleVersion = UnityBundleVersion.Rewrite(bundlePath, gameVersion);
            result.Patches.Add(new PatchAttempt(UnityBundleVersion.PatchName,
                previousBundleVersion == result.GameVersion ? "already-correct" : "applied",
                $"{previousBundleVersion} -> {result.GameVersion} in {UnityBundleVersion.RelativePath}"));
            Console.WriteLine($"Unity bundle version: {previousBundleVersion} -> {result.GameVersion}.");
            if (game.NeedsInteropGeneration)
            {
                Console.WriteLine("Runtime metadata requires a parser-only normalization; generating compatible MelonLoader assemblies.");
                result.InteropGenerated = MelonInteropAssemblyGenerator.TryGenerate(staging,
                    Path.Combine(staging, "GameAssembly.dll"), metadataPath, game.UnityVersion, game.ParserMetadataVersion, CancellationToken.None);
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

    private static void ApplyPatches(StaticGame game, VersionProfile profile, PatchResult result)
    {
        if (profile.Methods.PatchMethods.Count == 0) throw new InvalidDataException("Profile has no required patches.");
        var pending = new List<(int Offset, byte[] Bytes)>();
        foreach (var patch in profile.Methods.PatchMethods)
        {
            byte[] replacement = PatchSafety.ParseBytes(patch); // Even skipped alternatives must use supported syntax.
            if (!string.IsNullOrEmpty(patch.Patchifnotexists) && game.Method(patch.Patchifnotexists) >= 0)
            {
                result.Patches.Add(new PatchAttempt(patch.Name, "alternative-not-needed", patch.Patchifnotexists));
                continue;
            }
            int method = game.Method(patch.Name);
            if (method < 0)
            {
                // Upstream nowarning is only acceptable when another explicit conditional
                // alternative covers this missing method. A bare missing patch is never skipped.
                if (patch.Nowarning && profile.Methods.PatchMethods.Any(p => p.Patchifnotexists == patch.Name && game.Method(p.Name) >= 0))
                {
                    result.Patches.Add(new PatchAttempt(patch.Name, "alternative-selected"));
                    continue;
                }
                throw new InvalidDataException($"Required patch method is missing: {patch.Name}.");
            }
            PatchSafety.Validate(game.Binary, method, game.MethodEnd(method), patch, replacement);
            int offset = checked(method + patch.PatchOffset);
            if (game.Binary.AsSpan(offset, replacement.Length).SequenceEqual(replacement))
                throw new InvalidDataException($"Required patch {patch.Name} is already applied. Verify original game files in Steam before preparation.");
            if (pending.Any(p => offset < p.Offset + p.Bytes.Length && p.Offset < offset + replacement.Length))
                throw new InvalidDataException($"Overlapping patches for {patch.Name}.");
            pending.Add((offset, replacement));
            result.Patches.Add(new PatchAttempt(patch.Name, "applied", $"file offset 0x{offset:X}, {replacement.Length} bytes"));
        }
        foreach (var patch in pending) patch.Bytes.CopyTo(game.Binary, patch.Offset);
    }

    private static void ValidateCleanLauncher(string path, string unityVersion)
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64
            || pe.PEHeaders.PEHeader?.Subsystem != System.Reflection.PortableExecutable.Subsystem.WindowsGui
            || pe.PEHeaders.CoffHeader.Characteristics.HasFlag(System.Reflection.PortableExecutable.Characteristics.Dll))
            throw new InvalidDataException("Downloaded clean launcher is not a Windows x64 GUI executable.");
        string product = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
        // The upstream profile explicitly pairs its launcher path with the target Unity build.
        // The launcher stub's PE product version can differ (the 6000.0.43f1 profile ships a
        // 6000.0.42f1 stub); identity comes from the pinned profile pairing and content hash.
        Console.WriteLine($"Clean launcher for Unity {unityVersion}: PE product '{product}', SHA-256 {ToolDownloads.Hash(path)}.");
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
