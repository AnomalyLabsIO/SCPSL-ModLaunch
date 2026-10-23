using SCPSL.ModLaunch.ModPatch.Headless;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class HeadlessArgumentTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void MelonLoaderGenerationCanBeExplicitlySelected(string value)
    {
        var options = Program.ParseArguments(["prepare", "--game-dir", "stage", "--patch-info", "patchinfo.json",
            "--result", "result.json", "--skip-melonloader", value]);
        Assert.Equal(value, options["--skip-melonloader"]);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData("--result")]
    public void InvalidMelonLoaderModesAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArguments(["prepare", "--skip-melonloader", value]));
    }

    [Fact]
    public void RepeatedMelonLoaderModesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArguments(["prepare", "--skip-melonloader", "true", "--skip-melonloader", "false"]));
    }
}
