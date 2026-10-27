using Creatura.Hotz;
using Creatura.Worlds.Samples;

namespace Creatura.Tests;

/// <summary>
/// Today's answers from every world, recorded before <c>CatalogueWorld</c> (migration step 12) rewrites how the
/// hand-rolled worlds answer them. Each row says what a world does on a refusal (does <c>Ticks</c> move, what the
/// observation says), on a landed act (<c>EffectsLanded</c>, the observation), on a lie (succeeded with nothing
/// granted), on a revoked or hidden tool (absent from <c>AvailableTools</c>, still in <c>AllTools</c> or not, still
/// actable or not), and that <c>AllTools</c> covers <c>AvailableTools</c>. The quirks are recorded as they are,
/// not as they should be: a step that changes one changes a row here on purpose.
/// </summary>
public class WorldContractTests
{
    /// <summary>One world and its answers. Null means the row has no such part.</summary>
    public sealed record WorldCase(
        string Name,
        Func<IWorld> Make,
        string UnknownObservation,
        string Unmet,
        string UnmetObservation,
        bool TicksOnRefusal,
        string[] LandingSetup,
        string Landing,
        string? LandedObservation,
        bool LandedGrantsAreTheTools,
        string[] LieSetup,
        string? Liar,
        string? LieObservation,
        bool TicksOnLie,
        string? Hidden,
        bool HiddenInAllTools,
        string? HiddenObservation,
        bool TicksOnHidden,
        bool VacuousEffectIsTrue)
    {
        public override string ToString() => Name;
    }

    private static World Toy(bool patchLies = false, string? revoked = null)
    {
        var world = RestoreServiceDomain.NewWorld();
        if (patchLies) world.MakeLiar("apply-patch");
        if (revoked is not null) world.Revoke(revoked);
        return world;
    }

