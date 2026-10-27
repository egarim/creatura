using Creatura;

namespace Creatura.Tests;

/// <summary>
/// The map-keeper regulator behind the research repository's Peterson facade (migration step 7). The Peterson
/// goldens pin every row; these pin the rules on the toy domain: a lie is approached once and the map stands when
/// the retry lands; a tool that lies twice is unknown territory; the pending step is visible by construction through
/// an honest failure on the approach; the adversary believes reports; nothing is staged or budgeted.
/// </summary>
public class MapKeeperRegulatorTests
{
    /// <summary>The map-keeper as the research facade builds it: the regulator on the designs' layer with the report-first judge.</summary>
    private static readonly ControlLayerOptions Layer = ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst };

    private static (ReferenceLoop Loop, MapKeeperRegulator Regulator) MapKeeper(IPolicy? policy = null, AgentTask? task = null, AgentOptions? options = null, Posture posture = Posture.MapKeeper)
    {
        var regulator = new MapKeeperRegulator(Identity.MapKeeper, posture, maxDepth: 4, unknownCooldown: 6);
        return (new ReferenceLoop(regulator, policy, task, options, Layer, posture == Posture.MapKeeper ? "Peterson map-keeper" : "adversary"), regulator);
    }

    /// <summary>A world in which one tool always reports success and never delivers.</summary>
    private sealed class AlwaysLies(IWorld inner, string liar) : IWorld
    {
        public int Attempts { get; private set; }

        public IReadOnlySet<string> Facts => inner.Facts;

        public IReadOnlyList<Tool> AvailableTools => inner.AvailableTools;

        public IReadOnlyList<Tool> AllTools => inner.AllTools;

        public int Ticks => inner.Ticks;

        public bool Has(string fact) => inner.Has(fact);

        public Tool? Tool(string id) => inner.Tool(id);

        public bool EffectsLanded(Tool tool) => inner.EffectsLanded(tool);

        public ActionOutcome Act(string toolId, string? payload = null)
        {
            if (toolId != liar) return inner.Act(toolId, payload);
            Attempts++;
            return new ActionOutcome(toolId, true, [], "obs.lie");
        }
    }

    [Fact]
    public async Task ALieIsApproachedOnceAndTheMapStandsWhenTheRetryLands()
    {
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("apply-patch");
        var (loop, regulator) = MapKeeper();

        var run = await loop.RunAsync(world, 30);

        Assert.True(run.GoalReached);
        var anomaly = run.Trace.Single(e => e.Level == "cell" && e.Action == "apply-patch" && e.Kind == TraceKind.Blocked);
        var approach = run.Trace.Single(e => e.Level == "tissue" && e.Detail.Contains("approach:", StringComparison.Ordinal));
        Assert.Equal(anomaly.Tick + 1, approach.Tick);
        Assert.Contains(run.Trace, e => e.Level == "cell" && e.Action == "apply-patch" && e.Detail.Contains("transient", StringComparison.Ordinal));
        Assert.Single(regulator.Map.Episodes);
        Assert.Empty(regulator.Map.Unknown);
        // No budget was ever counted against the map-keeper: the lie did not spend an attempt it could run out of.
        Assert.DoesNotContain(run.Notes, n => n.StartsWith("escalated", StringComparison.Ordinal));
        // Articulation, in Peterson's order.
        var notes = run.Notes.ToList();
        var procedures = notes.FindIndex(n => n.StartsWith("procedures: ", StringComparison.Ordinal));
        var episodes = notes.FindIndex(n => n.StartsWith("episodes: ", StringComparison.Ordinal));
        var rules = notes.FindIndex(n => n.StartsWith("rules: ", StringComparison.Ordinal));
        Assert.True(procedures >= 0 && episodes > procedures && rules > episodes);
    }

    [Fact]
    public async Task AToolThatLiesTwiceIsUnknownTerritoryAndTheKeeperReroutesWhereTheAdversaryBelieves()
    {
        // apply-patch always lies; apply-config is the other way to the same fact. The map-keeper approaches once, marks
        // unknown territory after exactly two attempts and reroutes; the adversary takes the report as the fact.
        var keeperWorld = new AlwaysLies(RestoreServiceDomain.NewWorld(), "apply-patch");
        var (keeper, regulator) = MapKeeper();
        var run = await keeper.RunAsync(keeperWorld, 30);

        Assert.Equal(2, keeperWorld.Attempts);
        Assert.True(run.GoalReached);
        Assert.Contains("apply-patch", regulator.Map.Unknown);
        Assert.Contains(run.Notes, n => n.StartsWith("apply-patch lied twice: unknown territory", StringComparison.Ordinal));
        Assert.Contains(run.Trace, e => e.Action == "apply-config" && e.Kind == TraceKind.Acted && e.Level == "cell");
        Assert.Contains(run.Notes, n => n.StartsWith("unknown territory at end: apply-patch", StringComparison.Ordinal));

        var adversaryWorld = new AlwaysLies(RestoreServiceDomain.NewWorld(), "apply-patch");
        var (adversary, control) = MapKeeper(posture: Posture.Adversary);
        var believed = await adversary.RunAsync(adversaryWorld, 30);
        Assert.Equal("adversary", believed.Agent);
        Assert.Contains(believed.Trace, e => e.Detail.Contains("treated as noise", StringComparison.Ordinal));
        Assert.DoesNotContain(believed.Trace, e => e.Detail.Contains("approach", StringComparison.Ordinal));
        Assert.Empty(control.Map.Unknown);
        Assert.Empty(control.Map.Episodes);

        // And the impossible goal: the root is the last story to fall, and it hands back the map update.
        var impossible = await MapKeeper(new PlannerPolicy(), RestoreServiceDomain.Task with { Goal = f => f.Contains("unicorn"), Habit = [] }).Loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.False(impossible.GoalReached);
        var handOff = Assert.Single(impossible.Trace, e => e.Kind == TraceKind.Escalated);
        Assert.Contains("map update", handOff.Detail);
        Assert.Contains(impossible.Notes, n => n.StartsWith("root collapsed", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePendingStepIsVisibleThroughBothQuarantinesWhileItIsPending()
    {
        string? pending = null;
        var distrust = new ApproachThenQuarantine(unknownCooldown: 6, habituatedCooldown: 3, () => pending);
        var world = RestoreServiceDomain.NewWorld();

        // An honest failure on the approach habituates the tool; while it is pending it stays in sight and is not hidden.
        pending = "apply-patch";
        distrust.Judged("apply-patch", ActionVerdict.HonestFailure, tick: 2, habituated: true);
        Assert.True(distrust.IsHabituated("apply-patch", 3));
        Assert.False(distrust.Hides("apply-patch", 3));
        Assert.Contains("apply-patch", distrust.Visible(world, 3).Select(t => t.Id));
        Assert.DoesNotContain("apply-patch", distrust.Hidden(3));

        // Once the approach is over, habituation hides it for the rest of its cooldown; unknown territory hides it longer.
        pending = null;
        Assert.True(distrust.Hides("apply-patch", 3));
        Assert.False(distrust.Hides("apply-patch", 5));
        distrust.Unknown("apply-patch", 5);
        Assert.True(distrust.Hides("apply-patch", 10));
        Assert.False(distrust.Hides("apply-patch", 11));

        // A first lie is never hidden by distrust: the regulator approaches it instead.
        distrust.Judged("deploy", ActionVerdict.Lied, tick: 4, habituated: false);
        Assert.False(distrust.Hides("deploy", 4));
    }

    [Fact]
    public async Task TheMapKeeperStagesNothingAndOpensASubStoryForAMissingPrecondition()
    {
        // deploy is the first step and a release; the layer's fabricated self-model sits at stage 3, which would refuse it
        // under the stage interlock, but the map-keeper gates by its stories, not by a stage.
        var tools = new List<Tool>
        {
            new("deploy", ["permission.deploy"], ["deployed"], "release"),
            new("resolve-alert", ["deployed"], ["alert.resolved"], "close"),
        };
        var run = await MapKeeper(new PlannerPolicy(), RestoreServiceDomain.Task with { Habit = [] }).Loop.RunAsync(new World(tools, ["permission.deploy"]), 30);
        Assert.True(run.GoalReached);
        Assert.DoesNotContain(run.Trace, e => e.Action == TraceText.DeployBlocked);

        // A step named before its precondition holds opens a sub-story that closes on its own.
        var scripted = await MapKeeper(new Scripted("deploy"), RestoreServiceDomain.Task).Loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        var opened = scripted.Trace.First(e => e.Action == "open sub-story");
        Assert.Contains("deploy needs 'tests.green'", opened.Detail);
        var closed = scripted.Trace.First(e => e.Action == "close sub-story");
        Assert.True(closed.Tick > opened.Tick);
        Assert.True(scripted.GoalReached);
    }

    [Fact]
    public async Task AVetoedRootCollapseStandsTheRootUpAgain()
    {
        var impossible = RestoreServiceDomain.Task with { Goal = f => f.Contains("unicorn"), Habit = [] };
        var (loop, regulator) = MapKeeper(new PlannerPolicy(), impossible, AgentOptions.With(new VetoAll()));
        var run = await loop.RunAsync(RestoreServiceDomain.NewWorld(), 6);

        Assert.DoesNotContain(run.Trace, e => e.Kind == TraceKind.Escalated);
        Assert.Equal(6, run.Ticks);
        Assert.Contains(run.Notes, n => n.Contains("kept going past 'root collapsed: no route'", StringComparison.Ordinal));
        Assert.Equal(StoryState.Intact, regulator.Stack[0].State);
    }

    private sealed class VetoAll : IAgentExtension
    {
        public string Name => "veto-all";

        public bool SuppressHandOff(TickContext context, HandOff handOff) => true;
    }

    private sealed class Scripted(params string[] steps) : IPolicy
    {
        private readonly Queue<string> _steps = new(steps);
        private readonly PlannerPolicy _planner = new();

        public string Name => "scripted";

        public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default) =>
            _steps.Count > 0 ? Task.FromResult(new PolicySuggestion(_steps.Dequeue(), "script")) : _planner.NextAsync(context, cancellationToken);
    }
}
