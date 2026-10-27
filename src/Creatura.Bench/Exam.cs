using Creatura.Worlds;

namespace Creatura.Bench;

/// <summary>
/// A store an exam reads and never writes: the record is loaded from the inner store once, on the first read, and every
/// later read hands out a copy of that; a save is refused (counted, not performed), so nothing the examinee does during
/// the exam reaches the record it was examined on. This is the wrapper migration step 10 left open.
/// </summary>
public sealed class ReadOnlyRecordStore : IRecordStore
{
    private readonly IRecordStore _inner;
    private readonly object _gate = new();
    private string? _frozen;
    private bool _loaded;

    public ReadOnlyRecordStore(IRecordStore inner) => _inner = inner;

    /// <summary>How many saves were refused.</summary>
    public int SavesRefused { get; private set; }

    /// <summary>The record as frozen on the first load, or null when the inner store had none (or nothing has been loaded yet).</summary>
    public AgentRecord? Frozen
    {
        get { lock (_gate) return _frozen is null ? null : AgentRecord.Parse(_frozen); }
    }

    public AgentRecord? Load(string key)
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                _frozen = _inner.Load(key)?.ToJson();
                _loaded = true;
            }

            return _frozen is null ? null : AgentRecord.Parse(_frozen);
        }
    }

    /// <summary>Refused: an exam freezes the record. The call is counted and nothing is written.</summary>
    public void Save(string key, AgentRecord record)
    {
        lock (_gate) SavesRefused++;
    }
}

/// <summary>One run of an exam: the arm, the repeat, the case within it, the run, the class, the row.</summary>
public sealed record ExamRun(Arm Arm, int Repeat, int Case, AgentRun Run, OutcomeClass Outcome, Row Row);

/// <summary>An exam scored: every run's row, the battery's verdicts, and proof the store was not written.</summary>
public sealed record ExamReport(Battery Battery, string Key, AgentRecord? Record, IReadOnlyList<ExamRun> Runs, BatteryReport Report, int SavesRefused)
{
    public IReadOnlyList<Row> Rows => Runs.Select(r => r.Row).ToList();

    public HypothesisVerdict this[string hypothesisId] => Report[hypothesisId];

    /// <summary>One line per hypothesis, then one per run.</summary>
    public IEnumerable<string> Lines()
    {
        foreach (var h in Battery.Hypotheses) yield return $"{h.Id,-4} {BatteryReport.Word(Report[h.Id]),-8} {h.Statement}";
        foreach (var r in Runs) yield return $"{r.Arm.TestName,-22} {r.Arm.Agent,-12} {r.Arm.Name,-16} c{r.Case,-2} r{r.Repeat} {r.Outcome.AsRow(),-10} ticks {r.Run.Ticks,3}  {r.Row.Note}";
    }
}

/// <summary>What an exam runs with beyond the battery: which arms, and a seat resolver through <see cref="BenchOptions"/>.</summary>
public sealed record ExamOptions
{
    /// <summary>Only the arms this admits; all when null.</summary>
    public Func<Arm, bool>? Only { get; init; }

    public Action<ExamRun>? OnRun { get; init; }
}

