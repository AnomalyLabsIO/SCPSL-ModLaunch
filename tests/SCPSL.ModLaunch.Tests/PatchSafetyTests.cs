using SCPSL.ModLaunch.ModPatch.Headless;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class PatchSafetyTests
{
    [Theory]
    [InlineData("{\"unsupported\":true}")]
    [InlineData("{\"$type\":\"unsupported\"}")]
    [InlineData("{\"versionRanges\":[{\"unsupported\":true}]}")]
    [InlineData("{\"versionRanges\":[{\"methods\":{\"unsupported\":true}}]}")]
    [InlineData("{\"versionRanges\":[{\"methods\":{\"patchMethods\":[{\"unsupported\":true}]}}]}")]
    [InlineData("{\"versionRanges\":[{\"methods\":{\"gameVersionMethod\":{\"unsupported\":true}}}]}")]
    public void UnknownProfileFieldsStillFailAtEveryNestingLevelOnNet6(string json)
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "patch-info.json");
        File.WriteAllText(path, json);
        Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => PatchInfo.Read(path));
    }

    [Fact]
    public void KnownProfileFieldsKeepTheirValuesOnNet6()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "patch-info.json");
        File.WriteAllText(path, "{\"versionRanges\":[{\"versionFrom\":\"14.2.7\",\"versionTo\":\"Current\",\"metadataVersion\":31,\"methods\":{\"patchMethods\":[{\"name\":\"fixture\",\"patchData\":\"[RET]\",\"patchOffset\":2,\"nowarning\":false}],\"gameVersionMethod\":{\"autoFindOffsets\":true}}}]}");
        var profile = Assert.Single(PatchInfo.Read(path).VersionRanges);
        Assert.True(profile.Contains(new Version(14, 2, 7)));
        Assert.Equal(31, profile.MetadataVersion);
        Assert.Equal(2, Assert.Single(profile.Methods.PatchMethods).PatchOffset);
        Assert.True(profile.Methods.GameVersionMethod!.AutoFindOffsets);
    }

    [Theory]
    [InlineData("[ASM] xor eax,eax")]
    [InlineData("[UNKNOWN]")]
    [InlineData("ABC")]
    [InlineData("")]
    public void UnsupportedPatchSyntaxFailsClosed(string patch)
    {
        Assert.Throws<InvalidDataException>(() => PatchSafety.ParseBytes(new MethodPatch { Name = "fixture", PatchData = patch }));
    }

    [Fact]
    public void ExplicitPatchLengthCannotTruncateBytes()
    {
        Assert.Throws<InvalidDataException>(() => PatchSafety.ParseBytes(new MethodPatch { Name = "fixture", PatchData = "90 C3", PatchSize = 1 }));
    }

    [Fact]
    public void IncompleteInstructionReplacementIsRejected()
    {
        // mov rax,rcx (3 bytes), followed by ret. Replacing its first byte with NOP
        // would leave the remaining instruction bytes to execute with new meaning.
        byte[] original = [0x48, 0x89, 0xC8, 0xC3];
        var patch = new MethodPatch { Name = "fixture", PatchData = "90" };
        Assert.Throws<InvalidDataException>(() => PatchSafety.Validate(original, 0, original.Length, patch, PatchSafety.ParseBytes(patch)));
    }

    [Fact]
    public void OffsetInsideAnInstructionIsRejected()
    {
        byte[] original = [0x48, 0x89, 0xC8, 0xC3];
        var patch = new MethodPatch { Name = "fixture", PatchOffset = 1, PatchData = "90 90" };
        Assert.Throws<InvalidDataException>(() => PatchSafety.Validate(original, 0, original.Length, patch, PatchSafety.ParseBytes(patch)));
    }

    [Fact]
    public void PatchCannotWriteIntoNextFunction()
    {
        byte[] original = [0x90, 0x90, 0xC3, 0x90, 0xC3];
        var patch = new MethodPatch { Name = "fixture", PatchOffset = 2, PatchData = "90 90" };
        Assert.Throws<InvalidDataException>(() => PatchSafety.Validate(original, 0, 3, patch, PatchSafety.ParseBytes(patch)));
    }

    [Fact]
    public void FunctionEntryReturnAndWholeInstructionPatchAreAccepted()
    {
        byte[] original = [0x48, 0x89, 0xC8, 0xC3];
        foreach (string data in new[] { "[RET]", "[NOP][NOP][NOP]" })
        {
            var patch = new MethodPatch { Name = "fixture", PatchData = data };
            PatchSafety.Validate(original, 0, original.Length, patch, PatchSafety.ParseBytes(patch));
        }
    }

    [Fact]
    public void MetadataNormalizationOnlyChangesParserClone()
    {
        using var game = new TestGame();
        byte[] bytes = new byte[264];
        BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0);
        BitConverter.GetBytes(23).CopyTo(bytes, 4);
        BitConverter.GetBytes(264).CopyTo(bytes, 8);
        BitConverter.GetBytes(92).CopyTo(bytes, 164);
        string path = Path.Combine(game.Root, "metadata.dat");
        File.WriteAllBytes(path, bytes);

        var parser = MetadataHeaderNormalizer.NormalizeBytesForParser(bytes);
        Assert.NotSame(bytes, parser);
        Assert.Equal(24, BitConverter.ToInt32(parser, 4));
        Assert.Equal(23, BitConverter.ToInt32(bytes, 4));
        var inspected = MetadataHeaderNormalizer.NormalizeFile(path);
        Assert.False(inspected.HeaderWasNormalized);
        Assert.Equal(24, inspected.HeaderVersionAfter);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
