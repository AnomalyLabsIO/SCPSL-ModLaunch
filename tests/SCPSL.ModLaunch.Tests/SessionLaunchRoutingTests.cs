using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class SessionLaunchRoutingTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RetainedLoaderModeChangeRestoresThenPreparesWithoutExplicitLaunch(bool existingSkip, bool requestedSkip)
    {
        var arguments = RetainedArguments(requestedSkip);
        var session = new SessionJournal { SkipMelonLoader = existingSkip };

        Assert.False(arguments.Launch);
        Assert.False(arguments.Repair);
        Assert.False(SessionLaunchRouting.CanReuse(arguments, session));
        Assert.True(SessionLaunchRouting.ShouldPrepareAfterRestoration(arguments, session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedMatchingLoaderModeReusesTheActiveSession(bool skip)
    {
        var arguments = RetainedArguments(skip);
        var session = new SessionJournal { SkipMelonLoader = skip };

        Assert.True(SessionLaunchRouting.CanReuse(arguments, session));
        // A failed verification retains the existing restore-and-exit behavior.
        Assert.False(SessionLaunchRouting.ShouldPrepareAfterRestoration(arguments, session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepairAlwaysRestoresAndRebuildsEvenWhenLoaderModeMatches(bool skip)
    {
        var arguments = RetainedArguments(skip) with { Repair = true };
        var session = new SessionJournal { SkipMelonLoader = skip };

        Assert.False(SessionLaunchRouting.CanReuse(arguments, session));
        Assert.True(SessionLaunchRouting.ShouldPrepareAfterRestoration(arguments, session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryInvocationRestoresThenExitsRegardlessOfTheExistingLoaderMode(bool existingSkip)
    {
        var arguments = GameArguments.Parse(["--modlaunch-cli"]);
        var session = new SessionJournal { SkipMelonLoader = existingSkip };

        Assert.False(SessionLaunchRouting.CanReuse(arguments, session));
        Assert.False(SessionLaunchRouting.ShouldPrepareAfterRestoration(arguments, session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuiLaunchWithRestorationEnabledRestoresThenPrepares(bool existingSkip)
    {
        var arguments = GameArguments.Parse(GuiLaunchActions.ArgumentsFor(GuiLaunchAction.Launch));
        var session = new SessionJournal { SkipMelonLoader = existingSkip };

        Assert.False(SessionLaunchRouting.CanReuse(arguments, session));
        Assert.True(SessionLaunchRouting.ShouldPrepareAfterRestoration(arguments, session));
    }

    [Theory]
    [InlineData(GuiLaunchAction.Launch)]
    [InlineData(GuiLaunchAction.Prepare)]
    public void GuiActionWithRestorationDisabledUpgradesAPatchOnlySession(GuiLaunchAction action)
    {
        var arguments = GameArguments.Parse(GuiLaunchActions.ArgumentsFor(action, restoreVanillaOnExit: false));
        var session = new SessionJournal { SkipMelonLoader = true };

        Assert.False(SessionLaunchRouting.CanReuse(arguments, session));
        Assert.True(SessionLaunchRouting.ShouldPrepareAfterRestoration(arguments, session));
    }

    private static LaunchArguments RetainedArguments(bool skip) => GameArguments.Parse(skip
        ? ["--modlaunch-cli", "--modlaunch-no-restore", "--modlaunch-no-melonloader"]
        : ["--modlaunch-cli", "--modlaunch-no-restore"]);
}
