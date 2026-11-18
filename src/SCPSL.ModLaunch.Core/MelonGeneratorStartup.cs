using System.Security.Cryptography;

namespace SCPSL.ModLaunch.Core;

internal static class MelonGeneratorStartup
{
    internal const string RelativePath = "MelonLoader/Dependencies/Il2CppAssemblyGenerator/Il2CppAssemblyGenerator.dll";
    internal const string StockHash = "4B9C8A48640BEFE1840F7ED0A1059DB173B22B319C4F684CE7E1D8794802B4DC";
    internal const string PatchedHash = "FDDF536C2D2FC39FCAD9AC448055E3EBE95096C2561A116D0ECDAE118B03174C";

    internal static bool DisableRemoteLookup(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string hash = Hash(bytes);
        if (hash == PatchedHash) return false;
        if (hash != StockHash)
            throw new InvalidDataException("The MelonLoader assembly generator does not match the pinned 0.7.3 release. Run ModLaunch Repair.");

        // Official v0.7.3 x64, Core.Run IL_0016. Replace the five-byte call to
        // RemoteAPI.Contact() with nops. PackageBase.Setup still controls downloads.
        Array.Clear(bytes, 0x3C2, 5);
        if (Hash(bytes) != PatchedHash)
            throw new InvalidDataException("Unexpected result disabling the MelonLoader generator's remote lookup.");
        FileOps.WriteBytes(path, bytes);
        return true;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