    public static TheoryData<WorldCase> Worlds =>
    [
        // The toy world: refusals tick, a landed act says nothing, a revoked tool stays in AllTools and answers "tool unavailable".
        new("World", () => Toy(patchLies: true, revoked: "deploy"), "unknown tool", "apply-patch", "preconditions unmet: component.identified", true,
            [], "inspect-logs", null, true, ["inspect-logs", "identify-component"], "apply-patch", "reported success", true, "deploy", true, "tool unavailable", true, true),
        new("PermitOfficeWorld", () => new PermitOfficeWorld(new PermitBarriers(false, true, false, false, true)), "unknown tool", "pay-fee", "preconditions unmet: documents.complete", true,
            [], "check-documents", "obs.documents-ok", true, ["check-documents"], "pay-fee", "obs.gateway-accepted", true, null, false, null, false, true),
        new("IrrigationWorld", () => new IrrigationWorld(new Dictionary<string, bool> { ["sensor"] = true }), "unknown tool", "start-pump", "preconditions unmet: sensors.read", true,
            [], "read-sensors", "obs.sensors", true, ["read-sensors", "check-forecast", "start-pump", "allocate-water", "open-reservoir-gate"], "irrigate-zone-b", "obs.zone-b-sensor", true, null, false, null, false, true),
        new("LivestockWorld", () => new LivestockWorld(new Dictionary<string, bool> { ["scale"] = true }), "unknown tool", "vaccinate-lot", "preconditions unmet: vaccines.valid, herd.counted", true,
            [], "count-herd", "obs.count", true, ["count-herd"], "weigh-lot", "obs.scale-cached", true, null, false, null, false, true),
        // The flood's second clinic line is hidden from AvailableTools but Tool() still finds it in the catalogue, so it can be acted on.
        new("FloodReliefWorld", () => new FloodReliefWorld(new Dictionary<string, bool> { ["ferry-lies"] = true }), "unknown tool", "load-truck", "preconditions unmet: road.clear", true,
            [], "clear-road", "obs.clear-road", true, ["fuel-boat"], "ferry", "obs.ferry-radioed-delivered", true, "confirm-by-radio", true, "preconditions unmet: supplies.delivered", true, true),
        // The stale directory always lies, and adds a doubt fact while granting none of its own.
        new("ThreeSourcesWorld", () => new ThreeSourcesWorld(), "unknown tool", "registry-lookup", "preconditions unmet: case.open", true,
            [], "open-case", "obs.open-case", true, [], "stale-directory", "obs.directory-2019", true, null, false, null, false, true),
        new("LicenceWorld", () => new LicenceWorld(), "unknown tool", "take-payment", "preconditions unmet: file.ok", true,
            [], "check-file", "obs.check-file", true, [], null, null, false, null, false, null, false, true),
        // The map taken away: every refusal reads "nothing happened", a landed act "something changed" with the gain as its grant
        // (the stripped tool promises nothing), a lie "reported success"; a revoked tool is gone from AllTools too and its
        // refusal does not tick (the inner world is never asked).
        new("OpaqueWorld", () => new OpaqueWorld(Toy(patchLies: true), ["deploy"]), "nothing happened", "apply-patch", "nothing happened", true,
            [], "inspect-logs", "something changed", false, ["inspect-logs", "identify-component"], "apply-patch", "reported success", true, "deploy", false, "nothing happened", false, false),
        new("OpaqueWorld.transparent", () => new OpaqueWorld(Toy(patchLies: true), ["deploy"]) { Transparent = true }, "unknown tool", "apply-patch", "preconditions unmet: component.identified", true,
            [], "inspect-logs", null, true, ["inspect-logs", "identify-component"], "apply-patch", "reported success", true, "deploy", false, "nothing happened", false, true),
        new("LearnedWorld", () => new LearnedWorld(new OpaqueWorld(Toy(patchLies: true), ["deploy"]), new EffectMemory()), "nothing happened", "apply-patch", "nothing happened", true,
            [], "inspect-logs", "something changed", false, ["inspect-logs", "identify-component"], "apply-patch", "reported success", true, "deploy", false, "nothing happened", false, false),
        // Every tool lies once, without the inner world being asked (no tick); the second call lands.
        new("LiarWorld", () => new LiarWorld(Toy(revoked: "deploy")), "unknown tool", "apply-patch", "preconditions unmet: component.identified", true,
            ["inspect-logs"], "inspect-logs", null, true, [], "inspect-logs", "reported success", false, "deploy", true, "tool unavailable", true, true),
        new("RouteRecorder", () => new RouteRecorder(Toy(patchLies: true, revoked: "deploy")), "unknown tool", "apply-patch", "preconditions unmet: component.identified", true,
            [], "inspect-logs", null, true, ["inspect-logs", "identify-component"], "apply-patch", "reported success", true, "deploy", true, "tool unavailable", true, true),
    ];

    [Theory]
    [MemberData(nameof(Worlds))]
    public void ARefusalAnswersAsRecorded(WorldCase c)
    {
        var world = c.Make();
        var before = world.Ticks;

        var unknown = world.Act("no-such-tool");
        Assert.False(unknown.Succeeded);
        Assert.Empty(unknown.Granted);
        Assert.Equal(c.UnknownObservation, unknown.Observation);
        Assert.Equal(c.TicksOnRefusal ? before + 1 : before, world.Ticks);

        var unmet = world.Act(c.Unmet);
        Assert.False(unmet.Succeeded);
        Assert.Empty(unmet.Granted);
        Assert.Equal(c.UnmetObservation, unmet.Observation);
        Assert.Equal(c.TicksOnRefusal ? before + 2 : before, world.Ticks);
    }

    [Theory]
    [MemberData(nameof(Worlds))]
    public void ALandedActGrantsAndEffectsLanded(WorldCase c)
    {
        var world = c.Make();
        foreach (var step in c.LandingSetup) world.Act(step);
        var tool = world.Tool(c.Landing)!;
        Assert.False(world.EffectsLanded(tool) && tool.Grants.Count > 0, "nothing has landed yet");

        var outcome = world.Act(c.Landing);
        Assert.True(outcome.Succeeded);
        Assert.Equal(c.LandedObservation, outcome.Observation);
        if (c.LandedGrantsAreTheTools) Assert.Equal(world.Tool(c.Landing)!.Grants, outcome.Granted);
        Assert.NotEmpty(outcome.Granted);
        Assert.True(world.EffectsLanded(world.Tool(c.Landing)!), "the registry shows the effect");
    }

