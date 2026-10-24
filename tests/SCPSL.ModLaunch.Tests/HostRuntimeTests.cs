using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class HostRuntimeTests
{
    [Fact]
    public void SelectsNewestInstalledSixWithoutSelectingAnotherMajor()
    {
        using var game = new TestGame();
        string runtime = Path.Combine(game.Root, "runtime");
        WriteRuntime(runtime, "6.0.35");
        WriteRuntime(runtime, "6.0.36");
        WriteRuntime(runtime, "9.0.20");
        var result = HostRuntime.ResolveFromRoot(runtime);
        Assert.Equal(new Version(6, 0, 36), result.Version);
        Assert.Equal(Path.Combine(runtime, "host", "fxr", "6.0.36", "hostfxr.dll"), result.HostFxrPath);
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("missing-corelib")]
    [InlineData("linux")]
    [InlineData("missing-desktop-deps")]
    [InlineData("missing-desktop-config")]
    [InlineData("missing-desktop-primitives")]
    [InlineData("missing-desktop-native")]
    [InlineData("x86-desktop")]
    public void RejectsIncompatibleOrIncompleteWindowsRuntime(string condition)
    {
        using var game = new TestGame();
        string root = Path.Combine(game.Root, "runtime");
        WriteRuntime(root, "6.0.36");
        string clr = Path.Combine(root, "shared/Microsoft.NETCore.App/6.0.36/coreclr.dll");
        string desktop = Path.Combine(root, "shared/Microsoft.WindowsDesktop.App/6.0.36");
        if (condition == "missing-corelib") File.Delete(Path.Combine(root, "shared/Microsoft.NETCore.App/6.0.36/System.Private.CoreLib.dll"));
        else if (condition == "linux") File.WriteAllText(clr, "ELF");
        else if (condition == "missing-desktop-deps") File.Delete(Path.Combine(desktop, "Microsoft.WindowsDesktop.App.deps.json"));
        else if (condition == "missing-desktop-config") File.Delete(Path.Combine(desktop, "Microsoft.WindowsDesktop.App.runtimeconfig.json"));
        else if (condition == "missing-desktop-primitives") File.Delete(Path.Combine(desktop, "System.Windows.Forms.Primitives.dll"));
        else if (condition == "missing-desktop-native") File.Delete(Path.Combine(desktop, "wpfgfx_cor3.dll"));
        else if (condition == "x86-desktop")
        {
            string native = Path.Combine(desktop, "wpfgfx_cor3.dll");
            byte[] bytes = File.ReadAllBytes(native); bytes[84] = 0x4c; bytes[85] = 0x01; File.WriteAllBytes(native, bytes);
        }
        else { byte[] bytes = File.ReadAllBytes(clr); bytes[84] = 0x4c; bytes[85] = 0x01; File.WriteAllBytes(clr, bytes); }
        Assert.Throws<InvalidDataException>(() => HostRuntime.ResolveFromRoot(root));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-major")]
    [InlineData("older-desktop")]
    [InlineData("older-core")]
    public void RequiresTheFinalNetSixCoreAndDesktopRuntime(string condition)
    {
        using var game = new TestGame();
        string root = Path.Combine(game.Root, "runtime");
        WriteRuntime(root, condition == "older-core" ? "6.0.35" : "6.0.36", desktop: false);
        if (condition == "other-major") WriteRuntime(root, "9.0.20");
        else if (condition == "older-desktop") WriteRuntime(root, "6.0.35");
        else if (condition == "older-core")
        {
            WriteRuntime(root, "6.0.36");
            File.Delete(Path.Combine(root, "shared/Microsoft.NETCore.App/6.0.36/coreclr.dll"));
        }

        Assert.Throws<InvalidDataException>(() => HostRuntime.ResolveFromRoot(root));
    }

    internal static void WriteRuntime(string root, string version, bool desktop = true)
    {
        byte[] image = new byte[128];
        image[0] = 0x4d; image[1] = 0x5a; image[60] = 80;
        image[80] = 0x50; image[81] = 0x45; image[84] = 0x64; image[85] = 0x86;
        var paths = new List<string> { "dotnet.exe", $"host/fxr/{version}/hostfxr.dll", $"shared/Microsoft.NETCore.App/{version}/coreclr.dll", $"shared/Microsoft.NETCore.App/{version}/hostpolicy.dll", $"shared/Microsoft.NETCore.App/{version}/System.Private.CoreLib.dll" };
        if (desktop)
            paths.AddRange(new[] { "System.Windows.Forms.dll", "System.Windows.Forms.Primitives.dll", "Microsoft.WindowsDesktop.App.deps.json",
                "Microsoft.WindowsDesktop.App.runtimeconfig.json", "wpfgfx_cor3.dll" }.Select(file => $"shared/Microsoft.WindowsDesktop.App/{version}/{file}"));
        foreach (string path in paths)
        {
            string target = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, image);
        }
    }
}
