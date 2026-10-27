using Creatura;

namespace Creatura.Tests;

/// <summary>
/// The join laws (migration step 6): one test per slot of <see cref="JoinRules"/>, one for the composition rule,
/// and one for the frame. Proven on small regulators that owe no golden, never on the shipped hybrid, which is
/// written by hand (design-framework.md, refuted claim 5). No production code builds a join.
/// </summary>
public class JoinTests
{
    /// <summary>A regulator with a fixed error and a freedom of its own: enough to see each fold.</summary>
    private sealed class Side(string name, double error, double freedom) : IRegulator
    {
        public Identity Identity => new(name, "tests");

        public List<string> Calls { get; } = [];

        public double Error(RegulatorContext context) => error;

        public double Freedom(RegulatorContext context, double e) => freedom;

        public void Sleep(SleepContext context) => Calls.Add("sleep");

        public void AfterRun(RegulatorContext context) => Calls.Add("after");
    }

    /// <summary>A regulator that renders its own refusal row: a framer.</summary>
    private sealed class Framer : IRegulator
    {
        public Identity Identity => new("framer", "tests");

        public void Refused(ControlLayer layer, string toolId, RefusalReason why) =>
            layer.Record("framer", "frame", 1.0, toolId, TraceKind.Blocked, "framed refusal");
    }

    private static RegulatorContext Context(IWorld world) => new()
    {
        Agent = "join", World = world, Task = RestoreServiceDomain.Task, PolicyName = "bfs", Options = AgentOptions.None, MaxTicks = 10,
        Trace = [], Notes = [],
    };

    [Fact]
    public void ErrorFoldsByTheRuleAndTheWorseOfTheTwoByDefault()
    {
        var ctx = Context(RestoreServiceDomain.NewWorld());
        Assert.Equal(0.7, Regulators.Join(new Side("a", 0.2, 1), new Side("b", 0.7, 1)).Error(ctx));
        Assert.Equal(0.2, Regulators.Join(new Side("a", 0.2, 1), new Side("b", 0.7, 1), new JoinRules { Error = Combine.Min }).Error(ctx));
        Assert.Equal(0.2, Regulators.Join(new Side("a", 0.2, 1), new Side("b", 0.7, 1), new JoinRules { Error = Combine.First }).Error(ctx));
        Assert.Equal(0.7, Regulators.Join(new Side("a", 0.2, 1), new Side("b", 0.7, 1), new JoinRules { Error = Combine.Second }).Error(ctx));
    }

    [Fact]
    public async Task TheFreedomCapWinsBeforeTheExtensionChain()
    {
        var ctx = Context(RestoreServiceDomain.NewWorld());
        var join = Regulators.Join(new Side("wide", 0, 0.9), new Side("capped", 0, 0.35));
        Assert.Equal(0.35, join.Freedom(ctx, 0.5));
        Assert.Equal(0.9, Regulators.Join(new Side("wide", 0, 0.9), new Side("capped", 0, 0.35), new JoinRules { FreedomCap = Combine.Max }).Freedom(ctx, 0.5));

        // Before the chain: an extension that widens freedom sees the capped value, not either side's own.
        var seen = new FreedomSpy();
        var run = await new ReferenceLoop(join, options: AgentOptions.With(seen), layer: ControlLayerOptions.Design).RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.True(run.GoalReached);
        Assert.All(seen.Seen, f => Assert.Equal(0.35, f, 9));
        // And the rows carry what the chain made of it.
        Assert.Contains(run.Trace, e => e.Level == "tissue" && Math.Abs(e.Freedom - 1.0) < 1e-9);
    }

    [Fact]
    public async Task TheJoinsAlarmStopsTheCreatureWithAVetoableStuckRegulationHandOff()
    {
        var rules = new JoinRules { Alarm = new StuckRegulationAlarm(patience: 3) };
        var join = Regulators.Join(new Side("a", 1, 1), new Side("b", 1, 1), rules);
        // No tool: no progress after tick 1; patience 3 fires at tick 4, before the no-path budget (4 attempts) is spent at tick 5.
        var run = await new ReferenceLoop(join, layer: ControlLayerOptions.Design).RunAsync(new World([], []), 30);
        Assert.Equal(4, run.Ticks);
        Assert.False(run.GoalReached);
        var handOff = Assert.Single(run.Trace, e => e.Kind == TraceKind.Escalated);
        Assert.Equal((TraceText.HandOff, "stuck regulation"), (handOff.Action, handOff.Detail));

        var vetoed = await new ReferenceLoop(Regulators.Join(new Side("a", 1, 1), new Side("b", 1, 1), new JoinRules { Alarm = new StuckRegulationAlarm(3) }),
            options: AgentOptions.With(new VetoAll()), layer: ControlLayerOptions.Design).RunAsync(new World([], []), 10);
        Assert.Equal(10, vetoed.Ticks);
        Assert.DoesNotContain(vetoed.Trace, e => e.Kind == TraceKind.Escalated);
        Assert.Contains(vetoed.Notes, n => n.Contains("kept going past 'stuck regulation'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SleepRunsForBothSidesOrTheOneTheRuleNames()
    {
        foreach (var (rule, a, b) in new[] { (SleepRule.Both, 1, 1), (SleepRule.First, 1, 0), (SleepRule.Second, 0, 1) })
        {
            var first = new Side("a", 0, 1);
            var second = new Side("b", 0, 1);
            await new ReferenceLoop(Regulators.Join(first, second, new JoinRules { Sleep = rule }), layer: ControlLayerOptions.Design).RunAsync(RestoreServiceDomain.NewWorld(), 30);
            Assert.Equal(a, first.Calls.Count(c => c == "sleep"));
            Assert.Equal(b, second.Calls.Count(c => c == "sleep"));
            // After-run runs for both whatever the sleep rule.
            Assert.Contains("after", first.Calls);
            Assert.Contains("after", second.Calls);
        }
    }

    [Fact]
    public void TwoFramersCannotShareOneCreature()
    {
        var ex = Assert.Throws<CompositionException>(() => Regulators.Join(new Framer(), new SelfModelRegulator(Identity.Bach)));
        Assert.Contains("both render the frame", ex.Message);
        // The shipped regulators are all framers, which is the composition rule's way of saying the hybrid is not a join.
        Assert.Throws<CompositionException>(() => Regulators.Join(new ColonyRegulator(Identity.Levin), new SelfModelRegulator(Identity.Bach)));
        Assert.True(Regulators.IsFramer(new HybridRegulator()));
        Assert.False(Regulators.IsFramer(new Side("plain", 0, 1)));
    }

    [Fact]
    public async Task TheOneFramerRendersTheLayersDecisionsForTheJoin()
    {
        // A seat that names a tool that is not there: the join's refusal row is the framer's, not the plain one.
        var join = Regulators.Join(new Side("plain", 1, 1), new Framer());
        var run = await new ReferenceLoop(join, new Scripted("no-such-tool"), layer: ControlLayerOptions.Design).RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.Contains(run.Trace, e => e.Level == "framer" && e.Detail == "framed refusal");
        Assert.DoesNotContain(run.Trace, e => e.Detail == TraceText.NotVisible);
        Assert.True(run.GoalReached);
    }

    private sealed class FreedomSpy : IAgentExtension
    {
        public string Name => "freedom-spy";

        public List<double> Seen { get; } = [];

        public double Freedom(TickContext context, double freedom)
        {
            Seen.Add(freedom);
            return 1.0;
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