/// <summary>
/// Test: run a creature against a battery's arms on a record loaded once and never saved, and score the battery's
/// hypotheses over the rows. The canonical exam is the panic test on a stage-4 record: what a creature that earned its
/// releases does when every tool lies once. The examinee starts every run from the same frozen record; whatever it
/// learns during the exam is dropped with the run.
/// </summary>
public static class Exam
{
    /// <summary>
    /// Examines the agent the factory builds for each arm; the factory receives the frozen store so a loop can bind it
    /// (<see cref="AgentCatalog.Factory"/>). The frozen record reaches every agent through the record extension as well.
    /// </summary>
    public static async Task<ExamReport> RunAsync(Func<ReadOnlyRecordStore, Arm, AgentFactory> make, Battery battery, IRecordStore store, string key, ExamOptions? options = null, BenchOptions? bench = null, CancellationToken cancellationToken = default)
    {
        options ??= new ExamOptions();
        bench ??= new BenchOptions();
        var frozen = new ReadOnlyRecordStore(store);
        var record = frozen.Load(key);
        var runs = new List<ExamRun>();
        var routesExist = new Dictionary<string, int>(StringComparer.Ordinal);
        var seed = bench.Seed;
        foreach (var arm in battery.Arms.Where(a => options.Only?.Invoke(a) ?? true))
        {
            var manifest = WorldCatalog.Find(arm.World);
            var task = arm.TaskFor(manifest);
            var barriers = arm.BarriersFor(manifest);
            var oracle = bench.Oracle ?? battery.OracleFor(manifest);
            var batteryId = bench.Battery ?? battery.Id;
            var factory = make(frozen, arm);
            for (var repeat = 1; repeat <= battery.Repeats; repeat++)
            {
                if (arm.Protocol == Protocol.Series)
                {
                    var routesKey = $"{arm.World}/{barriers}";
                    if (!routesExist.TryGetValue(routesKey, out var exist)) routesExist[routesKey] = exist = await Series.CountRoutesAsync(manifest, barriers, arm.MaxTicks).ConfigureAwait(false);
                    var started = DateTimeOffset.UtcNow;
                    var seedAtStart = seed;
                    var result = await Series.RunAsync(manifest, barriers, arm.Agent,
                        (runOptions, runsSoFar) => factory(bench.SeatFor(arm.Seat), task, runOptions, seedAtStart + runsSoFar),
                        (memory, journal) => BenchRuns.OptionsFor(arm.Extensions, memory, journal, frozen, key, recordAlways: true),
                        arm.Opaque, arm.MaxTicks, exist, null, cancellationToken).ConfigureAwait(false);
                    seed += result.Runs;
                    var row = BenchRuns.RowFor(result, batteryId, arm.TestName, arm.Seat.Name, arm.World, arm.Agent, arm.ExtensionsLabel, arm.Name, repeat, started);
                    var last = new AgentRun(arm.Agent, result.RoutesFound > 0, result.TotalTicks, [], []);
                    Add(new ExamRun(arm, repeat, 1, last, row.Outcome, row));
                    continue;
                }

                var memory = new EffectMemory();
                var journal = new DreamJournal();
                var agentOptions = BenchRuns.OptionsFor(arm.Extensions, memory, journal, frozen, key, recordAlways: true);
                var cases = arm.Protocol == Protocol.CaseSequence ? Math.Max(1, arm.Cases) : 1;
                for (var @case = 1; @case <= cases; @case++)
                {
                    var (target, truth) = BenchRuns.WorldFor(manifest, barriers, arm.Opaque, arm.EveryToolLiesOnce, arm.Revoked, memory);
                    var agent = factory(bench.SeatFor(arm.Seat), task, agentOptions, seed++);
                    var started = DateTimeOffset.UtcNow;
                    var run = await agent.RunAsync(target, arm.MaxTicks, cancellationToken).ConfigureAwait(false);
                    var note = arm.EveryToolLiesOnce && target is LiarWorld liar ? $"lies {liar.Lies}" : "";
                    var row = BenchRuns.RowFor(run, truth, task, oracle, batteryId, arm.TestName, arm.Seat.Name, arm.World, arm.Agent,
                        agentOptions.Extensions.Count == 0 ? "none" : string.Join("+", agentOptions.Extensions.Select(e => e.Name)), arm.Name, @case, repeat, started, note);
                    Add(new ExamRun(arm, repeat, @case, run, row.Outcome, row));
                }
            }
        }

        var rows = runs.Select(r => r.Row).ToList();
        return new ExamReport(battery, key, record, runs, BatteryReport.Score(battery, rows), frozen.SavesRefused);

        void Add(ExamRun run)
        {
            runs.Add(run);
            options.OnRun?.Invoke(run);
            bench.Log?.Invoke($"{run.Arm.TestName} {run.Arm.Agent} {run.Arm.Name} c{run.Case} r{run.Repeat} {run.Outcome.AsRow()} ticks {run.Run.Ticks}");
        }
    }

    /// <summary>Examines the one agent the factory builds, whatever the arm names; the factory receives the frozen store so a loop can bind it.</summary>
    public static Task<ExamReport> RunAsync(Func<ReadOnlyRecordStore, AgentFactory> make, Battery battery, IRecordStore store, string key, ExamOptions? options = null, BenchOptions? bench = null, CancellationToken cancellationToken = default) =>
        RunAsync((frozen, _) => make(frozen), battery, store, key, options, bench, cancellationToken);

    /// <summary>Examines the agent a plain factory builds (a hosted agent, say): the frozen record reaches it through the record extension alone.</summary>
    public static Task<ExamReport> RunAsync(AgentFactory make, Battery battery, IRecordStore store, string key, ExamOptions? options = null, BenchOptions? bench = null, CancellationToken cancellationToken = default) =>
        RunAsync((_, _) => make, battery, store, key, options, bench, cancellationToken);

    /// <summary>Examines each arm's catalogued agent (<see cref="Arm.Agent"/>, with <see cref="Arm.Layer"/>), the frozen store bound into every loop and nothing extra for a control.</summary>
    public static Task<ExamReport> RunAsync(Battery battery, IRecordStore store, string key, ExamOptions? options = null, BenchOptions? bench = null, CancellationToken cancellationToken = default) =>
        RunAsync((frozen, arm) => AgentCatalog.Factory(arm.Agent, arm.Layer, AgentCatalog.Find(arm.Agent).Build is null ? null : frozen, key), battery, store, key, options, bench, cancellationToken);
}
