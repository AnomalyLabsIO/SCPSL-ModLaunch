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
    public void RejectsIncompatibleOrIncompleteWindowsRuntime(string condition)
    {
        using var game = new TestGame();
        string root = Path.Combine(game.Root, "runtime");
        WriteRuntime(root, "6.0.36");
        string clr = Path.Combine(root, "shared/Microsoft.NETCore.App/6.0.36/coreclr.dll");
        if (condition == "missing-corelib") File.Delete(Path.Combine(root, "shared/Microsoft.NETCore.App/6.0.36/System.Private.CoreLib.dll"));
        else if (condition == "linux") File.WriteAllText(clr, "ELF");
        else { byte[] bytes = File.ReadAllBytes(clr); bytes[84] = 0x4c; bytes[85] = 0x01; File.WriteAllBytes(clr, bytes); }
        Assert.Throws<InvalidDataException>(() => HostRuntime.ResolveFromRoot(root));
    }

    internal static void WriteRuntime(string root, string version)
    {
        byte[] image = new byte[128];
        image[0] = 0x4d; image[1] = 0x5a; image[60] = 80;
        image[80] = 0x50; image[81] = 0x45; image[84] = 0x64; image[85] = 0x86;
        foreach (string path in new[] { "dotnet.exe", $"host/fxr/{version}/hostfxr.dll", $"shared/Microsoft.NETCore.App/{version}/coreclr.dll", $"shared/Microsoft.NETCore.App/{version}/hostpolicy.dll", $"shared/Microsoft.NETCore.App/{version}/System.Private.CoreLib.dll" })
        {
            string target = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, image);
        }
    }
}
