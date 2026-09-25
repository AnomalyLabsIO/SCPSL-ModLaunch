using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class UnityBundleVersionTests
{
    [Fact]
    public void RewritesTheVersionWithoutTouchingOtherSerializedBytes()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, UnityBundleVersion.RelativePath);
        byte[] original = File.ReadAllBytes(path);
        Assert.Equal("14.1.0", UnityBundleVersion.Rewrite(path, new Version(14, 2, 7)));
        byte[] updated = File.ReadAllBytes(path);
        Assert.Equal("14.2.7", UnityBundleVersion.Read(path));
        Assert.Equal(original.Length, updated.Length);
        Assert.Equal(original[..20], updated[..20]);
        Assert.Equal(original[26..], updated[26..]);
    }

    [Fact]
    public void CorrectVersionIsNotWrittenAgain()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, UnityBundleVersion.RelativePath);
        DateTime before = File.GetLastWriteTimeUtc(path);
        Assert.Equal("14.1.0", UnityBundleVersion.Rewrite(path, new Version(14, 1, 0)));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Equal(game.Originals[UnityBundleVersion.RelativePath], File.ReadAllBytes(path));
    }

    [Fact]
    public void ShorterVersionKeepsTheNextAlignedFieldInPlace()
    {
        using var game = new TestGame();
        byte[] before = TestGame.BundleBytes("14.10.0");
        game.Write(UnityBundleVersion.RelativePath, before);
        string path = Path.Combine(game.Root, UnityBundleVersion.RelativePath);
        UnityBundleVersion.Rewrite(path, new Version(14, 2, 7));
        byte[] after = File.ReadAllBytes(path);
        Assert.Equal(6, BitConverter.ToInt32(after, 16));
        Assert.Equal(0, after[26]);
        Assert.Equal(before[28..], after[28..]);
        Assert.Equal("14.2.7", UnityBundleVersion.Read(path));
    }

    [Theory]
    [InlineData("14.1.0", "14.2.10")]
    [InlineData("114.111.0", "14.2.7")]
    public void UnsafeLengthChangesLeaveTheFileUntouched(string previous, string replacement)
    {
        using var game = new TestGame();
        byte[] before = TestGame.BundleBytes(previous);
        game.Write(UnityBundleVersion.RelativePath, before);
        string path = Path.Combine(game.Root, UnityBundleVersion.RelativePath);
        Assert.Throws<InvalidDataException>(() => UnityBundleVersion.Rewrite(path, Version.Parse(replacement)));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("truncated")]
    public void InvalidOrAmbiguousSlotsCannotBePatched(string condition)
    {
        using var game = new TestGame();
        byte[] valid = TestGame.BundleBytes("14.1.0");
        byte[] before = condition switch
        {
            "missing" => new byte[64],
            "ambiguous" => valid.Concat(valid).ToArray(),
            _ => valid[..24]
        };
        game.Write(UnityBundleVersion.RelativePath, before);
        string path = Path.Combine(game.Root, UnityBundleVersion.RelativePath);
        Assert.Throws<InvalidDataException>(() => UnityBundleVersion.Rewrite(path, new Version(14, 2, 7)));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("9.2.7")]
    [InlineData("14.256.7")]
    [InlineData("14.2.7.1")]
    public void InvalidDetectedVersionsAreRejected(string version)
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, UnityBundleVersion.RelativePath);
        Assert.Throws<InvalidDataException>(() => UnityBundleVersion.Rewrite(path, Version.Parse(version)));
        Assert.Equal(game.Originals[UnityBundleVersion.RelativePath], File.ReadAllBytes(path));
    }
}
