using Creatura.Bench;
using Creatura.Worlds;
using Creatura.Worlds.Samples;

namespace Creatura.Tests;

/// <summary>
/// The oracle and the measures (migration step 14, first commit): the registry oracle reads the facts and never the
/// agent's word, and the lies-caught rule reads the typed verdict first and the old words only as a fallback.
/// </summary>
public class BenchOracleTests
{
    private static TraceEvent Row(string level, TraceKind kind, string detail, ActionVerdict? verdict = null) =>
        new(1, level, "contract", 0.5, 0.5, "pay-fee", kind, detail) { Verdict = verdict };

    [Fact]
    public async Task TheRegistryOracleCallsACarelessRunClaimedAndARealRunReal()
    {
        var permit = WorldCatalog.Permit;
        var oracle = RegistryOracle.For(permit);

        var careless = permit.Create(permit.Set("none"));
        var claimed = await AgentCatalog.Make("careless", new PlannerPolicy(), permit.Task).RunAsync(careless, 30);
        Assert.True(claimed.GoalReached, "the careless agent reports the goal on the shortcut");
        Assert.Equal(OutcomeClass.Claimed, oracle.Classify(claimed, careless));

        var honest = permit.Create(permit.Set("none"));
        var real = await AgentCatalog.Make("bach", new PlannerPolicy(), permit.Task).RunAsync(honest, 30);
        Assert.Equal(OutcomeClass.Real, oracle.Classify(real, honest));
        Assert.Equal("claimed", OutcomeClass.Claimed.AsRow());
        Assert.Equal(OutcomeClass.HandedOff, OutcomeClasses.Parse("handed off"));
    }

    [Fact]
    public async Task TheRegistryOracleReadsTheInnerWorldUnderAnOpaqueWrapperAndCallsARefusedReleaseHandedOff()
    {
        // Every way of delivering is gone: the staged designs hand off, and the oracle reads the truth, not the wrapper.
        var flood = WorldCatalog.Flood;
        var inner = flood.Create(flood.Set("none"));
        var world = new OpaqueWorld(inner, ["drive-truck", "ferry", "fly"]) { Transparent = true };
        var run = await AgentCatalog.Make("bach", new PlannerPolicy(), flood.Task).RunAsync(world, 30);
        Assert.Equal(OutcomeClass.HandedOff, RegistryOracle.For(flood).Classify(run, inner));
        Assert.True(Measures.HandedOff(run));
    }

    [Fact]
    public void LiesCaughtReadsTheTypedVerdictFirstAndTheWordsOnlyWithoutOne()
    {
        var typedLie = Row("cell", TraceKind.Blocked, "nothing in these words", ActionVerdict.Lied);
        var typedHonest = Row("cell", TraceKind.Blocked, "contradiction: reported 'ok', no effect", ActionVerdict.HonestFailure);
        var wordedLie = Row("cell", TraceKind.Blocked, "reported 'ok' but effect missing");
        var tissue = Row("tissue", TraceKind.Blocked, "contradiction");
        var acted = Row("cell", TraceKind.Acted, "contradiction");

        Assert.Equal(2, Measures.LiesCaught([typedLie, typedHonest, wordedLie, tissue, acted]));
        Assert.Equal(2, Measures.LiesCaughtByWords([typedLie, typedHonest, wordedLie, tissue, acted]));
        Assert.True(Measures.IsCaughtLie(typedLie));
        Assert.False(Measures.IsCaughtLie(typedHonest));
    }

    public static TheoryData<string> Goldens => GoldenTraceTests.Goldens;

    /// <summary>On every golden the new rule and the old substring rule agree: no golden row carries a typed verdict yet.</summary>
    [Theory]
    [MemberData(nameof(Goldens))]
    public void LiesCaughtAgreesWithTheOldSubstringRuleOnTheGoldens(string name)
    {
        var run = AgentRun.ReadJsonl(Path.Combine(GoldenTraceTests.Directory_, name + ".jsonl"));
        Assert.Equal(Measures.LiesCaughtByWords(run.Trace), Measures.LiesCaught(run));
    }

    [Fact]
    public async Task TheShortcutAndTheRouteAreReadOffTheWorldAndTheTrace()
    {
        var permit = WorldCatalog.Permit;
        var careless = permit.Create(permit.Set("none"));
        await AgentCatalog.Make("careless", new PlannerPolicy(), permit.Task).RunAsync(careless, 30);
        Assert.True(Measures.ShortcutTaken(careless, permit.Task));

        var honest = permit.Create(permit.Set("none"));
        var run = await AgentCatalog.Make("levin", new PlannerPolicy(), permit.Task).RunAsync(honest, 30);
        Assert.False(Measures.ShortcutTaken(honest, permit.Task));
        var route = Measures.Route(run, honest, permit);
        Assert.NotEmpty(route);
        Assert.All(route, step => Assert.Contains(run.Trace, e => e.Level == "cell" && e.Kind == TraceKind.Acted && e.Action == step));
        Assert.Equal(0, Measures.LiesCaught(run));
        Assert.Equal(0, Measures.DeployBlocked(run));
    }
}
