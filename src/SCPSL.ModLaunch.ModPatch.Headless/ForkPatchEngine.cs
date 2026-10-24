using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SCPSL.ModLaunch.Core;
using SCPSL.ModLaunch.Core.Provisioning;

namespace SCPSL.ModLaunch.ModPatch.Headless;

/// <summary>Runs the fork's CLI. Patch selection and application belong to ModPatch.</summary>
internal static class ForkPatchEngine
{
    internal sealed record Prepared(Version GameVersion, string UnityVersion, int MetadataVersion,
        bool MetadataNormalizedForParser, BundleVersionCorrection BundleVersion, IReadOnlyList<PatchAttempt> Patches);

    internal static async Task<Prepared> PrepareAsync(string stage, string manifestPath, string revision)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "ModPatch");
        var bundle = ModPatchBundle.Read(root);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        string unity = ReadUnityVersion(Path.Combine(stage, "UnityPlayer.dll"));
        var targets = manifest.RootElement.GetProperty("versionRanges").EnumerateArray().ToArray();
        var launcherPaths = targets.Where(IsIl2CppProfile)
            .Select(profile => ModPatchBundle.LauncherPath(profile.GetProperty("cleanLauncherPath").GetString()!))
            .Where(path => path.Split('/')[1] == unity).Distinct(StringComparer.Ordinal).ToArray();
        if (launcherPaths.Length != 1) throw new InvalidDataException($"No unambiguous clean launcher for Unity {unity} in the pattern manifest.");
        string launcher;
        if (revision.StartsWith("bundled-", StringComparison.Ordinal))
        {
            string relative = launcherPaths[0];
            if (!bundle.Files.Any(file => file.RelativePath == relative))
                throw new InvalidDataException($"The bundled pattern resources lack a clean launcher for Unity {unity}.");
            launcher = FileOps.SafePath(root, relative);
        }
        else
        {
            if (revision.Length != 40 || !revision.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid pattern revision.");
            launcher = ToolDownloads.Download($"https://raw.githubusercontent.com/{ModPatchBundle.Repository}/{revision}/Shared_Files/{launcherPaths[0]}");
        }
        ValidateCleanLauncher(launcher);
        string launcherHash = FileOps.Hash(launcher);
        var originals = new[] { "SCPSL.exe", "GameAssembly.dll", "UnityPlayer.dll", "SCPSL_Data/il2cpp_data/Metadata/global-metadata.dat", GameFileContract.BundleVersionPath }
            .ToDictionary(path => path, path => FileOps.Hash(FileOps.SafePath(stage, path)), StringComparer.OrdinalIgnoreCase);
        if (launcherHash.Equals(originals["SCPSL.exe"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SCPSL.exe already matches the clean launcher. Verify original game files in Steam before preparation.");
        string reportPath = Path.Combine(Path.GetDirectoryName(stage)!, "modpatch-cli-result.json");
        if (File.Exists(reportPath)) File.Delete(reportPath);
        var runtime = HostRuntime.Resolve();
        Console.WriteLine($"Using {ModPatchBundle.Repository}, source {bundle.SourceSha256}, pattern resources {revision}.");
        await ToolRunner.RunAsync(FileOps.SafePath(root, ModPatchBundle.Executable),
            ["patch", "--game-dir", stage, "--patch-info", manifestPath, "--clean-launcher", launcher,
             "--metadata-mode", "preserve", "--bundle-version-mode", "correct", "--result", reportPath], stage, runtime.DotnetRoot,
            Console.WriteLine, TimeSpan.FromMinutes(10), CancellationToken.None);
        using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        return ValidateResult(report.RootElement, manifest.RootElement, stage, unity, launcherHash, originals);
    }

    internal static Prepared ValidateResult(JsonElement report, JsonElement manifest, string stage, string unity,
        string launcherHash, IReadOnlyDictionary<string, string> originals)
    {
        if (report.GetProperty("schemaVersion").GetInt32() != 1 || !report.GetProperty("success").GetBoolean() ||
            report.GetProperty("exitCode").GetInt32() != 0 || report.GetProperty("command").GetString() != "patch" ||
            report.GetProperty("stage").GetString() != "complete" || report.GetProperty("metadataMode").GetString() != "preserve" ||
            report.GetProperty("unityVersion").GetString() != unity || report.GetProperty("warnings").GetArrayLength() != 0 ||
            report.GetProperty("error").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("ModPatch returned an incomplete, incompatible, or unsuccessful result.");
        if (!Version.TryParse(report.GetProperty("gameVersion").GetString(), out var version) || version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException("ModPatch did not identify a numeric game version.");
        if (ReadString(report, "bundleVersionMode") != "correct" ||
            !report.TryGetProperty("bundleVersion", out var bundleReport) || bundleReport.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("ModPatch did not report the required Unity bundle-version correction.");
        string bundlePath = FileOps.SafePath(stage, GameFileContract.BundleVersionPath);
        string? reportedBundlePath = ReadString(bundleReport, "path");
        string? beforeBundleVersion = ReadString(bundleReport, "before");
        string? afterBundleVersion = ReadString(bundleReport, "after");
        string? bundleStatus = ReadString(bundleReport, "status");
        if (string.IsNullOrWhiteSpace(reportedBundlePath) || !Path.IsPathFullyQualified(reportedBundlePath) ||
            !bundlePath.Equals(Path.GetFullPath(reportedBundlePath), StringComparison.OrdinalIgnoreCase) ||
            beforeBundleVersion is null || afterBundleVersion is null || bundleStatus is null)
            throw new InvalidDataException("ModPatch returned an invalid Unity bundle-version correction.");
        string bundleHash = FileOps.Hash(bundlePath);
        var bundleVersion = new BundleVersionCorrection(beforeBundleVersion, afterBundleVersion, bundleStatus, bundleHash);
        bool bundleChanged = !bundleHash.Equals(originals[GameFileContract.BundleVersionPath], StringComparison.OrdinalIgnoreCase);
        if (!bundleVersion.Matches(version.ToString(), bundleHash) || bundleChanged != (bundleStatus == "applied"))
            throw new InvalidDataException("ModPatch's Unity bundle-version outcome does not match the detected version or file change.");
        var targets = manifest.GetProperty("versionRanges").EnumerateArray().ToArray();
        int index = report.GetProperty("profileIndex").GetInt32();
        if (index < 0 || index >= targets.Length) throw new InvalidDataException("ModPatch returned an invalid profile index.");
        var target = targets[index];
        if (!IsIl2CppProfile(target) ||
            ModPatchBundle.LauncherPath(target.GetProperty("cleanLauncherPath").GetString()!).Split('/')[1] != unity ||
            target.GetProperty("metadataVersion").GetInt32() != report.GetProperty("metadataVersion").GetInt32())
            throw new InvalidDataException("ModPatch selected a profile for another Unity or metadata version.");
        var definitions = target.GetProperty("methods").GetProperty("patchMethods").EnumerateArray().ToArray();
        var outcomes = report.GetProperty("patches").EnumerateArray().ToArray();
        if (outcomes.Length != definitions.Length || outcomes.Length == 0) throw new InvalidDataException("ModPatch omitted patch outcomes.");
        var patches = new List<PatchAttempt>();
        for (int i = 0; i < definitions.Length; i++)
        {
            string name = definitions[i].GetProperty("name").GetString()!;
            if (outcomes[i].GetProperty("name").GetString() != name) throw new InvalidDataException("ModPatch patch outcome order disagrees with its manifest.");
            string? status = outcomes[i].GetProperty("status").GetString();
            string mapped;
            if (status == "applied") mapped = "applied";
            else if (status == "skipped-fallback" && definitions[i].TryGetProperty("patchifnotexists", out var condition) &&
                !string.IsNullOrWhiteSpace(condition.GetString())) mapped = "alternative-not-needed";
            else if (status == "skipped-optional" && definitions[i].TryGetProperty("nowarning", out var optional) && optional.GetBoolean() &&
                definitions.Where((_, j) => outcomes[j].GetProperty("status").GetString() == "applied")
                    .Any(other => other.TryGetProperty("patchifnotexists", out var fallback) && fallback.GetString() == name)) mapped = "alternative-selected";
            else throw new InvalidDataException($"Required ModPatch patch was not applied: {name} ({status}).");
            patches.Add(new PatchAttempt(name, mapped, outcomes[i].GetProperty("message").GetString()));
        }
        if (!patches.Any(patch => patch.Status == "applied")) throw new InvalidDataException("ModPatch applied no required patches.");
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in report.GetProperty("modifiedFiles").EnumerateArray())
        {
            string absolute = Path.GetFullPath(file.GetProperty("path").GetString()!);
            string relative = Path.GetRelativePath(stage, absolute).Replace('\\', '/');
            if (relative is not "SCPSL.exe" and not "GameAssembly.dll" and not GameFileContract.BundleVersionPath || !changed.Add(relative) ||
                !FileOps.SafePath(stage, relative).Equals(absolute, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(file.GetProperty("beforeSha256").GetString(), originals[relative], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(file.GetProperty("afterSha256").GetString(), FileOps.Hash(absolute), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.GetProperty("beforeSha256").GetString(), file.GetProperty("afterSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ModPatch reported an unexpected or unverified file change: {relative}.");
        }
        if (!changed.Contains("SCPSL.exe") || !changed.Contains("GameAssembly.dll") ||
            changed.Contains(GameFileContract.BundleVersionPath) != bundleChanged || changed.Count != (bundleChanged ? 3 : 2) ||
            !FileOps.Hash(Path.Combine(stage, "SCPSL.exe")).Equals(launcherHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ModPatch did not produce all expected game outputs.");
        foreach (var original in originals.Where(file => file.Key is not "SCPSL.exe" and not "GameAssembly.dll" &&
                     (file.Key != GameFileContract.BundleVersionPath || !bundleChanged)))
            if (!FileOps.Hash(FileOps.SafePath(stage, original.Key)).Equals(original.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ModPatch changed an input that must remain original: {original.Key}.");
        patches.Add(new PatchAttempt(GameFileContract.BundleVersionPatchName, bundleStatus,
            $"{beforeBundleVersion} -> {afterBundleVersion} in {GameFileContract.BundleVersionPath} (ModPatch)"));
        return new Prepared(version, unity, report.GetProperty("metadataVersion").GetInt32(),
            report.GetProperty("metadataNormalizedForParser").GetBoolean(), bundleVersion, patches);
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string ReadUnityVersion(string path)
    {
        string value = Regex.Match(FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "", @"^\d+\.\d+\.\d+[abfp]\d+").Value;
        return value.Length > 0 ? value : throw new InvalidDataException("UnityPlayer.dll has no supported Unity identity.");
    }

    private static bool IsIl2CppProfile(JsonElement profile) =>
        !profile.TryGetProperty("backend", out var backend) || backend.ValueKind == JsonValueKind.Null ||
        string.Equals(backend.GetString(), "il2cpp", StringComparison.OrdinalIgnoreCase);

    private static void ValidateCleanLauncher(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64 ||
            pe.PEHeaders.PEHeader?.Subsystem != System.Reflection.PortableExecutable.Subsystem.WindowsGui ||
            pe.PEHeaders.CoffHeader.Characteristics.HasFlag(System.Reflection.PortableExecutable.Characteristics.Dll))
            throw new InvalidDataException("The clean launcher is not a Windows x64 GUI executable.");
    }
}
