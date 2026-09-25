using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;



namespace SCPSL.ModLaunch.ModPatch.Headless;

/// <summary>
/// Generates the MelonLoader Il2Cpp interop assemblies without launching the game. This is
/// needed for Unity builds whose runtime metadata header intentionally differs from the parser
/// layout (Unity 6000 builds ship v23-on-v31 metadata): Unity must keep the shipped v23 file on
/// disk, while Cpp2IL needs a temporary parser-normalized copy.
/// </summary>
public static class MelonInteropAssemblyGenerator
{
    private const string Cpp2IlVersion = "2022.1.0-pre-release.21";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(5);

    public static bool TryGenerate(
        string gameDir,
        string gameAssemblyPath,
        string metadataPath,
        string unityVersion,
        int metadataHeaderVersionForParser,
        CancellationToken ct)
    {
        string generatorDir = Path.Combine(gameDir, "MelonLoader", "Dependencies", "Il2CppAssemblyGenerator");
        string net6Dir = Path.Combine(gameDir, "MelonLoader", "net6");
        string cpp2IlDir = Path.Combine(generatorDir, "Cpp2IL");
        string cpp2IlPath = Path.Combine(cpp2IlDir, "Cpp2IL.exe");
        string cpp2IlOutputDir = Path.Combine(cpp2IlDir, "cpp2il_out");
        string interopOutputDir = Path.Combine(generatorDir, "Il2CppInterop", "Il2CppAssemblies");
        string finalAssembliesDir = Path.Combine(gameDir, "MelonLoader", "Il2CppAssemblies");

        if (!File.Exists(gameAssemblyPath))
            throw new InvalidDataException($"GameAssembly.dll not found at {gameAssemblyPath}.");
        if (!File.Exists(metadataPath))
            throw new InvalidDataException($"global-metadata.dat not found at {metadataPath}.");
        if (!Directory.Exists(net6Dir))
            throw new InvalidDataException($"MelonLoader net6 directory not found at {net6Dir}.");

        string unityDependencyVersion = ToUnityDependencyVersion(unityVersion);
        EnsureCpp2IlPackages(generatorDir, cpp2IlPath, unityDependencyVersion, ct);

        Directory.CreateDirectory(generatorDir);
        string tempDir = Path.Combine(generatorDir, "ModLaunchTemp");
        Directory.CreateDirectory(tempDir);
        string parserMetadataPath = Path.Combine(tempDir, $"global-metadata-v{metadataHeaderVersionForParser}.dat");

        try
        {
            byte[] normalizedMetadata = MetadataHeaderNormalizer.NormalizeBytesForParser(
                File.ReadAllBytes(metadataPath),
                metadataHeaderVersionForParser);
            File.WriteAllBytes(parserMetadataPath, normalizedMetadata);

            CleanDirectory(cpp2IlOutputDir);
            CleanDirectory(interopOutputDir);

            Console.WriteLine("  Running Cpp2IL with parser-normalized metadata...");
            RunCpp2Il(cpp2IlPath, cpp2IlOutputDir, gameAssemblyPath, parserMetadataPath, unityVersion, ct);

            int dummyAssemblyCount = Directory.Exists(cpp2IlOutputDir)
                ? Directory.GetFiles(cpp2IlOutputDir, "*.dll", SearchOption.TopDirectoryOnly).Length
                : 0;
            if (dummyAssemblyCount == 0)
                throw new InvalidDataException("Cpp2IL completed but produced no dummy assemblies.");
            Console.WriteLine($"  Cpp2IL generated {dummyAssemblyCount} dummy assembly file(s).");

            Console.WriteLine("  Running Il2CppInterop generator...");
            RunIl2CppInteropGenerator(gameDir, net6Dir, cpp2IlOutputDir, interopOutputDir, gameAssemblyPath);

            string[] generatedFiles = Directory.GetFiles(interopOutputDir, "*", SearchOption.TopDirectoryOnly);
            if (generatedFiles.Length == 0)
                throw new InvalidDataException("Il2CppInterop generator completed but produced no assemblies.");

            CleanDirectory(finalAssembliesDir);
            Directory.CreateDirectory(finalAssembliesDir);
            foreach (string file in generatedFiles)
            {
                File.Move(file, Path.Combine(finalAssembliesDir, Path.GetFileName(file)), overwrite: true);
            }

            WriteGeneratorConfig(
                gameDir,
                ComputeSha512Hex(gameAssemblyPath),
                unityDependencyVersion,
                generatedFiles.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>());

            Console.WriteLine($"  Generated {generatedFiles.Length} MelonLoader Il2Cpp assembly file(s).");
            return true;
        }
        finally
        {
            TryDeleteFile(parserMetadataPath);
        }
    }

