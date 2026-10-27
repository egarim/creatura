using Creatura;

namespace Creatura.Tests;

/// <summary>
/// The hybrid regulator behind the research repository's facade (migration step 6). The hybrid goldens pin every
/// row; these pin what the goldens show only by their absence or their timing: the stage ceiling, the alarm as a
/// vetoable stop that sees this tick's freedom, the two self-models with their two jobs, the lazy copy of a carried
/// record into the cell after the extension that seeds it, and a sleep that proposes and never applies.
/// </summary>
public class HybridRegulatorTests
{
    /// <summary>The hybrid as the research facade builds it.</summary>
    private static (ReferenceLoop Loop, HybridRegulator Regulator) Hybrid(IPolicy? policy = null, AgentTask? task = null, AgentOptions? options = null)
    {
        var regulator = new HybridRegulator(Identity.Hybrid, Identity.Cell, stage: 3);
        return (new ReferenceLoop(regulator, policy, task, options, ControlLayerOptions.Design, "Hybrid colony"), regulator);
    }

    [Fact]
    public async Task TheHybridNeverExceedsItsStageCeiling()
    {
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("apply-patch");

        var run = await Hybrid().Loop.RunAsync(world, 30);

        // Stage 3 caps freedom at 0.65; stage 4 at 0.80. Nothing may exceed the ceiling it earned.
        Assert.All(run.Trace, e => Assert.True(e.Freedom <= 0.80 + 1e-9, $"freedom {e.Freedom:F2} exceeded the stage cap at tick {e.Tick}"));
        Assert.Contains(run.Trace, e => e.Freedom <= 0.65 + 1e-9);
        Assert.True(run.GoalReached);
    }

    [Fact]
    public async Task TheAlarmFiresAfterFiveStaleTicksWithThisTicksFreedomAndIsVetoable()
    {
        // No tool at all: every tick is "no action". The no-path hand-off is vetoed so the alarm, not the budget, is
        // what stops the run: patience is the cooldown (3) plus two, progress never moves after tick 1, so tick 6 fires.
        var world = new World([], []);
        var (loop, regulator) = Hybrid(options: AgentOptions.With(new Veto(HandOffReason.NoPath)));
        var run = await loop.RunAsync(world, 30);

        Assert.False(run.GoalReached);
        Assert.Equal(6, run.Ticks);
        var alarm = Assert.Single(run.Trace, e => e.Action == TraceText.StuckRegulation);
        Assert.Equal(("organism", "contract", TraceKind.Blocked), (alarm.Level, alarm.SetPoint, alarm.Kind));
        Assert.Equal($"5 ticks with no progress at {alarm.Freedom:P0} freedom", alarm.Detail);
        Assert.Equal(0.65, alarm.Freedom, 9); // stress 1 buys 1.0; the stage caps it at 0.65, and the alarm row carries that
        Assert.Contains("stuck-regulation alarm: acting without moving the gap, handed to a human", run.Notes);
        Assert.Equal(5, regulator.Alarm.StaleTicks);

        // Vetoed, the alarm counts again from zero and the run goes on to the end.
        var (vetoed, _) = Hybrid(options: AgentOptions.With(new Veto(HandOffReason.NoPath, HandOffReason.StuckRegulation)));
        var kept = await vetoed.RunAsync(new World([], []), 12);
        Assert.Equal(12, kept.Ticks);
        Assert.DoesNotContain(kept.Trace, e => e.Action == TraceText.StuckRegulation);
        Assert.Equal(2, kept.Notes.Count(n => n.Contains("kept going past 'stuck regulation'", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheCellIsTheLedgerAndTheOrganismIsWhatTheExtensionsRead()
    {
        var seen = new SelfModelSpy();
        var (loop, regulator) = Hybrid(options: AgentOptions.With(seen));
        var run = await loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);

        Assert.True(run.GoalReached);
        // The extensions were handed the organism, and its skills stayed empty while the run recorded into the cell.
        Assert.Same(regulator.Self, seen.Model);
        Assert.Empty(seen.SkillsSeenDuringRun);
        Assert.NotEmpty(regulator.Cell.Skills);
        // After the run the cell's reliabilities are copied up, so a record extension's sleep carries them.
        Assert.Equal(regulator.Cell.Skills.Keys.Order(StringComparer.Ordinal), regulator.Self!.Skills.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(run.Notes, n => n.StartsWith("coherence 100%; cell reliabilities: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACarriedRecordReachesTheCellAfterTheExtensionSeedsIt()
    {
        // Case 1 fills the record; in case 2 the record extension seeds the organism at its first VisibleTools call,
        // and the seat's first render already shows the cell record copied from it (refuted claim 6: the copy is
        // lazy and lands after the seed, never before).
        var record = new RecordCarryExtension();
        var policy = new RenderSpy();
        var (loop, _) = Hybrid(policy, options: AgentOptions.With(record));
        await loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.DoesNotContain("cell record", policy.FirstRender);

        policy.FirstRender = null;
        await loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.Contains("cell record, how often each tool's effect landed when it said so:", policy.FirstRender);
        Assert.Contains("apply-patch", policy.FirstRender);
    }

    [Fact]
    public async Task TheSleepProposesAndNeverApplies()
    {
        var reached = Hybrid();
        var task = RestoreServiceDomain.Task;
        await reached.Loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.Equal(["propose: keep the contract set point as it is"], reached.Regulator.Proposals);

        var impossible = Hybrid(task: task with { Goal = f => f.Contains("unicorn"), Habit = [] });
        var run = await impossible.Loop.RunAsync(RestoreServiceDomain.NewWorld(), 8);
        Assert.False(run.GoalReached);
        Assert.Equal("propose: renegotiate the contract set point — unreachable with current tools", run.Notes[^1]);
        // Proposed, not applied: the proposal is on record and nothing in the run touched a set point.
        Assert.Equal([run.Notes[^1]], impossible.Regulator.Proposals);
        Assert.DoesNotContain(run.Notes, n => n.Contains("set point changed", StringComparison.Ordinal));
    }

    private sealed class Veto(params HandOffReason[] reasons) : IAgentExtension
    {
        public string Name => "veto";

        public bool SuppressHandOff(TickContext context, HandOff handOff) => reasons.Contains(handOff.Reason);
    }

    private sealed class SelfModelSpy : IAgentExtension
    {
        public string Name => "spy";

        public SelfModel? Model { get; private set; }

        public List<string> SkillsSeenDuringRun { get; } = [];

        public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible)
        {
            Model = context.SelfModel;
            SkillsSeenDuringRun.AddRange(context.SelfModel!.Skills.Keys);
            return visible;
        }
    }

    private sealed class RenderSpy : IPolicy
    {
        private readonly PlannerPolicy _planner = new();

        public string Name => "render-spy";

        public string? FirstRender { get; set; }

        public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
        {
            FirstRender ??= context.SelfModel;
            return _planner.NextAsync(context, cancellationToken);
        }
    }
}