    [Theory]
    [MemberData(nameof(Worlds))]
    public void ALieSucceedsWithNothingGranted(WorldCase c)
    {
        if (c.Liar is null) return;
        var world = c.Make();
        foreach (var step in c.LieSetup) world.Act(step);
        var before = world.Ticks;

        var lie = world.Act(c.Liar);
        Assert.True(lie.Succeeded, "a lie reports success");
        Assert.Empty(lie.Granted);
        Assert.Equal(c.LieObservation, lie.Observation);
        Assert.False(world.EffectsLanded(world.Tool(c.Liar)!), "and the registry shows nothing of what it promised");
        Assert.Equal(c.TicksOnLie ? before + 1 : before, world.Ticks);
    }

    [Theory]
    [MemberData(nameof(Worlds))]
    public void AHiddenToolIsAbsentFromAvailableToolsAndAnswersAsRecorded(WorldCase c)
    {
        var world = c.Make();
        Assert.True(world.AvailableTools.All(t => world.AllTools.Any(a => a.Id == t.Id)), "AllTools covers AvailableTools");
        if (c.Hidden is null) return;

        Assert.DoesNotContain(world.AvailableTools, t => t.Id == c.Hidden);
        Assert.Equal(c.HiddenInAllTools, world.AllTools.Any(t => t.Id == c.Hidden));
        var before = world.Ticks;
        var outcome = world.Act(c.Hidden);
        Assert.False(outcome.Succeeded);
        Assert.Equal(c.HiddenObservation, outcome.Observation);
        Assert.Equal(c.TicksOnHidden ? before + 1 : before, world.Ticks);
    }

    [Theory]
    [MemberData(nameof(Worlds))]
    public void AToolThatPromisesNothingHasLandedOnlyWhereTheRegistryIsRead(WorldCase c)
    {
        // The registry worlds read EffectsLanded live (all of nothing is true); the opaque ones read the last gain (never run = false).
        var world = c.Make();
        Assert.Equal(c.VacuousEffectIsTrue, world.EffectsLanded(new Tool("promises-nothing", [], [], "read")));
    }

    [Fact]
    public void TheRouteRecorderClaimsEveryActRefusedOrNot()
    {
        var world = new RouteRecorder(Toy());
        world.Act("no-such-tool");
        world.Act("apply-patch");
        world.Act("inspect-logs");
        Assert.Equal(3, world.Claims.Count);
        Assert.Equal([false, false, true], world.Claims.Select(c => c.ReportedSuccess));
    }

    [Fact]
    public void TheOpaqueWorldReadsTheLastGainNotTheRegistry()
    {
        // Its own law, recorded as its own row: EffectsLanded is whether the registry gained anything the last time
        // the tool ran. Never run: false. Landed: true. Run again once its facts already hold: false, although the
        // registry holds everything the tool promised. A repeat is not a lie either: the inner world grants again.
        var world = new OpaqueWorld(Toy());
        var inspect = world.Tool("inspect-logs")!;
        Assert.Null(world.LastGain("inspect-logs"));
        Assert.False(world.EffectsLanded(inspect));

        world.Act("inspect-logs");
        Assert.True(world.EffectsLanded(inspect));
        Assert.Equal(["logs.inspected"], world.LastGain("inspect-logs")!.Order());

        world.Act("inspect-logs");
        Assert.True(world.Has("logs.inspected"));
        Assert.False(world.EffectsLanded(inspect), "the last run gained nothing, so the effect did not land");
        Assert.Empty(world.LastGain("inspect-logs")!);
        Assert.Equal(0, world.Lies);
        Assert.Equal(0, world.HonestFailures);
    }

    [Fact]
    public void TheOpaqueWorldCountsLiesAndHonestFailuresAtTheSource()
    {
        var world = new OpaqueWorld(Toy(patchLies: true));
        world.Act("inspect-logs");
        world.Act("identify-component");
        world.Act("apply-patch");
        Assert.Equal(1, world.Lies);
        world.Act("deploy");
        Assert.Equal(1, world.HonestFailures);
        Assert.Equal(4, world.Ticks);
    }
}
