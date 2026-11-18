using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class LauncherUiActionTests
{
    [Fact]
    public void EmptyInvocationAndExplicitGuiSelectTheWindow()
    {
        Assert.Equal(LauncherMode.Gui, LauncherEntryPoint.ResolveMode([]));
        Assert.Equal(LauncherMode.Gui, LauncherEntryPoint.ResolveMode(["--modlaunch-gui"]));
        Assert.Empty(GameArguments.Parse(["--modlaunch-gui"]).GameArguments);
    }

    [Fact]
    public void ParametersKeepCliModeAndExplicitCliAllowsAnEmptyGameArgumentList()
    {
        foreach (string[] args in new[]
                 {
                     new[] { "--modlaunch-cli" }, new[] { "--modlaunch-no-start" },
                     new[] { "-noauth", "two words" }, new[] { "" }, new[] { "--" }
                 })
            Assert.Equal(LauncherMode.Cli, LauncherEntryPoint.ResolveMode(args));
        var cli = GameArguments.Parse(["--modlaunch-cli", "--modlaunch-cli"]);
        Assert.Empty(cli.GameArguments);
        Assert.False(cli.RecoverOnly);
        Assert.False(cli.Repair);
        Assert.False(cli.Launch);
    }

    [Theory]
    [InlineData("--modlaunch-cli")]
    [InlineData("--modlaunch-no-start")]
    [InlineData("-noauth")]
    [InlineData("--modlaunch-gui")]
    public void ExplicitGuiRejectsCliOptionsAndGameArguments(string other)
    {
        Assert.Throws<ArgumentException>(() => LauncherEntryPoint.ResolveMode(["--modlaunch-gui", other]));
        Assert.Throws<ArgumentException>(() => GameArguments.Parse([other, "--modlaunch-gui"]));
    }

    [Fact]
    public void LiteralModeAndActionNamesAfterSeparatorRemainGameArguments()
    {
        string[] literal = ["--", "--modlaunch-gui", "--modlaunch-cli", "--modlaunch-launch", "--modlaunch-repair", ""];
        Assert.Equal(LauncherMode.Cli, LauncherEntryPoint.ResolveMode(literal));
        var result = GameArguments.Parse(["--modlaunch-cli", .. literal]);
        Assert.Equal(literal, result.GameArguments);
        Assert.False(result.Repair);
        Assert.False(result.Launch);
        Assert.Throws<ArgumentException>(() => LauncherEntryPoint.ResolveMode(["--modlaunch-gui", "--"]));
    }

    [Fact]
    public void RepairAndLaunchOptionsAreConsumedWithoutChangingGameArguments()
    {
        var parsed = GameArguments.Parse(["--modlaunch-cli", "-noauth", "--modlaunch-repair", "", "--modlaunch-launch", "two words"]);
        Assert.True(parsed.Repair);
        Assert.True(parsed.Launch);
        Assert.False(parsed.NoStart);
        Assert.False(parsed.NoRestore);
        Assert.Equal(new[] { "-noauth", "", "two words" }, parsed.GameArguments);
    }

    [Theory]
    [InlineData("--modlaunch-repair")]
    [InlineData("--modlaunch-launch")]
    public void RecoveryRejectsPreparationAndLaunchActions(string option)
    {
        Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--modlaunch-cli", "--modlaunch-recover-only", option]));
    }

    [Fact]
    public void GuiLaunchUsesDefaultRestoreBehavior()
    {
        string[] args = GuiLaunchActions.ArgumentsFor(GuiLaunchAction.Launch);
        Assert.Equal(LauncherMode.Cli, LauncherEntryPoint.ResolveMode(args));
        var parsed = GameArguments.Parse(args);
        Assert.True(parsed.Launch);
        Assert.False(parsed.NoStart);
        Assert.False(parsed.NoRestore);
        Assert.False(parsed.NoMelonLoader);
        Assert.False(parsed.Repair);
        Assert.Empty(parsed.GameArguments);
    }

    [Theory]
    [InlineData(GuiLaunchAction.Launch)]
    [InlineData(GuiLaunchAction.Prepare)]
    [InlineData(GuiLaunchAction.Repair)]
    public void RestoreCheckboxControlsTheExistingCliOption(GuiLaunchAction action)
    {
        var restore = GameArguments.Parse(GuiLaunchActions.ArgumentsFor(action, restoreVanillaOnExit: true));
        var retain = GameArguments.Parse(GuiLaunchActions.ArgumentsFor(action, restoreVanillaOnExit: false));
        Assert.False(restore.NoRestore);
        Assert.True(retain.NoRestore);
        Assert.True(retain.Launch);
        Assert.Equal(restore.NoStart, retain.NoStart);
        Assert.Equal(restore.Repair, retain.Repair);
        Assert.Empty(retain.GameArguments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitRestoreIgnoresTheExitPreference(bool restoreVanillaOnExit)
    {
        var parsed = GameArguments.Parse(GuiLaunchActions.ArgumentsFor(GuiLaunchAction.Restore, restoreVanillaOnExit));
        Assert.True(parsed.RecoverOnly);
        Assert.False(parsed.NoRestore);
    }

    [Theory]
    [InlineData(GuiLaunchAction.Prepare, false)]
    [InlineData(GuiLaunchAction.Repair, true)]
    public void GuiPreparationContinuesAfterRecoveryButDoesNotStartTheGame(GuiLaunchAction action, bool repair)
    {
        var parsed = GameArguments.Parse(GuiLaunchActions.ArgumentsFor(action));
        Assert.True(parsed.Launch);
        Assert.True(parsed.NoStart);
        Assert.Equal(repair, parsed.Repair);
        Assert.False(parsed.RecoverOnly);
        Assert.False(parsed.NoRestore);
        Assert.Empty(parsed.GameArguments);
    }

    [Fact]
    public void GuiRestoreSelectsOnlyRecoveryAndUnknownActionsAreRejected()
    {
        var parsed = GameArguments.Parse(GuiLaunchActions.ArgumentsFor(GuiLaunchAction.Restore));
        Assert.True(parsed.RecoverOnly);
        Assert.False(parsed.Launch);
        Assert.False(parsed.Repair);
        Assert.Empty(parsed.GameArguments);
        Assert.Throws<ArgumentOutOfRangeException>(() => GuiLaunchActions.ArgumentsFor((GuiLaunchAction)int.MaxValue));
    }
}
