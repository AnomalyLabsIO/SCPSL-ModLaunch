using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SCPSL.ModLaunch;

/// <summary>Keeps OS version queries away from a game's active version.dll loader proxy.</summary>
internal static class NativeSystemLibraries
{
    private static readonly object Gate = new();
    private static bool initialized;
    // Retain the system module until process exit: its exports back cached P/Invoke stubs.
    private static IntPtr versionModule;

    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (initialized) return;
            string path = Path.Combine(Environment.SystemDirectory, "version.dll");
            versionModule = NativeLibrary.Load(path);
            // FileVersionInfo's native imports live in its framework assembly, not Core.
            // Resolve the exact system path before that assembly's first version query.
            NativeLibrary.SetDllImportResolver(typeof(FileVersionInfo).Assembly, Resolve);
            initialized = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName.Equals("version.dll", StringComparison.OrdinalIgnoreCase) ? versionModule : IntPtr.Zero;
}
