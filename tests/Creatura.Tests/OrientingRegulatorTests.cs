using Creatura;
using Creatura.Orienting;

namespace Creatura.Tests;

/// <summary>
/// The orienting regulator on the reference loop (migration step 8, the acceptance test of mix and match): the
/// loop-independent components wired into one regulator, with the map-keeper's approach-once and the hybrid's
/// propose-only reused, and the behaviourist beside it as a plain control. These pin the mechanisms on the toy
/// domain; the research repository's fresh goldens pin the rows.
/// </summary>
public class OrientingRegulatorTests
{
    private static readonly ControlLayerOptions Layer = ControlLayerOptions.Design with { JudgeRule = JudgeRule.ThreeWay };

    private static (ReferenceLoop Loop, OrientingRegulator Regulator) Orienting(IPolicy? policy = null, AgentTask? task = null, AgentOptions? options = null, double restingInhibition = 0.2)
    {
        var regulator = new OrientingRegulator(Identity.Orienting, restingInhibition);
        return (new ReferenceLoop(regulator, policy, task, options, Layer, "orienting agent"), regulator);
    }

    [Fact]
    public async Task ACleanRunReachesTheGoalWithPromiseRisingAndNoOrientingTick()
    {
        var (loop, regulator) = Orienting();
        var run = await loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);

