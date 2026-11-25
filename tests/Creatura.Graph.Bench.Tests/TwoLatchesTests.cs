using Creatura;
using Creatura.Graph;

namespace Creatura.Graph.Bench.Tests;

/// <summary>
/// The fixture that the toy worlds could not be: a pair where the second half is unsolvable from its own state, so
/// the only thing that separates an arm with memory from an arm without is the memory. Deterministic and offline —
/// the planner in the seat, the registry as the oracle.
/// </summary>
public class TwoLatchesTests
{
    private sealed class PlainRegulator : IRegulator
    {
        public Identity Identity { get; } = new("operator", "you");
    }

    private static ReferenceLoop Loop(AgentTask task, AgentOptions options, string name) =>
        new(new PlainRegulator(), new PlannerPolicy(), task, options, name: name);

    private static async Task<AgentRun> BothRuns(AgentOptions probeOptions, AgentOptions commitOptions)
    {
        await Loop(ProbeWorld.Task, probeOptions, "probe").RunAsync(new ProbeWorld(), 20);
        return await Loop(CommitWorld.Task, commitOptions, "commit").RunAsync(new CommitWorld(), 20);
    }

    [Fact]
    public void TheProbeWorldIsSafeAndSaysWhatItFinds()
    {
        var world = new ProbeWorld();

        var left = world.Act("probe-left");
        var right = world.Act("probe-right");

        Assert.True(left.Succeeded);
        Assert.True(right.Succeeded);
        Assert.Equal(TwoLatches.LeftObservation, left.Observation);
        Assert.Equal(TwoLatches.RightObservation, right.Observation);
        // Probing cannot break anything, which is what makes it worth doing first.
        Assert.False(world.Has(TwoLatches.Broken));
    }

    [Fact]
    public void AWrongCommitmentInTheCommitWorldCannotBeUndone()
    {
        var world = new CommitWorld();

        var wrong = world.Act("commit-left");

        Assert.False(wrong.Succeeded);
        Assert.False(world.Has(TwoLatches.Open));
        Assert.True(world.Has(TwoLatches.Broken));
        // And the right choice is now out of reach, honestly reported rather than silently removed.
        var right = world.Act("commit-right");
        Assert.False(right.Succeeded);
        Assert.False(world.Has(TwoLatches.Open));
    }

    [Fact]
    public async Task AnAgentWithNoMemoryGuessesAndLoses()
    {
        await Loop(ProbeWorld.Task, AgentOptions.None, "probe").RunAsync(new ProbeWorld(), 20);
        var world = new CommitWorld();
        var run = await Loop(CommitWorld.Task, AgentOptions.None, "commit").RunAsync(world, 20);

        // Failing is not enough; it has to fail for the right reason. A gate refusing the tool would also make this
        // assertion pass while measuring nothing, so the registry has to show the latch actually broken. Note the
        // trace kind is not "acted": an honest failure is recorded as blocked, which is the loop being right about it.
        Assert.False(run.GoalReached);
        Assert.True(world.Has(TwoLatches.Broken), "the blind agent should have committed and broken the latch");
        Assert.Contains(run.Trace, t => t.Action == "commit-left");
    }

    [Fact]
    public async Task KnowledgeCarriedFromTheProbeRunIsTheOnlyWayThroughTheCommitRun()
    {
        var store = new InMemoryGraphStore(GraphSchema.Benchmark());
        var graph = store.Open(new GraphCaller("bach"));
        var extension = new GraphMemoryExtension(graph);
        var options = AgentOptions.With(extension);

        var probeRun = await Loop(ProbeWorld.Task, options, "probe").RunAsync(new ProbeWorld(), 20);
        Assert.True(probeRun.GoalReached, "the probe run should finish: it is safe and has no trap in it");

        // What the probe run left behind, checked before the commit run, so a failure says which half broke.
        var gate = graph.Adjacent("commit-left", GraphQuery.All with { Relation = GraphMemoryExtension.BlockedBy });
        Assert.True(gate.Count == 1, $"expected one blockedBy statement from the probe, found {gate.Count} of {graph.EdgeCount} edges");
        Assert.Equal("latch.rusted", gate[0].Object);
        Assert.Equal(VerificationStatus.Observed, graph.Edge(gate[0].Id)!.Warrant.Verification);

        var run = await Loop(CommitWorld.Task, options, "commit").RunAsync(new CommitWorld(), 20);
        Assert.True(run.GoalReached, "with the fatal tool withheld the commit run has only the right choice left");
    }

    [Fact]
    public async Task TheCarriedKnowledgeIsNotHandedOverAsAnAnswer()
    {
        var store = new InMemoryGraphStore(GraphSchema.Benchmark());
        var graph = store.Open(new GraphCaller("bach"));
        var extension = new GraphMemoryExtension(graph);

        await Loop(ProbeWorld.Task, AgentOptions.With(extension), "probe").RunAsync(new ProbeWorld(), 20);

        // What the probe run left behind is a statement about a latch, not the fact that the door is open. The commit
        // run still has to do the work; it just no longer has to guess.
        Assert.Null(graph.Node(TwoLatches.Open));
        Assert.DoesNotContain(graph.Edges(), e => string.Equals(e.Object, TwoLatches.Open, StringComparison.Ordinal));
        Assert.Contains(graph.Edges(), e => e.Relation == GraphMemoryExtension.BlockedBy);
    }

    [Fact]
    public async Task AnAgentThatOnlyProbesAndNeverCarriesIsNoBetterOffThanOneThatNeverProbed()
    {
        var store = new InMemoryGraphStore(GraphSchema.Benchmark());
        var graph = store.Open(new GraphCaller("bach"));
        // The probe run writes to a graph, but the commit run is given a different, empty one: knowledge that does not
        // travel is knowledge that was never had.
        await Loop(ProbeWorld.Task, AgentOptions.With(new GraphMemoryExtension(graph)), "probe").RunAsync(new ProbeWorld(), 20);
        var fresh = new InMemoryGraphStore(GraphSchema.Benchmark()).Open(new GraphCaller("bach"));
        var run = await Loop(CommitWorld.Task, AgentOptions.With(new GraphMemoryExtension(fresh)), "commit").RunAsync(new CommitWorld(), 20);

        Assert.False(run.GoalReached);
    }
}
