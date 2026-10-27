using Creatura.Hotz;

namespace Creatura.Tests;

/// <summary>Each cross-cutting barrier does its one thing over any world, and the recorder applied last sees them all.</summary>
public class BarrierTests
{
    private static World Toy() => RestoreServiceDomain.NewWorld();

    [Fact]
    public void RevokeHidesTheToolsAndAnswersUnavailableWithoutAskingTheInnerWorld()
    {
        var world = Barrier.Revoke(Toy(), "apply-patch");
        Assert.DoesNotContain(world.AvailableTools, t => t.Id == "apply-patch");
        Assert.Contains(world.AllTools, t => t.Id == "apply-patch");
        Assert.NotNull(world.Tool("apply-patch"));
        var refused = world.Act("apply-patch");
        Assert.False(refused.Succeeded);
        Assert.Equal("tool unavailable", refused.Observation);
        Assert.Equal(0, world.Ticks);
        Assert.True(world.Act("inspect-logs").Succeeded);
        Assert.Equal(1, world.Ticks);
    }

    [Fact]
    public void LieOnceLiesTheFirstTimeForTheNamedToolsOnly()
    {
        var world = Barrier.LieOnce(Toy(), "inspect-logs");
        var lie = world.Act("inspect-logs");
        Assert.True(lie.Succeeded);
        Assert.Empty(lie.Granted);
        Assert.Equal("reported success", lie.Observation);
        Assert.True(world.Ticks == 0, "the inner world is never asked");
        Assert.Equal(1, world.Lies);
        Assert.True(world.Act("inspect-logs").Succeeded);
        Assert.True(world.Has("logs.inspected"));
        Assert.True(world.Act("identify-component").Succeeded, "an unnamed tool never lies");
        Assert.True(world.Has("component.identified"));
    }

    [Fact]
    public void LieAlwaysNeverLands()
    {
        var world = Barrier.LieAlways(Toy(), "inspect-logs");
        for (var i = 0; i < 3; i++) Assert.Empty(world.Act("inspect-logs").Granted);
        Assert.False(world.Has("logs.inspected"));
        Assert.Equal(3, world.Lies);
        Assert.Equal(0, world.Ticks);
    }

    [Fact]
    public void LieOnceOverEveryToolIsTheLiarWorld()
    {
        var world = Barrier.LieOnce(Toy());
        Assert.IsType<LiarWorld>(world);
        Assert.Empty(world.Act("inspect-logs").Granted);
        Assert.True(world.Act("inspect-logs").Succeeded);
        Assert.True(world.Has("logs.inspected"));
        // A tool whose preconditions are unmet does not spend its lie: the inner world refuses it.
        Assert.Equal("preconditions unmet: component.identified", world.Act("apply-patch").Observation);
    }

    [Fact]
    public void OpaqueTakesTheMapAway()
    {
        var world = Barrier.Opaque(Toy());
        Assert.IsType<OpaqueWorld>(world);
        Assert.All(world.AvailableTools, t => Assert.Empty(t.Requires));
        Assert.All(world.AvailableTools, t => Assert.Empty(t.Grants));
        Assert.Equal("something changed", world.Act("inspect-logs").Observation);
        Assert.Equal("nothing happened", world.Act("deploy").Observation);
    }

    [Fact]
    public void RecordedLastSeesTheBarriersUnderIt()
    {
        var recorded = Barrier.Recorded(Barrier.LieOnce(Barrier.Revoke(Toy(), "apply-config"), "inspect-logs"));
        Assert.IsType<RouteRecorder>(recorded);
        recorded.Act("inspect-logs");
        recorded.Act("apply-config");
        Assert.Equal(2, recorded.Claims.Count);
        var lie = recorded.Claims[0];
        Assert.True(lie.ReportedSuccess);
        Assert.Equal(["logs.inspected"], lie.Promised);
        Assert.DoesNotContain("logs.inspected", lie.Registry);
        Assert.False(recorded.Claims[1].ReportedSuccess);
        Assert.Equal("tool unavailable", recorded.Claims[1].Observation);
    }
}
