using System.Runtime.InteropServices;

namespace SCPSL.ModLaunch.Core;

public sealed record HostRuntimeInfo(string DotnetRoot, string HostFxrPath, Version Version);

/// <summary>Locates the Windows x64 .NET 6 Desktop prerequisite without downloading another runtime.</summary>
public static class HostRuntime
{
    private static readonly Version MinimumRuntimeVersion = new(6, 0, 36);

    public static HostRuntimeInfo Resolve()
    {
        string? current = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar))?.Parent?.Parent?.FullName;
        var candidates = new List<string?>
        {
            current, Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"), Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
        };
        if (OperatingSystem.IsWindows())
            candidates.Add(Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\dotnet\Setup\InstalledVersions\x64", "InstallLocation", null) as string);
        // Prefer a host installation over a runtime retained by an older ModLaunch release.
        foreach (string root in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x.Replace('\\', '/').Contains("/.modlaunch/", StringComparison.OrdinalIgnoreCase)))
        {
            try { return ResolveFromRoot(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
        }
        throw new InvalidOperationException("Windows x64 .NET 6 Desktop Runtime 6.0.36 or later is required. Install it in this Windows environment (inside the Proton prefix on Linux), then retry.");
    }

    public static HostRuntimeInfo ResolveFromRoot(string root)
    {
        root = Path.GetFullPath(root);
        string runtimeBase = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        string desktopBase = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
        string fxrBase = Path.Combine(root, "host", "fxr");
        if (!File.Exists(Path.Combine(root, "dotnet.exe")) || !Directory.Exists(runtimeBase) ||
            !Directory.Exists(desktopBase) || !Directory.Exists(fxrBase))
            throw new InvalidDataException("The selected directory is not a complete Windows .NET installation.");
        var runtime = FindVersion(runtimeBase, "coreclr.dll", MinimumRuntimeVersion);
        var desktop = FindVersion(desktopBase, "System.Windows.Forms.dll", MinimumRuntimeVersion);
        var fxr = FindVersion(fxrBase, "hostfxr.dll", new Version(6, 0));
        foreach (string file in new[] { "coreclr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll" })
            if (!File.Exists(Path.Combine(runtime.Path, file))) throw new InvalidDataException("The selected .NET 6 runtime is incomplete.");
        foreach (string file in new[] { "System.Windows.Forms.Primitives.dll", "Microsoft.WindowsDesktop.App.deps.json",
                     "Microsoft.WindowsDesktop.App.runtimeconfig.json", "wpfgfx_cor3.dll" })
            if (!File.Exists(Path.Combine(desktop.Path, file))) throw new InvalidDataException("The selected .NET 6 Desktop runtime is incomplete.");
        RequireX64(Path.Combine(runtime.Path, "coreclr.dll"));
        RequireX64(Path.Combine(desktop.Path, "wpfgfx_cor3.dll"));
        RequireX64(Path.Combine(fxr.Path, "hostfxr.dll"));
        return new HostRuntimeInfo(root, Path.Combine(fxr.Path, "hostfxr.dll"), runtime.Version);
    }

    private static (string Path, Version Version) FindVersion(string parent, string required, Version minimum)
    {
        var match = Directory.EnumerateDirectories(parent)
            .Select(p => (Path: p, Version: Version.TryParse(Path.GetFileName(p), out var v) ? v : null))
            .Where(x => x.Version is { Major: 6, Minor: 0 } && x.Version >= minimum && File.Exists(Path.Combine(x.Path, required)))
            .OrderByDescending(x => x.Version).FirstOrDefault();
        return match.Version is null ? throw new InvalidDataException($"Windows .NET 6 {Path.GetFileName(parent)} {minimum} or later was not found.") : (match.Path, match.Version);
    }

    private static void RequireX64(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("The selected runtime is not a Windows PE image.");
        stream.Position = 0x3c;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 6) throw new InvalidDataException("Invalid runtime PE header.");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664) throw new InvalidDataException("The selected Windows runtime must be x64.");
    }
}
