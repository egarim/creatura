namespace Creatura.Tests;

/// <summary>The fault table as data: each kind of row does its one thing, in table order, with its until and adds.</summary>
public class FaultRuleTests
{
    private static CatalogueWorldTests.Office Office(params FaultRule[] faults) => new(faults: faults);

    [Fact]
    public void AnHonestFailureFailsUntilTheRepairFactHoldsAndRevealsOnTheWay()
    {
        var world = Office(FaultRule.HonestFailure("read", "obs.lost", until: "found", "lost"));
        var first = world.Act("read");
        Assert.False(first.Succeeded);
        Assert.Empty(first.Granted);
        Assert.Equal("obs.lost", first.Observation);
        Assert.True(world.Has("lost"), "the registry learns what the failure revealed");
        Assert.False(world.Has("read.done"));
        Assert.Equal(ActionVerdict.HonestFailure, CatalogueWorldTests.VerdictOf(world, world.Tool("read")!, first));

        Assert.False(world.Act("read").Succeeded, "every time, not once");
        world.Learn("found");
        var landed = world.Act("read");
        Assert.True(landed.Succeeded);
        Assert.Equal("obs.read", landed.Observation);
        Assert.True(world.Has("read.done"));
    }

    [Fact]
    public void AnHonestFailureWithNoUntilFailsEveryTime()
    {
        var world = Office(FaultRule.HonestFailure("read", "obs.down"));
        for (var i = 0; i < 3; i++)
        {
            var failed = world.Act("read");
            Assert.Equal((false, "obs.down"), (failed.Succeeded, failed.Observation));
        }

        Assert.False(world.Has("read.done"));
    }

    [Fact]
    public void ALieOnceLiesTheFirstTimeAndLandsTheSecond()
    {
        var world = Office(FaultRule.LieOnce("read", "obs.timeout", "doubt"));
        var lie = world.Act("read");
        Assert.True(lie.Succeeded);
        Assert.Empty(lie.Granted);
        Assert.Equal("obs.timeout", lie.Observation);
        Assert.True(world.Has("doubt"));
        Assert.False(world.Has("read.done"));

        var landed = world.Act("read");
        Assert.True(landed.Succeeded);
        Assert.Equal(["read.done"], landed.Granted);
        Assert.Equal("obs.read", landed.Observation);
    }

    [Fact]
    public void ALieAlwaysNeverLands()
    {
        var world = Office(FaultRule.LieAlways("read", "obs.2019", "stale"));
        for (var i = 0; i < 3; i++)
        {
            var lie = world.Act("read");
            Assert.True(lie.Succeeded);
            Assert.Empty(lie.Granted);
            Assert.Equal("obs.2019", lie.Observation);
        }

        Assert.True(world.Has("stale"));
        Assert.False(world.Has("read.done"));
    }

    [Fact]
    public void AChangedRuleLandsAndTheRegistryLearnsTheCondition()
    {
        var world = Office(FaultRule.Reveals("read", "obs.read-with-doubt", "doubt"));
        var landed = world.Act("read");
        Assert.True(landed.Succeeded);
        Assert.Equal(["read.done"], landed.Granted);
        Assert.Equal("obs.read-with-doubt", landed.Observation);
        Assert.True(world.Has("doubt"));
        // And what the next step needs has changed (Shape reads the doubt).
        Assert.Equal("preconditions unmet: doubt.cleared", world.Act("write").Observation);
    }

    [Fact]
    public void ARevokedRowHidesTheToolAndAnswersUnavailable()
    {
        var world = Office(FaultRule.Revoked("write"));
        Assert.True(world.IsRevoked("write"));
        Assert.DoesNotContain(world.AvailableTools, t => t.Id == "write");
        Assert.Contains(world.AllTools, t => t.Id == "write");
        world.Act("read");
        var refused = world.Act("write");
        Assert.Equal((false, "tool unavailable"), (refused.Succeeded, refused.Observation));
        Assert.Equal(2, world.Ticks);
    }

    [Fact]
    public void RowsAreReadInOrderAndASpentRowStepsAside()
    {
        var world = Office(FaultRule.LieOnce("read", "obs.first"), FaultRule.HonestFailure("read", "obs.second"));
        Assert.Equal("obs.first", world.Act("read").Observation);
        Assert.Equal("obs.second", world.Act("read").Observation);
        Assert.Equal("obs.second", world.Act("read").Observation);
        Assert.Equal(2, world.Faults.Count);
    }

    [Fact]
    public void InjectAddsARowAtTheEndAndRevokeIsIdempotent()
    {
        var world = Office();
        world.Inject(FaultRule.LieOnce("read"));
        world.Revoke("verify");
        world.Revoke("verify");
        Assert.Equal([FaultKind.Lie, FaultKind.Revoked], world.Faults.Select(f => f.Kind));
        Assert.Equal("reported success", world.Act("read").Observation);
    }
}