    private static void EnsureCpp2IlPackages(string generatorDir, string cpp2IlPath, string unityDependencyVersion, CancellationToken ct)
    {
        string cpp2IlDir = Path.GetDirectoryName(cpp2IlPath)!;
        Directory.CreateDirectory(cpp2IlDir);

        string cpp2IlUrl = $"https://github.com/SamboyCoding/Cpp2IL/releases/download/{Cpp2IlVersion}/Cpp2IL-{Cpp2IlVersion}-Windows.exe";
        EnsureDownloadedFile("Cpp2IL", cpp2IlUrl, cpp2IlPath, ct, "663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c");

        string pluginDir = Path.Combine(cpp2IlDir, "Plugins");
        Directory.CreateDirectory(pluginDir);
        string pluginName = "Cpp2IL.Plugin.StrippedCodeRegSupport.dll";
        string pluginUrl = $"https://github.com/SamboyCoding/Cpp2IL/releases/download/{Cpp2IlVersion}/{pluginName}";
        EnsureDownloadedFile("Cpp2IL StrippedCodeRegSupport plugin", pluginUrl, Path.Combine(pluginDir, pluginName), ct, "2cc4f8c66541f18b4daed5410149ee7859c3190454d30bda5ccd0895d2989ee6");

        string unityDependenciesDir = Path.Combine(generatorDir, "UnityDependencies");
        string zipPath = Path.Combine(generatorDir, $"UnityDependencies_{unityDependencyVersion}.zip");
        string unityUrl = $"https://github.com/LavaGang/MelonLoader.UnityDependencies/releases/download/{unityDependencyVersion}/Managed.zip";
        EnsureDownloadedFile("Unity dependencies", unityUrl, zipPath, ct,
            ToolDownloads.PublishedDigest("LavaGang/MelonLoader.UnityDependencies", unityDependencyVersion, "Managed.zip", ct));

        if (Directory.Exists(unityDependenciesDir))
            Directory.Delete(unityDependenciesDir, recursive: true);
        Directory.CreateDirectory(unityDependenciesDir);
        ZipFile.ExtractToDirectory(zipPath, unityDependenciesDir, overwriteFiles: true);
        TryDeleteFile(zipPath);
    }

