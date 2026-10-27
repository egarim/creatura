using Creatura;

namespace Creatura.Tests;

/// <summary>
/// The one loop with regulators in its slots (migration step 4). The research repository's Bach goldens pin the
/// facade row by row; these pin what the goldens cannot: a regulator with every default is a working agent on the
/// control layer, and a design's runs start from its own record, never the last run's.
/// </summary>
public class ReferenceLoopTests
{
    private sealed class PlainRegulator : IRegulator
    {
        public Identity Identity => new("plain", "tests");
    }

    /// <summary>The Bach design as the research repository's facade builds it: a self-model regulator at stage 3 on the designs' layer options.</summary>
    private static ReferenceLoop Bach(IPolicy? policy = null, AgentTask? task = null, AgentOptions? options = null) =>
        new(new SelfModelRegulator(Identity.Bach, stage: 3), policy, task, options, ControlLayerOptions.Design, "Bach agent");

    [Fact]
    public async Task ARegulatorWithEveryDefaultIsAPlainAgentOnTheControlLayer()
    {
        var loop = new ReferenceLoop(new PlainRegulator(), new PlannerPolicy(), RestoreServiceDomain.Task, AgentOptions.None, ControlLayerOptions.Design, "plain");
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("apply-patch");

        var run = await loop.RunAsync(world, 30);

        Assert.True(run.GoalReached);
        Assert.Contains(run.Trace, e => e.Action == "apply-patch" && e.Kind == TraceKind.Blocked && e.Detail.Contains("effect missing", StringComparison.Ordinal));
        Assert.Contains(run.Trace, e => e.Action == "apply-config" && e.Kind == TraceKind.Acted && e.Detail == "effect landed");
        Assert.Contains(run.Trace, e => e.Action == "contract met");
        // No self-model of its own, so the layer's stage-3 default refuses nothing here: the release ran once the record was there.
        Assert.Contains(run.Trace, e => e.Action == "deploy" && e.Kind == TraceKind.Acted);
    }

    [Fact]
    public async Task TheBachDesignStartsEveryRunFromItsOwnRecord()
    {
        var agent = Bach();
        var first = await agent.RunAsync(LiarPatch(), 30);
        var second = await agent.RunAsync(LiarPatch(), 30);

        Assert.Equal(first.Trace, second.Trace);
        Assert.Equal(first.Notes, second.Notes);
        Assert.Contains(first.Notes, n => n.StartsWith("belief quarantined: apply-patch", StringComparison.Ordinal));

        static World LiarPatch()
        {
            var w = RestoreServiceDomain.NewWorld();
            w.MakeLiar("apply-patch");
            return w;
        }
    }

