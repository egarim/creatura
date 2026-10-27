using Creatura.Worlds;

namespace Creatura.Bench;

/// <summary>What a trainer or an exam runs with: how a seat name becomes a seat, what to log, which oracle to read.</summary>
public sealed record BenchOptions
{
    /// <summary>The seat for a spec. The bench knows the planner; a runner that has models resolves the rest. Null means the planner for <c>bfs</c> and an error for anything else.</summary>
    public Func<SeatSpec, IPolicy>? Seats { get; init; }

    /// <summary>The oracle every run is classified by; null is the registry oracle over the case's task.</summary>
    public IOracle? Oracle { get; init; }

    /// <summary>The <c>battery</c> column of the rows.</summary>
    public string? Battery { get; init; }

    /// <summary>The seed the first run gets; a seeded agent (the random walker) gets one more per run.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>Where a line about each run goes, when anywhere.</summary>
    public Action<string>? Log { get; init; }

    public IPolicy SeatFor(SeatSpec spec)
    {
        if (Seats is not null) return Seats(spec);
        if (spec.IsPlanner) return new PlannerPolicy();
        throw new ArgumentException($"the bench has no seat '{spec.Name}'; give BenchOptions.Seats a resolver for it");
    }
}

/// <summary>The plumbing a trainer and an exam share: the world an arm or a case names, the options with the record bound to a store, the row a run leaves.</summary>
public static class BenchRuns
{
    /// <summary>The world an agent acts on and the truth an oracle reads: the manifest's world under the switches named (liar, opaque, revoked).</summary>
    public static (IWorld Target, IWorld Truth) WorldFor(WorldManifest manifest, BarrierSet barriers, bool opaque, bool everyToolLiesOnce, IReadOnlyList<string> revoked, EffectMemory memory)
    {
        var inner = manifest.Create(barriers);
        IWorld target = inner;
        if (opaque) target = new LearnedWorld(new OpaqueWorld(inner, revoked), memory);
        else if (revoked.Count > 0) target = new OpaqueWorld(inner, revoked) { Transparent = true };
        if (everyToolLiesOnce) target = new LiarWorld(target);
        return (target, inner);
    }

    /// <summary>
    /// The extensions named, with the record bound to the store and key: a <c>record</c> or <c>record:resample</c> name becomes a
    /// <see cref="RecordCarryExtension"/> over that store (the catalog's would carry an in-memory one of its own), and when the
    /// caller asks for one and no name says so, one is added last.
    /// </summary>
    public static AgentOptions OptionsFor(IReadOnlyList<string> names, EffectMemory memory, DreamJournal journal, IRecordStore? store, string? key, bool recordAlways)
    {
        var others = names.Where(n => !n.StartsWith("record", StringComparison.Ordinal)).ToList();
        var parsed = ExtensionCatalog.Parse(others, memory, journal);
        if (store is null || key is null) return parsed;
        var resample = names.Contains("record:resample", StringComparer.Ordinal) ? 3 : 0;
        if (!recordAlways && !names.Any(n => n.StartsWith("record", StringComparison.Ordinal))) return parsed;
        return new AgentOptions([.. parsed.Extensions, new RecordCarryExtension(store, key, resample)]);
    }

    /// <summary>The row one run leaves: the measures read off the run, the truth and the task.</summary>
    public static Row RowFor(AgentRun run, IWorld truth, AgentTask task, IOracle oracle, string battery, string test, string seat, string world, string agent, string extensions, string arm, int @case, int repeat, DateTimeOffset started, string note = "", TickUnit tickUnit = TickUnit.LoopTick)
    {
        var releases = run.Trace.Count(e => e.Level == "cell" && e.Kind == TraceKind.Acted && truth.Tool(e.Action)?.Kind == task.ReleaseKind);
        return new Row
        {
            At = started, Battery = battery, Test = test, Seat = seat, World = world, Agent = agent, Extensions = extensions, Arm = arm, Case = @case, Repeat = repeat,
            Outcome = oracle.Classify(run, truth), Ticks = run.Ticks, TickUnit = tickUnit,
            DeployBlocked = Measures.DeployBlocked(run), LiesCaught = Measures.LiesCaught(run), ShortcutTaken = Measures.ShortcutTaken(truth, task), Releases = releases, Note = note,
        };
    }

    /// <summary>The row a series leaves: real when it found a route, handed off otherwise (as the ingenuity runner writes its scoreboard rows).</summary>
    public static Row RowFor(SeriesResult result, string battery, string test, string seat, string world, string agent, string extensions, string arm, int repeat, DateTimeOffset started) => new()
    {
        At = started, Battery = battery, Test = test, Seat = seat, World = world, Agent = agent, Extensions = extensions, Arm = arm, Case = 1, Repeat = repeat,
        Outcome = result.RoutesFound > 0 ? OutcomeClass.Real : OutcomeClass.HandedOff, Ticks = result.TotalTicks, LiesCaught = result.LiesCaught,
        RoutesFound = result.RoutesFound, RoutesExist = result.RoutesExist, ExploratoryTries = result.ExploratoryTries,
        Note = $"routes {result.RoutesFound}/{result.RoutesExist}; {result.EndState}; {string.Join(" | ", result.Routes)}",
    };
}
