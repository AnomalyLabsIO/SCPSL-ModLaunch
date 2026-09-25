using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SCPSL.ModLaunch.Core;

/// <summary>Targeted repair of Unity's serialized bundleVersion field.</summary>
public static class UnityBundleVersion
{
    public const string RelativePath = "SCPSL_Data/globalgamemanagers";
    public const string PatchName = "PlayerSettings.bundleVersion";
    private static readonly Regex Pattern = new(@"\A[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\z", RegexOptions.CultureInvariant);

    public static string Read(string path) => FindSlot(File.ReadAllBytes(path)).Value;

    /// <summary>Updates a staged file without moving subsequent serialized fields; returns the prior version.</summary>
    public static string Rewrite(string path, Version gameVersion)
    {
        ArgumentNullException.ThrowIfNull(gameVersion);
        string replacement = gameVersion.ToString();
        if (!IsGameVersion(replacement)) throw new InvalidDataException("The detected game version must have three byte-sized components and major >= 10.");
        byte[] data = File.ReadAllBytes(path);
        var slot = FindSlot(data);
        if (slot.Value == replacement) return slot.Value;
        byte[] value = Encoding.ASCII.GetBytes(replacement);
        if (value.Length > slot.Length)
            throw new InvalidDataException($"Cannot grow the bundleVersion slot from '{slot.Value}' to '{replacement}' safely.");
        // Unity aligns serialized strings to four bytes. Even shrinking must preserve the
        // next field's position; the original installer's length-prefix update alone is insufficient.
        if (Align(slot.Offset + 4 + slot.Length) != Align(slot.Offset + 4 + value.Length))
            throw new InvalidDataException($"Changing bundleVersion from '{slot.Value}' to '{replacement}' would move subsequent serialized fields.");
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(slot.Offset, 4), value.Length);
        data.AsSpan(slot.Offset + 4, slot.Length).Clear();
        value.CopyTo(data, slot.Offset + 4);
        FileOps.WriteBytes(path, data);
        if (Read(path) != replacement) throw new InvalidDataException("The saved bundleVersion could not be verified.");
        return slot.Value;
    }

    private static (int Offset, int Length, string Value) FindSlot(byte[] data)
    {
        (int Offset, int Length, string Value)? found = null;
        for (int offset = 0; offset <= data.Length - 4; offset++)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            if (length is < 6 or > 11 || length > data.Length - offset - 4) continue;
            string candidate = Encoding.ASCII.GetString(data, offset + 4, length);
            if (!IsGameVersion(candidate)) continue;
            if (found != null) throw new InvalidDataException("Multiple possible bundleVersion fields in globalgamemanagers; refusing an ambiguous rewrite.");
            found = (offset, length, candidate);
        }
        return found ?? throw new InvalidDataException("Cannot locate the required bundleVersion field in globalgamemanagers.");
    }

    private static bool IsGameVersion(string value) => Pattern.IsMatch(value) &&
        value.Split('.').All(part => byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)) &&
        byte.Parse(value.Split('.')[0], CultureInfo.InvariantCulture) >= 10;

    private static long Align(long position) => (position + 3) & ~3L;
}
