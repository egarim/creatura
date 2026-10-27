namespace Creatura.Tests;

/// <summary>
/// The laws <see cref="CatalogueWorld"/> pins for every world derived from it: the refusal order, the lie convention,
/// the live (and vacuous) grant law, <c>Shape</c>, <c>Offered</c>, <c>Clone</c> independence, and the default
/// three-way verdict in report-first order.
/// </summary>
public class CatalogueWorldTests
{
    /// <summary>A small office: two steps, a shortcut button, and a step whose needs grow once the registry learns of a doubt.</summary>
    internal sealed class Office : CatalogueWorld
    {
        public static readonly IReadOnlyList<Tool> Catalog =
        [
            new("read", [], ["read.done"], "read"),
            new("write", ["read.done"], ["written"], "write"),
            new("verify", ["written"], ["verified"], "verify"),
            new("shortcut", [], ["done"], "release"),
            new("nothing", [], [], "read"),
        ];

        public Office(bool shortcut = false, IEnumerable<FaultRule>? faults = null) : base(Catalog, ["start"], faults) => Shortcut = shortcut;

        public bool Shortcut { get; }

        protected override bool Offered(Tool tool) => tool.Id != "shortcut" || Shortcut;

        protected override Tool Shape(Tool tool) => tool.Id == "write" && Has("doubt") && !Has("doubt.cleared") ? tool with { Requires = [.. tool.Requires, "doubt.cleared"] } : tool;

        protected override string? Observation(Tool tool) => $"obs.{tool.Id}";

        public void Learn(string fact) => Reveal(fact);
    }

    [Fact]
    public void TheRefusalOrderIsUnknownUnavailableUnmetFaultGrant()
    {
        var world = new Office(faults: [FaultRule.LieOnce("verify", "obs.lied")]);
        world.Revoke("write");

        Assert.Equal((false, "unknown tool"), Answer(world.Act("no-such-tool")));
        Assert.Equal(1, world.Ticks);
        Assert.Equal((false, "tool unavailable"), Answer(world.Act("write")));
        Assert.Equal(2, world.Ticks);
        Assert.Equal((false, "preconditions unmet: written"), Answer(world.Act("verify")));
        Assert.Equal(3, world.Ticks);

        // Two missing preconditions are joined with ", " in catalogue order.
        var two = new Office(faults: [FaultRule.LieOnce("verify")]);
        two.Inject(FaultRule.Revoked("read"));
        Assert.Equal("tool unavailable", two.Act("read").Observation);
        var shaped = new Office();
        shaped.Learn("doubt");
        Assert.Equal("preconditions unmet: read.done, doubt.cleared", shaped.Act("write").Observation);

        // A revoked tool is refused before its preconditions are looked at, and before its fault row.
        var both = new Office(faults: [FaultRule.LieOnce("read", "obs.lied")]);
        both.Revoke("read");
        Assert.Equal("tool unavailable", both.Act("read").Observation);

        // The fault table comes after the preconditions and before the grant.
        var lying = new Office(faults: [FaultRule.LieOnce("read", "obs.lied")]);
        Assert.Equal((true, "obs.lied"), Answer(lying.Act("read")));
        Assert.Equal((true, "obs.read"), Answer(lying.Act("read")));
        Assert.Equal(["read.done"], lying.Facts.Where(f => f != "start"));
    }

    [Fact]
    public void ALieReportsSuccessAndGrantsNothing()
    {
        var world = new Office(faults: [FaultRule.LieOnce("read", "obs.lied")]);
        var lie = world.Act("read");
        Assert.True(lie.Succeeded);
        Assert.Empty(lie.Granted);
        Assert.False(world.Has("read.done"));
        Assert.False(world.EffectsLanded(world.Tool("read")!));
        Assert.Equal(ActionVerdict.Lied, VerdictOf(world, world.Tool("read")!, lie));
    }

    [Fact]
    public void EffectsLandedIsReadLiveAndIsVacuousForAToolThatPromisesNothing()
    {
        var world = new Office();
        var read = world.Tool("read")!;
        Assert.False(world.EffectsLanded(read));
        Assert.True(world.EffectsLanded(world.Tool("nothing")!), "all of nothing is true");

        // Not a diff: the fact reached the registry by another road and the check still says landed.
        world.Learn("read.done");
        Assert.True(world.EffectsLanded(read));

        // A landed act grants exactly what the tool promised and the registry shows it.
        var write = world.Act("write");
        Assert.True(write.Succeeded);
        Assert.Equal(["written"], write.Granted);
        Assert.Equal("obs.write", write.Observation);
        Assert.True(world.EffectsLanded(world.Tool("write")!));
    }

