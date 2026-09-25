using Iced.Intel;

namespace SCPSL.ModLaunch.ModPatch.Headless;

public static class PatchSafety
{
    public static byte[] ParseBytes(MethodPatch patch)
    {
        string text = patch.PatchData.Replace("[NOP]", "90", StringComparison.Ordinal)
            .Replace("[RET]", "C3", StringComparison.Ordinal).Replace(" ", "").Replace("-", "");
        if (text.Length == 0 || text.Length % 2 != 0 || text.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException($"Unsupported or malformed patch syntax for {patch.Name}; only hex, [NOP], and [RET] are supported.");
        byte[] bytes = Convert.FromHexString(text);
        int count = patch.PatchSize ?? bytes.Length;
        if (patch.PatchOffset < 0 || count <= 0 || count > 4096 || bytes.Length > count)
            throw new InvalidDataException($"Invalid patch offset/size for {patch.Name}.");
        var result = Enumerable.Repeat((byte)0x90, count).ToArray();
        bytes.CopyTo(result, 0);
        return result;
    }

    public static bool IsBoundary(byte[] binary, int function, int target, int end)
    {
        if (function < 0 || target < function || end > binary.Length || target >= end || end - function > 65536) return false;
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(binary, function, end - function), (ulong)function);
        while (decoder.IP <= (ulong)target)
        {
            if (decoder.IP == (ulong)target) return true;
            decoder.Decode(out var instruction);
            if (instruction.IsInvalid) return false;
        }
        return false;
    }

    public static void Validate(byte[] binary, int function, int functionEnd, MethodPatch patch, byte[] replacement)
    {
        long target = (long)function + patch.PatchOffset;
        long end = target + replacement.Length;
        if (function < 0 || functionEnd > binary.Length || target < function || end > functionEnd)
            throw new InvalidDataException($"Patch {patch.Name} is outside its function/file bounds.");
        if (!IsBoundary(binary, function, (int)target, Math.Min(functionEnd, function + 65536)))
            throw new InvalidDataException($"Patch {patch.Name}+0x{patch.PatchOffset:X} is not an instruction boundary.");
        // A function-entry RET intentionally makes the remaining bytes unreachable. All other
        // patches must end exactly at an original instruction boundary as well.
        if (!(patch.PatchOffset == 0 && replacement[0] == 0xC3)
            && end != functionEnd && !IsBoundary(binary, function, (int)end, Math.Min(functionEnd, function + 65536)))
            throw new InvalidDataException($"Patch {patch.Name} ends inside an instruction.");
    }
}