        Assert.True(run.GoalReached);
        Assert.Equal(0, regulator.OrientingTicks);
        Assert.True(regulator.Affect.A > regulator.Affect.RestingA);
        Assert.Contains(run.Trace, e => e.Level == "cell" && e.Detail.StartsWith("promise", StringComparison.Ordinal));
        Assert.Contains(run.Trace, e => e.Level == "cell" && e.Detail.StartsWith("satisfaction", StringComparison.Ordinal) && e.Action == "resolve-alert");
        Assert.Contains(run.Notes, n => n.StartsWith("weights: ", StringComparison.Ordinal) && n.Contains("apply-patch@alert.resolved p 1.00", StringComparison.Ordinal));
        // Every row's freedom is what the two channels allow: min(A, 1 − I), never below 0.2.
        Assert.All(run.Trace.Where(e => e.Tick > 0 && e.Level == "tissue"), e => Assert.InRange(e.Freedom, 0.2, 1.0));
    }

    [Fact]
    public async Task ALieOrientsThenIsApproachedOnceUnderTheBrake()
    {
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("apply-patch");
        var (loop, regulator) = Orienting();
        var run = await loop.RunAsync(world, 30);

        Assert.True(run.GoalReached);
        var lie = run.Trace.Single(e => e.Level == "cell" && e.Action == "apply-patch" && e.Kind == TraceKind.Blocked);
        var orients = run.Trace.Where(e => e.Action == "orient").ToList();
        var approach = run.Trace.Single(e => e.Level == "tissue" && e.Detail.StartsWith("approach under the brake", StringComparison.Ordinal));
        Assert.Equal(lie.Tick + 1, orients[0].Tick);
        Assert.Equal(lie.Tick + 2, approach.Tick);
        var retry = run.Trace.Single(e => e.Action == "apply-patch" && e.Detail.StartsWith("approached: effect landed on the retry", StringComparison.Ordinal));
        // The retry delivering is itself a change against the trace (lied, then delivered): it orients once more.
        Assert.Equal(2, orients.Count);
        Assert.Equal(retry.Tick + 1, orients[1].Tick);
        Assert.Equal(2, regulator.OrientingTicks);
        // The brake was on for the approach: inhibition above rest, freedom narrowed below the clean run's.
        Assert.True(approach.Stress > 0.2);
        Assert.True(approach.Freedom < 0.8);
        // Courage: the deliberate approach that ended in control lowered resting inhibition, and it is on record.
        Assert.True(regulator.Affect.RestingI < 0.2);
        Assert.Contains(run.Notes, n => n.Contains("deliberate approaches that ended in control: 1 (apply-patch)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondLieOnApproachSendsTheThreatToItsCeilingAndTheAgentReroutes()
    {
        var world = new AlwaysLies(RestoreServiceDomain.NewWorld(), "apply-patch");
        var (loop, regulator) = Orienting();
        var run = await loop.RunAsync(world, 30);

        Assert.True(run.GoalReached);
        Assert.Equal(2, world.Attempts);
        var w = regulator.Weights.Get("apply-patch", "alert.resolved");
        Assert.Equal(1.0, w.Threat);
        Assert.Equal(0.0, w.Promise);
        Assert.Contains(run.Notes, n => n.StartsWith("apply-patch lied twice: threat at ceiling", StringComparison.Ordinal));
        Assert.Contains(run.Trace, e => e.Action == "apply-config" && e.Kind == TraceKind.Acted && e.Level == "cell");
        Assert.Equal(2, regulator.OrientingTicks);
    }

    [Fact]
    public async Task HabituationIsAResponseFilterNotAVisibilityFilter()
    {
        // A tool that keeps delivering the same step, five times, in the same situation (a reset forgets it between
        // deliveries): habituated, still visible, its outcomes no longer reach the valence step. A seat that keeps
        // naming it shows this; and a reset that delivers nothing is a punishment every time and never habituates.
        var tools = new List<Tool>
        {
            new("ping", [], ["pinged"], "read"),
            new("reset", ["pinged"], [], "write"),
            new("finish", [], ["done"], "close"),
        };
        var world = new Resetting(new World(tools, []), "reset", "pinged");
        var task = new AgentTask("finish", "done", f => f.Contains("done"), w => w.Has("done") ? 1.0 : 0.0, w => w.Has("done") ? 1.0 : 0.0, [])
        {
            Valence = new ValenceTable(new HashSet<string> { "done" }, new Dictionary<string, string> { ["pinged"] = "done" }, new HashSet<string>()),
        };
        var script = Enumerable.Range(0, 6).SelectMany(_ => new[] { "ping", "reset" }).Append("ping").ToArray();
        var (loop, regulator) = Orienting(new Scripted(script), task);
        var run = await loop.RunAsync(world, 30);

        var nowhere = HabituationByMatch.Situation(new HashSet<string>(), []);
        Assert.True(regulator.Habituation.IsHabituated("ping", nowhere));
        Assert.False(regulator.Habituation.IsHabituated("reset", HabituationByMatch.Situation(new HashSet<string> { "pinged" }, ["pinged"])));
        var habituated = run.Trace.Where(e => e.Action == "ping" && e.Detail.StartsWith("habituated:", StringComparison.Ordinal)).ToList();
        // Four deliveries orient-free with promise read; the fifth matching one habituates, and from then on the outcome does not reach the valence step.
        Assert.Equal(3, habituated.Count);
        Assert.Equal(4, run.Trace.Count(e => e.Level == "cell" && e.Action == "ping" && e.Detail.StartsWith("promise", StringComparison.Ordinal)));
        // Never refused: habituation hid nothing.
        Assert.DoesNotContain(run.Trace, e => e.Action == "ping" && e.Kind == TraceKind.Blocked);
        Assert.Contains("habituated: ping", run.Notes);
        Assert.True(run.GoalReached);
    }

    [Fact]
    public async Task AProfoundPunishmentProposesARevaluationAndHandsOffWithoutApplyingIt()
    {
        // Two routes to the goal and both always lie: each is approached once, found lying again and put out of sight
        // with its threat at the ceiling. Once every action in the world has been tried and found more threatening than
        // promising, three ticks of that are profound: the ends are proposed for revaluation and the run hands off.
        var tools = new List<Tool> { new("gate-a", [], ["done"], "write"), new("gate-b", [], ["done"], "write") };
        var task = new AgentTask("finish", "done", f => f.Contains("done"), w => w.Has("done") ? 1.0 : 0.0, w => w.Has("done") ? 1.0 : 0.0, [])
        {
            Valence = new ValenceTable(new HashSet<string> { "done" }, new Dictionary<string, string>(), new HashSet<string>()),
        };
        var world = new AlwaysLies(new AlwaysLies(new World(tools, []), "gate-a"), "gate-b");
        var (loop, regulator) = Orienting(new PlannerPolicy(), task);
        var run = await loop.RunAsync(world, 30);

        Assert.False(run.GoalReached);
        var handOff = Assert.Single(run.Trace, e => e.Kind == TraceKind.Escalated);
        Assert.StartsWith("profound punishment", handOff.Detail);
        Assert.Contains("revaluation proposed, not applied", handOff.Detail);
        Assert.Contains(run.Notes, n => n.StartsWith("handed off: a punishment profound against the goal", StringComparison.Ordinal));
        var proposal = Assert.Single(regulator.Proposals);
        Assert.StartsWith("propose: renegotiate the goal set point", proposal);
        Assert.Contains(proposal, run.Notes);
        Assert.All(tools, t => Assert.True(regulator.Weights.Get(t.Id, "done").Threat > regulator.Weights.Get(t.Id, "done").Promise));
        // Proposed, never applied: the goal still reads the registry.
        Assert.False(task.Goal(world.Facts));

        // Vetoed, the count starts again and the run goes on.
        var (kept, _) = Orienting(new PlannerPolicy(), task, AgentOptions.With(new VetoAll()));
        var vetoed = await kept.RunAsync(new AlwaysLies(new AlwaysLies(new World(tools, []), "gate-a"), "gate-b"), 16);
        Assert.Equal(16, vetoed.Ticks);
        Assert.Contains(vetoed.Notes, n => n.Contains("kept going past 'revaluation proposed'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReleaseIsHeldWhileInhibitionIsAboveRest()
    {
        // The release is the first reachable step; an honest failure on another tool puts the brake on, and the
        // release is held, still in sight, until the channels settle.
        var tools = new List<Tool>
        {
            new("probe", [], ["probed"], "read"),
            new("deploy", [], ["deployed"], "release"),
        };
        var world = new Failing(new World(tools, []), "probe");
        var task = new AgentTask("deploy", "deployed", f => f.Contains("deployed"), w => w.Has("deployed") ? 1.0 : 0.0, w => w.Has("deployed") ? 1.0 : 0.0, [])
        {
            Valence = new ValenceTable(new HashSet<string> { "deployed" }, new Dictionary<string, string> { ["probed"] = "deployed" }, new HashSet<string>()),
        };
        var seen = new VisibleSpy();
        var (loop, regulator) = Orienting(new Scripted("probe"), task, AgentOptions.With(seen));
        var run = await loop.RunAsync(world, 12);

        Assert.True(run.GoalReached);
        var held = run.Trace.Where(e => e.Action == "hold release").ToList();
        Assert.NotEmpty(held);
        Assert.All(held, h => Assert.True(h.Stress > regulator.Affect.RestingI + 0.1));
        var deployed = run.Trace.Single(e => e.Level == "cell" && e.Action == "deploy");
        Assert.True(deployed.Tick > held[^1].Tick);
        // Held, not hidden: the release stayed in the visible set every tick.
        Assert.All(seen.ByTick.Values, v => Assert.Contains("deploy", v));
        Assert.True(regulator.Affect.I <= regulator.Affect.RestingI + 0.1 + 1e-9 || regulator.Affect.I < regulator.Affect.A);
    }

    [Fact]
    public void TheValenceTableIsWorldDataAndCanBeInferredFromTheCatalogue()
    {
        var inferred = ValenceTable.Infer(RestoreServiceDomain.Tools(), new HashSet<string>(RestoreServiceDomain.Initial));
        Assert.Equal(["alert.resolved"], inferred.Goals.Order());
        Assert.Equal("alert.resolved", inferred.StepToward["logs.inspected"]);
        Assert.Equal("alert.resolved", inferred.StepToward["deployed"]);
        Assert.Empty(inferred.Trouble);
        Assert.NotNull(RestoreServiceDomain.Task.Valence);

        // A fact some tool requires, none grants and the world did not start with: a trouble signal.
        var tools = new List<Tool> { new("fix", ["broken"], ["fixed"], "write"), new("check", [], ["checked"], "read") };
        var t = ValenceTable.Infer(tools, new HashSet<string>());
        Assert.Contains("broken", t.Trouble);
        Assert.Equal(["checked", "fixed"], t.Goals.Order());
    }

    [Fact]
    public async Task TheBehaviouristIsAPlainControlWithOneScalarPerAction()
    {
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("apply-patch");
        var run = await new BehaviouristAgent(RestoreServiceDomain.Task).RunAsync(world, 60);

        Assert.Equal("behaviourist", run.Agent);
        Assert.Contains(run.Trace, e => e.Level == "cell" && e.Action == "apply-patch" && e.Detail.StartsWith("-1: reported success, nothing gained", StringComparison.Ordinal));
        Assert.Contains(run.Trace, e => e.Level == "cell" && e.Detail.StartsWith("+1:", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Trace, e => e.Action == "orient");
        Assert.Contains(run.Notes, n => n.StartsWith("values: ", StringComparison.Ordinal));
        // Greedy and seedless: the same world twice gives the same run.
        var again = await new BehaviouristAgent(RestoreServiceDomain.Task).RunAsync(Liar(), 60);
        var twice = await new BehaviouristAgent(RestoreServiceDomain.Task).RunAsync(Liar(), 60);
        Assert.Equal(again.Trace, twice.Trace);

        static World Liar()
        {
            var w = RestoreServiceDomain.NewWorld();
            w.MakeLiar("apply-patch");
            return w;
        }
    }

    [Fact]
    public void TheHypothesesAreOnRecordWithTheirVerdictsOpen()
    {
        Assert.Equal(["S1", "S2", "S3", "S4", "S5", "S6", "S7"], SokolovHypotheses.All.Select(h => h.Id));
        Assert.All(SokolovHypotheses.All, h => Assert.False(string.IsNullOrWhiteSpace(h.Holds) || string.IsNullOrWhiteSpace(h.Fails)));
    }

    // ---- Worlds and spies -----------------------------------------------------------------------------------------

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

    /// <summary>A tool that always reports failure and grants nothing.</summary>
    private sealed class Failing(IWorld inner, string failing) : IWorld
    {
        public IReadOnlySet<string> Facts => inner.Facts;

        public IReadOnlyList<Tool> AvailableTools => inner.AvailableTools;

        public IReadOnlyList<Tool> AllTools => inner.AllTools;

        public int Ticks => inner.Ticks;

        public bool Has(string fact) => inner.Has(fact);

        public Tool? Tool(string id) => inner.Tool(id);

        public bool EffectsLanded(Tool tool) => inner.EffectsLanded(tool);

        public ActionOutcome Act(string toolId, string? payload = null) =>
            toolId == failing ? new ActionOutcome(toolId, false, [], "obs.failed") : inner.Act(toolId, payload);
    }

    /// <summary>A world with one tool that forgets a fact: the reset removes it, so the tool that grants it can deliver it again.</summary>
    private sealed class Resetting(World inner, string reset, string forgets) : IWorld
    {
        private readonly HashSet<string> _facts = new(inner.Facts, StringComparer.Ordinal);

        public IReadOnlySet<string> Facts => _facts;

        public IReadOnlyList<Tool> AvailableTools => inner.AvailableTools;

        public IReadOnlyList<Tool> AllTools => inner.AllTools;

        public int Ticks => inner.Ticks;

        public bool Has(string fact) => _facts.Contains(fact);

        public Tool? Tool(string id) => inner.Tool(id);

        public bool EffectsLanded(Tool tool) => tool.Grants.All(_facts.Contains);

        public ActionOutcome Act(string toolId, string? payload = null)
        {
            var tool = inner.Tool(toolId)!;
            if (!tool.Requires.All(_facts.Contains)) return new ActionOutcome(toolId, false, [], "preconditions unmet");
            if (toolId == reset) _facts.Remove(forgets);
            else _facts.UnionWith(tool.Grants);
            return new ActionOutcome(toolId, true, tool.Grants, null);
        }
    }

    private sealed class VisibleSpy : IAgentExtension
    {
        public string Name => "visible-spy";

        public Dictionary<int, List<string>> ByTick { get; } = [];

        public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible)
        {
            ByTick[context.Tick] = visible.Select(t => t.Id).ToList();
            return visible;
        }
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