    private static void EnsureDownloadedFile(string label, string url, string destPath, CancellationToken ct, string? sha256 = null)
    {
        Console.WriteLine($"  Resolving cached {label}...");
        string cached = ToolDownloads.Download(url, sha256, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.Copy(cached, destPath, overwrite: true);
    }

    private static void RunCpp2Il(
        string cpp2IlPath,
        string outputDir,
        string gameAssemblyPath,
        string parserMetadataPath,
        string unityVersion,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cpp2IlPath,
            WorkingDirectory = Path.GetDirectoryName(cpp2IlPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(Path.GetDirectoryName(cpp2IlPath)!, "Cpp2IL_Temp");
        psi.ArgumentList.Add("--force-binary-path");
        psi.ArgumentList.Add(gameAssemblyPath);
        psi.ArgumentList.Add("--force-metadata-path");
        psi.ArgumentList.Add(parserMetadataPath);
        psi.ArgumentList.Add("--force-unity-version");
        psi.ArgumentList.Add(unityVersion);
        psi.ArgumentList.Add("--output-as");
        psi.ArgumentList.Add("dummydll");
        psi.ArgumentList.Add("--use-processor");
        psi.ArgumentList.Add("attributeanalyzer");
        psi.ArgumentList.Add("attributeinjector");
        psi.ArgumentList.Add("--output-to");
        psi.ArgumentList.Add(outputDir);

        RunProcess(psi, "Cpp2IL", ct);
    }

    private static void RunProcess(ProcessStartInfo psi, string label, CancellationToken ct)
    {
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new List<string>();
        object gate = new();

        proc.OutputDataReceived += (_, e) => CaptureToolLine(e.Data, output, gate);
        proc.ErrorDataReceived += (_, e) => CaptureToolLine(e.Data, output, gate);

        try
        {
            if (!proc.Start())
                throw new InvalidDataException($"Failed to start {label}.");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            DateTime deadline = DateTime.UtcNow + ToolTimeout;
            while (!proc.WaitForExit(500))
            {
                if (ct.IsCancellationRequested)
                {
                    TryKill(proc);
                    ct.ThrowIfCancellationRequested();
                }

                if (DateTime.UtcNow <= deadline)
                    continue;

                TryKill(proc);
                throw new InvalidDataException($"{label} did not finish within {ToolTimeout.TotalMinutes:0} minute(s).");
            }

            proc.WaitForExit();
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Failed to run {label}: {ex.Message}");
        }

        if (proc.ExitCode != 0)
        {
            string tail;
            lock (gate)
            {
                tail = string.Join(Environment.NewLine, output.TakeLast(25));
            }
            throw new InvalidDataException($"{label} exited with code {proc.ExitCode}. Last output:{Environment.NewLine}{tail}");
        }
    }

    private static void CaptureToolLine(string? line, List<string> output, object gate)
    {
        if (line == null)
            return;

        line = StripAnsi(line).TrimEnd();
        if (line.Length == 0)
            return;

        lock (gate)
        {
            output.Add(line);
            if (output.Count > 500)
                output.RemoveRange(0, output.Count - 500);
        }

        if (ShouldPrintToolLine(line))
            Console.WriteLine($"    {line}");
    }

    private static bool ShouldPrintToolLine(string line)
    {
        return line.Contains("Using actual IL2CPP Metadata version", StringComparison.Ordinal)
               || line.Contains("Got Binary codereg", StringComparison.Ordinal)
               || line.Contains("Finished outputting", StringComparison.Ordinal)
               || line.Contains("Done. Total execution time", StringComparison.Ordinal)
               || line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
               || line.Contains("[Error]", StringComparison.OrdinalIgnoreCase)
               || line.Contains("[Warn]", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripAnsi(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\u001b' && i + 1 < value.Length && value[i + 1] == '[')
            {
                i += 2;
                while (i < value.Length && !char.IsLetter(value[i]))
                    i++;
                continue;
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    private static void RunIl2CppInteropGenerator(
        string gameDir,
        string net6Dir,
        string cpp2IlOutputDir,
        string interopOutputDir,
        string gameAssemblyPath)
    {
        var alc = new AssemblyLoadContext($"SCPSL.ModLaunch.MelonInterop.{Guid.NewGuid():N}", isCollectible: true);
        alc.Resolving += (_, name) =>
        {
            string path = Path.Combine(net6Dir, name.Name + ".dll");
            return File.Exists(path) ? alc.LoadFromAssemblyPath(path) : null;
        };

        try
        {
            LoadIfExists(alc, Path.Combine(net6Dir, "AsmResolver.dll"));
            Assembly asmResolverDotNet = alc.LoadFromAssemblyPath(Path.Combine(net6Dir, "AsmResolver.DotNet.dll"));
            LoadIfExists(alc, Path.Combine(net6Dir, "AsmResolver.PE.dll"));
            LoadIfExists(alc, Path.Combine(net6Dir, "AsmResolver.PE.File.dll"));
            LoadIfExists(alc, Path.Combine(net6Dir, "Il2CppInterop.Common.dll"));
            Assembly generatorAssembly = alc.LoadFromAssemblyPath(Path.Combine(net6Dir, "Il2CppInterop.Generator.dll"));

            Type moduleDefinitionType = asmResolverDotNet.GetType("AsmResolver.DotNet.ModuleDefinition")
                ?? throw new InvalidDataException("AsmResolver.ModuleDefinition type not found.");
            Type assemblyDefinitionType = asmResolverDotNet.GetType("AsmResolver.DotNet.AssemblyDefinition")
                ?? throw new InvalidDataException("AsmResolver.AssemblyDefinition type not found.");

            object sourceAssemblies = LoadCpp2IlAssemblies(moduleDefinitionType, assemblyDefinitionType, cpp2IlOutputDir);
            object options = CreateGeneratorOptions(
                generatorAssembly,
                sourceAssemblies,
                interopOutputDir,
                Path.Combine(gameDir, "MelonLoader", "Dependencies", "Il2CppAssemblyGenerator", "UnityDependencies"),
                gameAssemblyPath);

            Type generatorType = generatorAssembly.GetType("Il2CppInterop.Generator.Il2CppInteropGenerator")
                ?? throw new InvalidDataException("Il2CppInteropGenerator type not found.");
            object generator = generatorType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { options })
                ?? throw new InvalidDataException("Could not create Il2CppInteropGenerator.");

            Type runnerExtensionType = generatorAssembly.GetType("Il2CppInterop.Generator.Runners.InteropAssemblyGenerator")
                ?? throw new InvalidDataException("InteropAssemblyGenerator extension type not found.");
            MethodInfo addInteropAssemblyGenerator = runnerExtensionType.GetMethod("AddInteropAssemblyGenerator", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidDataException("AddInteropAssemblyGenerator method not found.");
            addInteropAssemblyGenerator.Invoke(null, new[] { generator });

            try
            {
                MethodInfo run = generatorType.GetMethod("Run", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidDataException("Il2CppInteropGenerator.Run method not found.");
                run.Invoke(generator, null);
            }
            finally
            {
                if (generator is IDisposable disposable)
                    disposable.Dispose();
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw new InvalidDataException($"Il2CppInterop generation failed: {ex.InnerException.Message}");
        }
        finally
        {
            alc.Unload();
        }
    }

    private static void LoadIfExists(AssemblyLoadContext alc, string path)
    {
        if (File.Exists(path))
            alc.LoadFromAssemblyPath(path);
    }

    private static object LoadCpp2IlAssemblies(Type moduleDefinitionType, Type assemblyDefinitionType, string cpp2IlOutputDir)
    {
        Type sourceListType = typeof(List<>).MakeGenericType(assemblyDefinitionType);
        var sourceList = (IList)Activator.CreateInstance(sourceListType)!;
        MethodInfo fromFile = moduleDefinitionType.GetMethod("FromFile", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) })
            ?? throw new InvalidDataException("AsmResolver.ModuleDefinition.FromFile(string) not found.");
        PropertyInfo assemblyProperty = moduleDefinitionType.GetProperty("Assembly")
            ?? throw new InvalidDataException("AsmResolver.ModuleDefinition.Assembly property not found.");

        foreach (string dll in Directory.GetFiles(cpp2IlOutputDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            object module = fromFile.Invoke(null, new object[] { dll })
                ?? throw new InvalidDataException($"Could not read Cpp2IL dummy assembly {dll}.");
            object assembly = assemblyProperty.GetValue(module)
                ?? throw new InvalidDataException($"Cpp2IL dummy assembly {dll} had no assembly definition.");
            sourceList.Add(assembly);
        }

        return sourceList;
    }

    private static object CreateGeneratorOptions(
        Assembly generatorAssembly,
        object sourceAssemblies,
        string outputDir,
        string unityBaseLibsDir,
        string gameAssemblyPath)
    {
        Type optionsType = generatorAssembly.GetType("Il2CppInterop.Generator.GeneratorOptions")
            ?? throw new InvalidDataException("GeneratorOptions type not found.");
        object options = Activator.CreateInstance(optionsType)
            ?? throw new InvalidDataException("Could not create GeneratorOptions.");

        optionsType.GetProperty("GameAssemblyPath")?.SetValue(options, gameAssemblyPath);
        optionsType.GetProperty("Source")?.SetValue(options, sourceAssemblies);
        optionsType.GetProperty("OutputDir")?.SetValue(options, outputDir);
        optionsType.GetProperty("UnityBaseLibsDir")?.SetValue(options, unityBaseLibsDir);
        optionsType.GetProperty("Parallel")?.SetValue(options, true);

        Type prefixModeType = optionsType.GetNestedType("PrefixMode")
            ?? throw new InvalidDataException("GeneratorOptions.PrefixMode type not found.");
        object optOut = Enum.Parse(prefixModeType, "OptOut");
        optionsType.GetProperty("Il2CppPrefixMode")?.SetValue(options, optOut);

        return options;
    }

    private static void WriteGeneratorConfig(
        string gameDir,
        string gameAssemblyHash,
        string? unityDependencyVersion,
        IEnumerable<string> oldFiles)
    {
        string configPath = Path.Combine(gameDir, "MelonLoader", "Dependencies", "Il2CppAssemblyGenerator", "Config.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        var lines = File.Exists(configPath)
            ? File.ReadAllLines(configPath).ToList()
            : new List<string> { "[Il2CppAssemblyGenerator]" };

        if (!lines.Any(l => l.Trim().Equals("[Il2CppAssemblyGenerator]", StringComparison.Ordinal)))
            lines.Insert(0, "[Il2CppAssemblyGenerator]");

        UpsertConfigValue(lines, "GameAssemblyHash", Quote(gameAssemblyHash));
        if (!string.IsNullOrWhiteSpace(unityDependencyVersion))
            UpsertConfigValue(lines, "UnityVersion", Quote(unityDependencyVersion));
        UpsertConfigValue(lines, "DumperVersion", Quote(Cpp2IlVersion));
        UpsertConfigValue(lines, "DumperSCRSVersion", Quote(Cpp2IlVersion));
        UpsertConfigValue(lines, "OldFiles", "[ " + string.Join(", ", oldFiles.OrderBy(name => name, StringComparer.Ordinal).Select(Quote)) + " ]");

        File.WriteAllLines(configPath, lines);
    }

    private static void UpsertConfigValue(List<string> lines, string key, string value)
    {
        string prefix = key + " =";
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith(prefix, StringComparison.Ordinal))
                continue;

            lines[i] = $"{key} = {value}";
            return;
        }

        int headerIndex = lines.FindIndex(l => l.Trim().Equals("[Il2CppAssemblyGenerator]", StringComparison.Ordinal));
        lines.Insert(headerIndex >= 0 ? headerIndex + 1 : lines.Count, $"{key} = {value}");
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string ToUnityDependencyVersion(string unityVersion)
    {
        string value = new(unityVersion.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return value.TrimEnd('.');
    }

    private static void CleanDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    private static string ComputeSha512Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var algorithm = SHA512.Create();
        return Convert.ToHexString(algorithm.ComputeHash(stream));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