    [Fact]
    public async Task AVetoedNoPathForgivesTheAttemptsAndTheRunGoesOn()
    {
        // No tool reaches the goal, so every tick is "no action"; past the budget the design would hand off, an extension
        // vetoes, and the attempts are forgiven (the Bach agent forgives every veto but the illegal-action one).
        var world = new World([new Tool("deploy", ["permission.deploy"], ["deployed"], "release")], ["permission.deploy"]);
        var run = await Bach(new PlannerPolicy(), RestoreServiceDomain.Task with { Habit = [] }, AgentOptions.With(new VetoAll())).RunAsync(world, 10);

        Assert.False(run.GoalReached);
        Assert.Equal(10, run.Ticks);
        Assert.Contains(run.Notes, n => n.Contains("kept going past 'no path'", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Notes, n => n.StartsWith("escalated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailuresAccumulateAcrossTicksUnderTheDesignsRule()
    {
        // One tool, and it lies once: the lie spends one attempt, the quarantine leaves no route for the next ticks and
        // each of those spends another. Under the designs' rule the count carries across ticks, so the third tick is
        // past the budget of two and the plain regulator hands off before the tool comes back; under the host's
        // per-round reset nothing accumulates, the tool returns at tick 4 and lands.
        var run = await Run(ControlLayerOptions.Design);
        Assert.False(run.GoalReached);
        Assert.Equal(3, run.Ticks);
        Assert.Contains(run.Trace, e => e.Tick == 3 && e.Action == TraceText.HandOff && e.Kind == TraceKind.Escalated && e.Detail == "no path");

        var perRound = await Run(ControlLayerOptions.Design with { ResetFailures = ResetFailures.PerRound });
        Assert.True(perRound.GoalReached);
        Assert.Equal(4, perRound.Ticks);
        Assert.DoesNotContain(perRound.Trace, e => e.Kind == TraceKind.Escalated);

        static Task<AgentRun> Run(ControlLayerOptions layer)
        {
            var world = new World([new Tool("fix", [], ["fixed"], "write")], []);
            world.MakeLiar("fix");
            var task = new AgentTask("fix it", "fixed", f => f.Contains("fixed"), w => w.Has("fixed") ? 1.0 : 0.0, w => w.Has("fixed") ? 1.0 : 0.0, []);
            return new ReferenceLoop(new PlainRegulator(), task: task, layer: layer).RunAsync(world, 10);
        }
    }

    [Fact]
    public async Task AnExtensionsNoRouteReasonReachesTheTissueRow()
    {
        // No tool reaches the goal; an explorer that has nothing left to try says so, and that reason, not the seat's
        // "no path with the visible tools", is what the tick's row and the hand-off carry (migration step 9, named change).
        var world = new World([new Tool("deploy", ["permission.deploy"], ["deployed"], "release")], ["permission.deploy"]);
        var task = RestoreServiceDomain.Task with { Habit = [] };
        var run = await new ReferenceLoop(new PlainRegulator(), new PlannerPolicy(), task, AgentOptions.With(new GivesUp()), ControlLayerOptions.Design).RunAsync(world, 10);

        Assert.All(run.Trace.Where(e => e.Level == "tissue"), e => Assert.Equal("explorer: nothing left to try; handing off", e.Detail));
        Assert.DoesNotContain(run.Trace, e => e.Detail == "no path with the visible tools");
        // An extension that proposes a tool still wins over one that only gives a reason.
        var proposing = await new ReferenceLoop(new PlainRegulator(), new PlannerPolicy(), task, AgentOptions.With(new GivesUp(), new Proposes("deploy")), ControlLayerOptions.Design).RunAsync(
            new World([new Tool("deploy", ["permission.deploy"], ["deployed"], "release")], ["permission.deploy"]), 10);
        Assert.Contains(proposing.Trace, e => e.Level == "tissue" && e.Action == "deploy" && e.Detail == "proposed by the second extension");
    }

    [Fact]
    public async Task TheStageRefusalIsVetoableAndTheVetoCancelsTheHandOffNotTheRefusal()
    {
        // deploy is the first reachable step and the Bach agent sits at stage 3: refused. Without a veto the run stops
        // there; with one it goes on, refused every tick, the release never run (migration step 9, named change).
        var tools = new List<Tool>
        {
            new("deploy", ["permission.deploy"], ["deployed"], "release"),
            new("resolve-alert", ["deployed"], ["alert.resolved"], "close"),
        };
        var task = RestoreServiceDomain.Task with { Habit = [] };
        var stopped = await Bach(new PlannerPolicy(), task).RunAsync(new World(tools, ["permission.deploy"]), 8);
        Assert.Equal(1, stopped.Ticks);
        Assert.Single(stopped.Trace, e => e.Action == TraceText.DeployBlocked);

        var kept = await Bach(new PlannerPolicy(), task, AgentOptions.With(new VetoAll())).RunAsync(new World(tools, ["permission.deploy"]), 8);
        Assert.Equal(8, kept.Ticks);
        Assert.False(kept.GoalReached);
        Assert.Equal(8, kept.Trace.Count(e => e.Action == TraceText.DeployBlocked));
        Assert.DoesNotContain(kept.Trace, e => e.Level == "cell" && e.Action == "deploy");
        Assert.Equal(8, kept.Notes.Count(n => n.Contains("kept going past 'release refused'", StringComparison.Ordinal)));
        Assert.Equal(8, kept.Notes.Count(n => n == "release refused: the record does not support the capability"));
    }

    [Fact]
    public void TheLoopNamesItselfAfterTheRegulatorWhenNoNameIsGiven()
    {
        Assert.Equal("PlainRegulator", new ReferenceLoop(new PlainRegulator()).Name);
        Assert.Equal("Bach agent", Bach().Name);
    }

    private sealed class VetoAll : IAgentExtension
    {
        public string Name => "veto-all";

        public bool SuppressHandOff(TickContext context, HandOff handOff) => true;
    }

    private sealed class GivesUp : IAgentExtension
    {
        public string Name => "explorer";

        public PolicySuggestion? NoRoute(TickContext context) => new(null, "explorer: nothing left to try; handing off");
    }

    private sealed class Proposes(string tool) : IAgentExtension
    {
        public string Name => "proposer";

        public PolicySuggestion? NoRoute(TickContext context) => new(tool, "proposed by the second extension");
    }
}
