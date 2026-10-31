using System.Globalization;
using System.Text;

namespace Creatura.Worlds.Web;

/// <summary>A web task as the loop reads it: the goal is the done fact the app's data grants; nothing else counts.</summary>
public static class WebTask
{
    public static AgentTask For(UxTask task)
    {
        var done = task.DoneFact;
        return new AgentTask(task.Id, task.Prompt, f => f.Contains(done), w => w.Has(done) ? 1.0 : 0.0, w => w.Has(done) ? 1.0 : 0.0, []);
    }
}

/// <summary>The loop with the control layer and no design in the slots: what a run gets with no <c>--design</c>.</summary>
public static class PlainLoop
{
    private sealed class Plain : IRegulator
    {
        public Identity Identity { get; } = new("tester", "the app's owner");
    }

    public static IAgent Make(IPolicy policy, AgentTask task) => Make(policy, task, null, null);

    /// <summary>The plain loop that loads its record before the run and saves it after, under the key.</summary>
    public static IAgent Make(IPolicy policy, AgentTask task, IRecordStore? store, string? key) =>
        new ReferenceLoop(new Plain(), policy, task, name: "plain loop", store: store, recordKey: key);
}

/// <summary>
/// One cell of the matrix: a task, a persona, an environment, a seat (the tester). What the data says is the result;
/// what the page said and what the model believed are beside it, so the gap can be read.
/// </summary>
public sealed record UxCell
{
    public required string Task { get; init; }

    public required string Persona { get; init; }

    public required string Environment { get; init; }

    /// <summary>The seat's own name (a script's file, a model's name).</summary>
    public required string Seat { get; init; }

    /// <summary>The tester as the matrix named it (<c>script:…</c>, <c>model:…</c>): part of the cell's key. The seat's name when run alone.</summary>
    public string Tester { get; init; } = "";

    public required string Agent { get; init; }

    /// <summary>The design in the loop, by its matrix name (<c>plain</c>, <c>bach</c>, …): part of the cell's key.</summary>
    public string Design { get; init; } = "plain";

    /// <summary>The persona's record key when it remembers, else null.</summary>
    public string? Memory { get; init; }

    /// <summary>How many cells the persona's record had been through before this one (0: it started fresh).</summary>
    public int CasesBefore { get; init; }

    /// <summary>The lessons the persona was shown from earlier cells.</summary>
    public IReadOnlyList<string> Remembered { get; init; } = [];

    /// <summary>The cell's identity across runs, for compare: task, persona, environment, tester and design.</summary>
    public string Key => $"{Task}|{Persona}|{Environment}|{(Tester.Length == 0 ? Seat : Tester)}|{Design}";

    /// <summary>Why the cell could not run (the app did not open, the browser failed); null when it ran.</summary>
    public string? Error { get; init; }

    public IReadOnlyList<string> Barriers { get; init; } = [];

    /// <summary>The app's own data says the task is done: the only verdict.</summary>
    public required bool DataDone { get; init; }

    public required string CheckDetail { get; init; }

    /// <summary>The page took a commit and said so.</summary>
    public required bool PageSaidDone { get; init; }

    /// <summary>The tick the persona said it was done, when it did (a model seat only).</summary>
    public int? BelievedDoneAt { get; init; }

    public required int Ticks { get; init; }

    /// <summary>The lies the world told in this cell, by its own truth (a commit the page took that the data does not show).</summary>
    public int LiesTold { get; init; }

    /// <summary>Of <see cref="LiesTold"/>, the ones the run blocked on the tick they were told, in any design's words.</summary>
    public required int LiesCaught { get; init; }

    public required int Escalations { get; init; }

    /// <summary>What the seat's calls cost: 0 for a script, null for a model whose price the catalog does not know.</summary>
    public decimal? Cost { get; init; }

    public IReadOnlyDictionary<string, int> BarrierFires { get; init; } = new Dictionary<string, int>();

    [System.Text.Json.Serialization.JsonIgnore]
    public AgentRun? Run { get; init; }

    /// <summary>The gap a judge reads: someone (the page or the model) said done and the data does not.</summary>
    public bool FalseDone => !DataDone && (PageSaidDone || BelievedDoneAt is not null);

