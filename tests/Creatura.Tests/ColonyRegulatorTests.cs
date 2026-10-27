using Creatura;

namespace Creatura.Tests;

/// <summary>
/// The colony regulator behind the research repository's Levin facade (migration step 5). The Levin goldens pin
/// every row; these pin the two rules the goldens show only by their absence: the colony stages nothing, and its
/// organism's only lever short of a hand-off is to widen the budget and say so.
/// </summary>
public class ColonyRegulatorTests
{
    /// <summary>The colony as the Levin facade builds it: the colony regulator on the designs' layer options, with no route lifting the quarantine at once.</summary>
    private static ReferenceLoop Colony(IPolicy? policy = null, AgentTask? task = null, AgentOptions? options = null) =>
        new(new ColonyRegulator(Identity.Levin, leak: 0.5, ErrorChannel.Tissue), policy, task, options, ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true }, "Levin colony");

    [Fact]
    public async Task TheColonyStagesNothingSoAReleaseIsAStepLikeAnyOther()
    {
        // The world the Bach agent and the host refuse at stage 3: deploy is the first reachable step.
        var tools = new List<Tool>
        {
            new("deploy", ["permission.deploy"], ["deployed"], "release"),
            new("resolve-alert", ["deployed"], ["alert.resolved"], "close"),
        };
        var world = new World(tools, ["permission.deploy"]);

        var run = await Colony(new PlannerPolicy(), RestoreServiceDomain.Task with { Habit = [] }).RunAsync(world, 30);

        Assert.True(run.GoalReached);
        Assert.Equal(2, run.Ticks);
        Assert.DoesNotContain(run.Trace, e => e.Kind == TraceKind.Escalated || e.Action == "deploy blocked");
        Assert.DoesNotContain(run.Notes, n => n.Contains("stage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOrganismWidensTheBudgetBeforeItHandsOff()
    {
        // No tool at all: the tissue's stress is 1, which buys the whole budget of 4; each fruitless tick is noted, the fifth hands off.
        var world = new World([], []);

        var run = await Colony().RunAsync(world, 30);

        Assert.False(run.GoalReached);
        Assert.Equal(5, run.Ticks);
        Assert.Equal(4, run.Notes.Count(n => n.StartsWith("organism shaping: budget 4/4, attempts ", StringComparison.Ordinal)));
        Assert.Contains("organism shaping: budget 4/4, attempts 1 — widening autonomy (no path with the visible tools)", run.Notes);
        Assert.Contains(run.Notes, n => n.StartsWith("organism: goal is not regulatable (no path with the visible tools)", StringComparison.Ordinal));
        var handOff = Assert.Single(run.Trace, e => e.Kind == TraceKind.Escalated);
        Assert.Equal("hand off", handOff.Action);
        Assert.Equal("stuck regulation after 5 attempts", handOff.Detail);
        Assert.Equal("quarantined at end: none", run.Notes[^1]);
    }

    [Fact]
    public async Task AVetoForgivesEveryAttemptInTheColony()
    {
        var world = new World([], []);
        var run = await Colony(options: AgentOptions.With(new VetoAll())).RunAsync(world, 12);

        Assert.Equal(12, run.Ticks);
        Assert.DoesNotContain(run.Trace, e => e.Kind == TraceKind.Escalated);
        // Forgiven at tick 5, the count starts again: ticks 6-9 are attempts 1-4, tick 10 is vetoed again, 11-12 are attempts 1-2.
        Assert.Equal(2, run.Notes.Count(n => n.Contains("kept going past 'no route'", StringComparison.Ordinal)));
        Assert.Equal(10, run.Notes.Count(n => n.StartsWith("organism shaping: budget 4/4", StringComparison.Ordinal)));
    }

    private sealed class VetoAll : IAgentExtension
    {
        public string Name => "veto-all";

        public bool SuppressHandOff(TickContext context, HandOff handOff) => true;
    }
}