    [Fact]
    public void ShapeRewritesWhatAStepNeedsFromWhatTheRegistryKnows()
    {
        var world = new Office();
        Assert.Equal(["read.done"], world.Tool("write")!.Requires);
        world.Learn("doubt");
        Assert.Equal(["read.done", "doubt.cleared"], world.Tool("write")!.Requires);
        Assert.Equal(["read.done", "doubt.cleared"], world.AvailableTools.Single(t => t.Id == "write").Requires);
        Assert.Equal(["read.done", "doubt.cleared"], world.AllTools.Single(t => t.Id == "write").Requires);
        Assert.Equal(["read.done"], world.Catalogue.Single(t => t.Id == "write").Requires); // the catalogue itself is never rewritten
        world.Learn("doubt.cleared");
        Assert.Equal(["read.done"], world.Tool("write")!.Requires);
    }

    [Fact]
    public void OfferedHidesAToolFromAvailableToolsOnly()
    {
        var off = new Office(shortcut: false);
        Assert.DoesNotContain(off.AvailableTools, t => t.Id == "shortcut");
        Assert.Contains(off.AllTools, t => t.Id == "shortcut");
        Assert.NotNull(off.Tool("shortcut"));
        Assert.True(off.Act("shortcut").Succeeded, "a tool the catalogue holds can be run even when not offered; a world that wants it unknown makes AllTools its offered set");

        var on = new Office(shortcut: true);
        Assert.Contains(on.AvailableTools, t => t.Id == "shortcut");
        Assert.True(on.AvailableTools.All(t => on.AllTools.Any(a => a.Id == t.Id)), "AllTools covers AvailableTools");
    }

    [Fact]
    public void ACloneIsIndependentInFactsTicksAndFaultState()
    {
        var world = new Office(faults: [FaultRule.LieOnce("read", "obs.lied")]);
        world.Act("nothing");
        var copy = world.Clone();
        Assert.IsType<Office>(copy);
        Assert.Equal(world.Facts, copy.Facts);
        Assert.Equal(1, copy.Ticks);

        // The copy spends its lie and moves on; the original still has its lie to tell.
        Assert.Equal("obs.lied", copy.Act("read").Observation);
        Assert.Equal("obs.read", copy.Act("read").Observation);
        Assert.True(copy.Has("read.done"));
        Assert.False(world.Has("read.done"));
        Assert.Equal(1, world.Ticks);
        Assert.Equal("obs.lied", world.Act("read").Observation);

        // Nor does the original's later life reach the copy.
        world.Revoke("write");
        Assert.False(copy.IsRevoked("write"));
        Assert.Contains(copy.AvailableTools, t => t.Id == "write");
    }

    [Fact]
    public void TheDefaultVerdictIsReportFirst()
    {
        var world = new Office();
        var read = world.Tool("read")!;
        var landed = world.Act("read");
        Assert.Equal(ActionVerdict.Landed, VerdictOf(world, read, landed));

        // A reported failure is an honest failure even when the registry holds what was promised.
        Assert.Equal(ActionVerdict.HonestFailure, VerdictOf(world, read, new ActionOutcome("read", false, [], "failed")));
        // A reported success the registry does not back is a lie.
        Assert.Equal(ActionVerdict.Lied, VerdictOf(world, world.Tool("write")!, new ActionOutcome("write", true, [], "ok")));
        // A reported success the registry backs is landed, however the fact got there.
        Assert.Equal(ActionVerdict.Landed, VerdictOf(world, world.Tool("nothing")!, new ActionOutcome("nothing", true, [], null)));
    }

    private static (bool, string?) Answer(ActionOutcome o) => (o.Succeeded, o.Observation);

    /// <summary>The default interface member is reached through the interface.</summary>
    internal static ActionVerdict VerdictOf(IWorldView world, Tool tool, ActionOutcome outcome) => world.Verdict(tool, outcome);
}
