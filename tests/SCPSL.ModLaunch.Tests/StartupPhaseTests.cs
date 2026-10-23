using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class StartupPhaseTests
{
    [Fact]
    public void PhaseIsLoggedWhenTheScopeFinishes()
    {
        var messages = new List<string>();
        using (StartupPhase.Start(messages.Add, "cache validation")) Assert.Empty(messages);
        Assert.Matches(@"^Startup phase cache validation: .+ ms\.$", Assert.Single(messages));
    }

    [Fact]
    public void FailedPhaseStillReportsItsElapsedTime()
    {
        var messages = new List<string>();
        Action failingPhase = () =>
        {
            using var phase = StartupPhase.Start(messages.Add, "resource resolution");
            throw new InvalidOperationException("fixture failure");
        };
        Assert.Throws<InvalidOperationException>(failingPhase);
        Assert.Matches(@"^Startup phase resource resolution: .+ ms\.$", Assert.Single(messages));
    }

    [Fact]
    public void RepeatedDisposeDoesNotDuplicateThePhaseTiming()
    {
        var messages = new List<string>();
        var phase = StartupPhase.Start(messages.Add, "game launch");
        phase.Dispose();
        phase.Dispose();
        Assert.Single(messages);
    }
}