    /// <summary>The model believed it was done and the data says not: the persona's gap.</summary>
    public bool BelievedNotDone => !DataDone && BelievedDoneAt is not null;

    /// <summary>The page said a commit went through and the data says not: the app's gap (or a barrier's).</summary>
    public bool PageSaidNotDone => !DataDone && PageSaidDone;

    /// <summary>The world told a lie the run did not block: the design did not notice.</summary>
    public bool UncaughtLie => LiesTold > LiesCaught;

    /// <summary>A cell that could not run, recorded so the matrix keeps its shape.</summary>
    public static UxCell Failed(string task, string persona, string environment, string tester, string error, string design = "plain") => new()
    {
        Task = task, Persona = persona, Environment = environment, Seat = tester, Tester = tester, Agent = "", Design = design,
        DataDone = false, CheckDetail = "not run", PageSaidDone = false, Ticks = 0, LiesCaught = 0, Escalations = 0, Error = error,
    };

    public string Summary()
    {
        var sb = new StringBuilder();
        var design = Design == "plain" ? "" : $" × {Design}";
        if (Error is not null) return $"{Task} × {Persona} × {Environment} × {(Tester.Length == 0 ? Seat : Tester)}{design}: NOT RUN ({Error})";
        sb.Append($"{Task} × {Persona} × {Environment} × {Seat}{design}: the data says {(DataDone ? "DONE" : "NOT DONE")} ({CheckDetail})");
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"; {Ticks} tick{(Ticks == 1 ? "" : "s")}, {LiesCaught} lie{(LiesCaught == 1 ? "" : "s")} caught"));
        if (PageSaidDone && !DataDone) sb.Append("; the page said it was saved");
        if (BelievedDoneAt is { } t) sb.Append(string.Create(CultureInfo.InvariantCulture, $"; the persona believed it was done at tick {t}"));
        if (Escalations > 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $"; {Escalations} escalation{(Escalations == 1 ? "" : "s")}"));
        if (Cost is { } cost && cost > 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $"; cost {cost:0.####}"));
        if (Memory is not null) sb.Append(string.Create(CultureInfo.InvariantCulture, $"; remembered {CasesBefore} earlier cell{(CasesBefore == 1 ? "" : "s")}"));
        if (BarrierFires.Count > 0) sb.Append("; barriers fired: " + string.Join(", ", BarrierFires.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} ×{kv.Value}"))));
        return sb.ToString();
    }
}

/// <summary>Runs one cell: opens the world, seats the persona, runs the agent, reads the data.</summary>
public static class UxRunner
{
    /// <param name="seat">Builds the seat for the opened world (it needs the world's screen).</param>
    /// <param name="agent">Builds the agent around the seat and the task; the plain loop when null.</param>
    public static async Task<UxCell> RunAsync(WebBrowser browser, UxAdapter adapter, UxTask task, UxPersona persona, UxEnvironment environment,
        Func<WebWorld, IPolicy> seat, Func<IPolicy, AgentTask, IAgent>? agent = null, WebWorldOptions? options = null, int? maxTicks = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new WebWorldOptions();
        await using var world = await WebWorld.OpenAsync(browser, adapter, task, environment, options, cancellationToken).ConfigureAwait(false);
        var policy = seat(world);
        var loop = (agent ?? PlainLoop.Make)(policy, WebTask.For(task));
        var run = await loop.RunAsync(world, maxTicks ?? adapter.Limits.Steps, cancellationToken).ConfigureAwait(false);
        await world.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var persona_ = policy as PersonaPolicy;
        return new UxCell
        {
            Task = task.Id, Persona = persona.Id, Environment = environment.Id, Seat = policy.Name, Agent = run.Agent,
            Barriers = options.Barriers, DataDone = world.LastCheck.Done, CheckDetail = world.LastCheck.Detail,
            PageSaidDone = world.PageReportedCommit, BelievedDoneAt = persona_?.Telemetry.BelievedDoneAt,
            Ticks = run.Ticks, LiesTold = world.Lies.Count, LiesCaught = world.Lies.Count(l => l.CaughtIn(run)), Escalations = persona_?.Telemetry.Escalations ?? 0,
            Cost = persona_ is null ? 0m : persona_.Telemetry.Cost, BarrierFires = new Dictionary<string, int>(world.BarrierFires), Run = run,
        };
    }
}
