using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace SCPSL.ModLaunch.ModPatch.Headless;

/// <summary>Uses the Unity 6.4 unstripping fix for preparation without replacing MelonLoader's runtime.</summary>
internal static class BundledInteropGenerator
{
    internal const string Revision = "455c42a6f35fd272c2ab3023208defa593fb1d70";
    private const string Sha256 = "0FD112D3986BEF4810E8FF9DAC69C18502626B076243CEAE4514173CC3DEA67E";

    internal static Assembly Load(AssemblyLoadContext context)
    {
        using var resource = typeof(BundledInteropGenerator).Assembly.GetManifestResourceStream("SCPSL.ModLaunch.Il2CppInterop.Generator.dll")
            ?? throw new InvalidDataException("The adapter is missing its bundled Il2CppInterop generator.");
        using var payload = new MemoryStream();
        resource.CopyTo(payload);
        if (Convert.ToHexString(SHA256.HashData(payload.ToArray())) != Sha256)
            throw new InvalidDataException("The bundled Il2CppInterop generator does not match its pinned SHA-256.");
        payload.Position = 0;
        Console.WriteLine($"  Using bundled Il2CppInterop generator {Revision} (Unity 6.4 unstripping fix).");
        return context.LoadFromStream(payload);
    }
}
