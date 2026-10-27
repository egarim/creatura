using Creatura.Worlds;

namespace Creatura.Bench;

/// <summary>What the trainer runs with, beyond the curriculum: the extensions beside the record, the seat, the layer.</summary>
public sealed record TrainerOptions
{
    /// <summary>The extension names loaded into every case, beside the record. A <c>record:resample</c> name turns re-sampling on.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    public SeatSpec Seat { get; init; } = SeatSpec.Planner;

    /// <summary>The control layer's switches for a catalogued loop (<see cref="Trainer.RunAsync(string, Curriculum, IRecordStore, string, TrainerOptions?, BenchOptions?, CancellationToken)"/>); null keeps the design's own.</summary>
    public ControlLayerOptions? Layer { get; init; }

    /// <summary>Called after every case, with the row written for it.</summary>
    public Action<TrainingCase>? OnCase { get; init; }
}

/// <summary>One case of a training: the run, what it came to, and the record as the store held it afterwards.</summary>
public sealed record TrainingCase(int Index, CurriculumCase Case, AgentRun Run, OutcomeClass Outcome, AgentRecord RecordAfter, Row Row);

/// <summary>A training: every case in order and the record it left in the store.</summary>
public sealed record TrainingReport(string Key, Curriculum Curriculum, IReadOnlyList<TrainingCase> Cases, AgentRecord Record)
{
    /// <summary>The first case (1-based) the oracle called real, or null when none was.</summary>
    public int? FirstRealCase => Cases.FirstOrDefault(c => c.Outcome == OutcomeClass.Real)?.Index;

    public IReadOnlyList<Row> Rows => Cases.Select(c => c.Row).ToList();

    /// <summary>One line per case: index, label, outcome, ticks, stage carried after.</summary>
    public IEnumerable<string> Lines() =>
        Cases.Select(c => $"case {c.Index,2} {c.Case.World}/{c.Case.Label,-8} {c.Outcome.AsRow(),-10} ticks {c.Run.Ticks,3}  stage carried {c.RecordAfter.StageLevel}  cases {c.RecordAfter.Cases}");
}

/// <summary>
/// Train: run a creature through a curriculum case after case with its <see cref="AgentRecord"/> carried through the
/// store and saved after each. This is battery 2's learning test and the <c>record</c> / <c>record:resample</c>
/// experiments as a first-class loop. The Core carry (skills, predictions, stage) travels in a
/// <see cref="RecordCarryExtension"/> over the store; a catalogued loop built with the store bound
/// (<see cref="AgentCatalog.Factory"/>) also loads and saves its regulator's private parts through the same key.
/// </summary>
public static class Trainer
{
    /// <summary>Trains the agent the factory builds. Bind the store into the factory (<see cref="AgentCatalog.Factory"/>) for a loop to carry its regulator's parts as well; a hosted agent carries the Core record through the extension alone.</summary>
    public static async Task<TrainingReport> RunAsync(AgentFactory make, Curriculum curriculum, IRecordStore store, string key, TrainerOptions? options = null, BenchOptions? bench = null, CancellationToken cancellationToken = default)
    {
        options ??= new TrainerOptions();
        bench ??= new BenchOptions();
        var memory = new EffectMemory();
        var journal = new DreamJournal();
        // One record extension for the whole training, as the learning test ran it: it re-reads the store before every case.
        var agentOptions = BenchRuns.OptionsFor(options.Extensions, memory, journal, store, key, recordAlways: true);
        var cases = new List<TrainingCase>();
        var index = 0;
        foreach (var @case in curriculum.Cases)
        {
            index++;
            var manifest = WorldCatalog.Find(@case.World);
            var task = @case.TaskFor(manifest);
            var (target, truth) = BenchRuns.WorldFor(manifest, @case.BarriersFor(manifest), @case.Opaque, @case.EveryToolLiesOnce, @case.Revoked, memory);
            var agent = make(bench.SeatFor(options.Seat), task, agentOptions, bench.Seed + index - 1);
            var started = DateTimeOffset.UtcNow;
            var run = await agent.RunAsync(target, @case.MaxTicks, cancellationToken).ConfigureAwait(false);
            var oracle = bench.Oracle ?? new RegistryOracle(task);
            var after = store.Load(key) ?? run.Record ?? new AgentRecord { Key = key };
            var row = BenchRuns.RowFor(run, truth, task, oracle, bench.Battery ?? "training", curriculum.Id, options.Seat.Name, @case.World, agent.Name,
                agentOptions.Extensions.Count == 0 ? "none" : string.Join("+", agentOptions.Extensions.Select(e => e.Name)), @case.Label, index, 1, started,
                $"stage carried {after.StageLevel}");
            var done = new TrainingCase(index, @case, run, row.Outcome, after, row);
            cases.Add(done);
            options.OnCase?.Invoke(done);
            bench.Log?.Invoke($"{curriculum.Id} case {index} {agent.Name} {row.Outcome.AsRow()} ticks {run.Ticks} stage {after.StageLevel}");
        }

        return new TrainingReport(key, curriculum, cases, store.Load(key) ?? new AgentRecord { Key = key });
    }

    /// <summary>Trains a catalogued agent by name, with the store bound into its loop so regulator parts are carried too.</summary>
    public static Task<TrainingReport> RunAsync(string agentKind, Curriculum curriculum, IRecordStore store, string key, TrainerOptions? options = null, BenchOptions? bench = null, CancellationToken cancellationToken = default) =>
        RunAsync(AgentCatalog.Factory(agentKind, options?.Layer, AgentCatalog.Find(agentKind).Build is null ? null : store, key), curriculum, store, key, options, bench, cancellationToken);
}
