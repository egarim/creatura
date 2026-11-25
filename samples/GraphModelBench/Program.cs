using System.Diagnostics;
using System.Globalization;
using System.Text;
using Creatura;
using Creatura.Bench;
using Creatura.Graph;
using Creatura.Graph.Bench;
using Creatura.Seats;
using Creatura.Worlds;

// The model-in-the-seat arm of the cognitive graph experiment (issue #110, milestone 6). Everything else in the
// benchmark is deterministic and offline; this puts a language model in the chair and asks whether structured
// retrieval changes what an agent DOES, judged by world state and not by the agent's own account of itself.
//
//   dotnet run --project samples/GraphModelBench -- --smoke  --model deepseek-chat
//   dotnet run --project samples/GraphModelBench -- --pilot  --model deepseek-chat --repeats 5
//
// The pilot is deliberately one scenario and two arms. Before spending hours on a matrix it asks whether an effect
// is detectable at all above the noise of a stochastic seat.

var catalogPath = Path.Combine(AppContext.BaseDirectory, "models.local.json");
ModelCatalog.Use(ModelCatalog.Load(catalogPath));
var model = Arg("--model", "deepseek-chat");
var design = Arg("--design", "bach");
var ticks = int.Parse(Arg("--ticks", "20"), CultureInfo.InvariantCulture);
var repeats = int.Parse(Arg("--repeats", "5"), CultureInfo.InvariantCulture);

IPolicy policy;
try
{
    policy = new SeatResolver().Resolve(new SeatSpec($"model:{model}"));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"the seat could not be resolved: {ex.Message}");
    return 2;
}

if (args.Contains("--latches")) return await Latches();
if (args.Contains("--pilot")) return await Pilot();
return await Smoke();

// ---------------------------------------------------------------- smoke

async Task<int> Smoke()
{
    var agent = AgentCatalog.Make(design, policy, RestoreServiceDomain.Task, AgentOptions.None, 1);
    var clock = Stopwatch.StartNew();
    var run = await agent.RunAsync(RestoreServiceDomain.NewWorld(), ticks);
    clock.Stop();
    Report($"smoke {model} on the plain restore world", run, clock.Elapsed);
    return 0;
}

// ---------------------------------------------------------------- two latches

/// <summary>
/// The model-seat matrix over <see cref="TwoLatches"/>: probe, then commit, with a real language model choosing and
/// the registry judging. Arm C gets a fresh graph per repeat, so nothing leaks between repeats and the only thing it
/// carries is what its own probe run put there. The oracle is world state — did <c>door.open</c> land — and the run
/// also records whether the latch got broken, because failing by guessing and failing by being refused are different
/// failures and only one of them is the fixture working. It reports what each arm paid in retrieved context and when
/// demand-gating opened as well, because two arms that tie on the door and differ on price are not the same result.
/// </summary>
async Task<int> Latches()
{
    Console.WriteLine($"two latches: seat {model}, design {design}, {repeats} repeats per arm, probe then commit");
    Console.WriteLine();
    var arms = new (string Name, Func<(AgentOptions Options, GraphMemoryExtension? Graph)> Make)[]
    {
        ("A none", () => (AgentOptions.None, null)),
        ("C graph", () => WithGraph(GraphMemoryOptions.Default)),
        ("E demand", () => WithGraph(new GraphMemoryOptions { Trigger = GraphMemoryTrigger.OnDemand })),
    };
    var rows = new List<LatchRow>();
    foreach (var (arm, make) in arms)
    for (var repeat = 1; repeat <= repeats; repeat++)
    {
        // A fresh graph per repeat per arm: the only thing carried in is what this arm's own probe run put there.
        var (options, graph) = make();

        var probeWorld = new ProbeWorld();
        var commitWorld = new CommitWorld();
        var clock = Stopwatch.StartNew();
        string? fault = null;
        AgentRun probe, commit;
        var (notesAfterProbe, retrievalsAfterProbe, gateAfterProbe) = (0, 0, 0);
        try
        {
            probe = await AgentCatalog.Make(design, policy, ProbeWorld.Task, options, repeat).RunAsync(probeWorld, 10);
            (notesAfterProbe, retrievalsAfterProbe, gateAfterProbe) = Counted(graph);
            commit = await AgentCatalog.Make(design, policy, CommitWorld.Task, options, repeat).RunAsync(commitWorld, 10);
        }
        catch (Exception ex)
        {
            probe = commit = new AgentRun(design, false, 0, [], []);
            fault = $"{ex.GetType().Name}: {ex.Message}";
        }

        clock.Stop();
        var acted = commit.Trace.Where(t => t.Level == "cell").Select(t => t.Action).ToList();
        var broken = commitWorld.Has(TwoLatches.Broken);
        var open = commitWorld.Has(TwoLatches.Open);

        // The two runs share one extension, so its counters are cumulative; the probe's share comes off, because the
        // commitment run is the one whose outcome is being judged. Two arms that tie on the door and differ on what
        // they paid to open it are not the same result, and the last matrix could not tell them apart.
        var (notes, retrievals, gateAt) = Counted(graph);
        var gate = graph is null ? "no graph"
            : gateAt == 0 ? "never"
            : gateAfterProbe > 0 ? $"probe (tick {gateAfterProbe})"
            : $"commit (tick {gateAt})";
        var commitNotes = notes - notesAfterProbe;
        var commitRetrievals = retrievals - retrievalsAfterProbe;
        rows.Add(new LatchRow(arm, repeat, probe.GoalReached, open, broken,
            acted.Count(a => a == "commit-left"), acted.Count(a => a == "commit-right"), commit.Ticks,
            clock.Elapsed.TotalSeconds, fault, commitNotes, commitRetrievals, gate));
        Console.WriteLine($"  {arm,-8} r{repeat,2}  probe={(probe.GoalReached ? "yes" : "NO"),-3}  door={(open ? "OPEN" : "shut"),-4}  " +
                          $"broken={(broken ? "yes" : "no"),-3}  left={acted.Count(a => a == "commit-left")} right={acted.Count(a => a == "commit-right")}  " +
                          $"ticks={commit.Ticks,2}  notes={commitNotes,3}  gate={gate,-18}  {clock.Elapsed.TotalSeconds,5:F1}s{(fault is null ? "" : $"  FAULT {fault}")}");
    }

    static (int Notes, int Retrievals, int GateAt) Counted(GraphMemoryExtension? graph) =>
        graph is null ? (0, 0, 0) : (graph.NotesEmitted, graph.Retrievals, graph.GateOpenedAtTick);

    (AgentOptions, GraphMemoryExtension?) WithGraph(GraphMemoryOptions options)
    {
        var extension = new GraphMemoryExtension(
            new InMemoryGraphStore(GraphSchema.Benchmark()).Open(new GraphCaller(design)), options);
        return (AgentOptions.With(extension), extension);
    }

    var markdown = LatchMarkdown(rows);
    Console.WriteLine();
    Console.WriteLine(markdown);
    Directory.CreateDirectory("experiments/graph-model");
    File.WriteAllText(Path.Combine("experiments/graph-model", "two-latches.md"), markdown);
    return 0;
}

string LatchMarkdown(List<LatchRow> rows)
{
    var inv = CultureInfo.InvariantCulture;
    var sb = new StringBuilder();
    sb.Append("<!-- generated by samples/GraphModelBench; the hand-written account is docs/cognitive-graph/results.md -->\n");
    sb.Append("# Two latches, with a model in the seat\n\n");
    sb.Append($"Seat `{model}`, design `{design}`, {repeats} repeats per arm. Each repeat probes `ProbeWorld` and then\n");
    sb.Append("commits in `CommitWorld`, judged by the registry: `door.open` landed or it did not, and `latch.broken`\n");
    sb.Append("says whether a wrong commitment was actually made. Arm C's graph is fresh per repeat, so the only thing\n");
    sb.Append("it carries is what its own probe run put there.\n\n");
    sb.Append("`notes` and `retrievals` are the commitment run's own, with the probe run's share taken off: both runs\n");
    sb.Append("share one extension, so its counters are cumulative. `gate` says when demand-gating opened — arm E\n");
    sb.Append("retrieves only from the tick on which the seat reported no route, and keeps retrieving after it. An arm\n");
    sb.Append("reading `never` never asked its memory anything, which is not the same result as one that asked and was\n");
    sb.Append("ignored, and the last matrix over this fixture could not tell the two apart.\n\n");
    sb.Append("| arm | repeats | door opened | latch broken | commit-left attempts | mean ticks | mean notes | gate opened | mean wall s |\n|---|---|---|---|---|---|---|---|---|\n");
    foreach (var arm in rows.Select(r => r.Arm).Distinct(StringComparer.Ordinal))
    {
        var g = rows.Where(r => r.Arm == arm).ToList();
        sb.Append("| ").Append(arm)
            .Append(" | ").Append(g.Count.ToString(inv))
            .Append(" | ").Append(g.Count(r => r.Open).ToString(inv))
            .Append(" | ").Append(g.Count(r => r.Broken).ToString(inv))
            .Append(" | ").Append(g.Sum(r => r.Left).ToString(inv))
            .Append(" | ").Append(g.Average(r => r.Ticks).ToString("0.0", inv))
            .Append(" | ").Append(g.Average(r => r.Notes).ToString("0.0", inv))
            .Append(" | ").Append($"{g.Count(r => r.Gate is not ("never" or "no graph"))}/{g.Count}")
            .Append(" | ").Append(g.Average(r => r.Seconds).ToString("0.0", inv))
            .Append(" |\n");
    }

    sb.Append("\n| arm | repeat | probe done | door | broken | left | right | ticks | notes | retrievals | gate | wall s | fault |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
    foreach (var r in rows)
        sb.Append("| ").Append(r.Arm).Append(" | ").Append(r.Repeat.ToString(inv))
            .Append(" | ").Append(r.Probe ? "yes" : "**NO**")
            .Append(" | ").Append(r.Open ? "**open**" : "shut")
            .Append(" | ").Append(r.Broken ? "yes" : "no")
            .Append(" | ").Append(r.Left.ToString(inv))
            .Append(" | ").Append(r.Right.ToString(inv))
            .Append(" | ").Append(r.Ticks.ToString(inv))
            .Append(" | ").Append(r.Notes.ToString(inv))
            .Append(" | ").Append(r.Retrievals.ToString(inv))
            .Append(" | ").Append(r.Gate)
            .Append(" | ").Append(r.Seconds.ToString("0.0", inv))
            .Append(" | ").Append(r.Fault ?? "").Append(" |\n");
    return sb.ToString();
}

// ---------------------------------------------------------------- pilot

/// <summary>
/// One scenario, two arms, judged by the registry. The world is the toy restore case with the <c>patchlies</c>
/// barrier on: <c>apply-patch</c> reports success and grants nothing, which is the trap the task's own habit walks
/// straight into. Arm C is told, as observed prior knowledge, that <c>apply-patch</c> waits on a condition the
/// registry does not hold, so it never offers the tool. Arm A is told nothing and has to find out the hard way.
/// <para>
/// The oracle is world state — did <c>alert.resolved</c> land — plus what it cost to get there. Not what the agent
/// said about itself, which is the one thing an agent is least qualified to report.
/// </para>
/// </summary>
async Task<int> Pilot()
{
    var manifest = WorldCatalog.Restore;
    var barriers = manifest.Set("patchlies");
    var arms = new (string Name, bool Graph)[] { ("A none", false), ("C graph", true) };
    var rows = new List<PilotRow>();

    Console.WriteLine($"pilot: {model}, design {design}, {repeats} repeats per arm, world restore+{barriers}");
    Console.WriteLine();

    foreach (var (name, withGraph) in arms)
    for (var repeat = 1; repeat <= repeats; repeat++)
    {
        var caller = new GraphCaller(design);
        var store = new InMemoryGraphStore(GraphSchema.Benchmark());
        var options = AgentOptions.None;
        if (withGraph)
        {
            Seed(store.Open(caller), caller);
            options = AgentOptions.With(new GraphMemoryExtension(store.Open(caller)));
        }

        var agent = AgentCatalog.Make(design, policy, manifest.Task, options, repeat);
        var world = manifest.NewWorld(barriers);
        var clock = Stopwatch.StartNew();
        AgentRun run;
        string? fault = null;
        try
        {
            run = await agent.RunAsync(world, ticks);
        }
        catch (Exception ex)
        {
            run = new AgentRun(design, false, 0, [], []);
            fault = $"{ex.GetType().Name}: {ex.Message}";
        }

        clock.Stop();
        var acted = run.Trace.Where(t => t.Level == "cell" && t.Kind == TraceKind.Acted).Select(t => t.Action).ToList();
        rows.Add(new PilotRow(name, repeat, run.GoalReached, run.Ticks, clock.Elapsed.TotalSeconds,
            acted.Count(a => a == "apply-patch"), acted.Count(a => a == "apply-config"), acted.Count, fault));
        Console.WriteLine($"  {name,-8} r{repeat}  goal={(run.GoalReached ? "yes" : "NO "),-3} ticks={run.Ticks,2} " +
                          $"wall={clock.Elapsed.TotalSeconds,5:F1}s  apply-patch={acted.Count(a => a == "apply-patch")} " +
                          $"apply-config={acted.Count(a => a == "apply-config")}" +
                          (fault is null ? "" : $"  FAULT {fault}"));
    }

    Console.WriteLine();
    Console.WriteLine(Markdown(rows));
    Directory.CreateDirectory("experiments/graph-model");
    File.WriteAllText(Path.Combine("experiments/graph-model", "pilot.md"), Markdown(rows));
    return 0;
}

/// <summary>What arm C knows before it starts: the trap is closed, and the registry agrees it is closed.</summary>
void Seed(ICognitiveGraph graph, GraphCaller caller)
{
    var known = GraphWarrant.Plain with
    {
        Verification = VerificationStatus.Observed,
        Provenance = new GraphProvenance(ProvenanceKind.Observation, "prior-run"),
        Scope = caller.Scope,
    };
    graph.Put(new GraphNode("apply-patch", "Tool", GraphCategory.Task) { Warrant = known });
    graph.Put(new GraphNode("change.freeze", "Condition", GraphCategory.Task) { Warrant = known });
    graph.Put(new GraphEdge("gate", "apply-patch", GraphMemoryExtension.BlockedBy, "change.freeze", GraphCategory.Task) { Warrant = known });
}

string Markdown(List<PilotRow> rows)
{
    var inv = CultureInfo.InvariantCulture;
    var sb = new StringBuilder();
    sb.Append("<!-- generated by samples/GraphModelBench; the hand-written account is docs/cognitive-graph/results.md -->\n");
    sb.Append("# Model-in-the-seat pilot\n\n");
    sb.Append($"Seat `{model}`, design `{design}`, world `restore+patchlies`, {repeats} repeats per arm, at most {ticks} ticks.\n\n");
    sb.Append("The trap: `apply-patch` reports success and grants nothing. Arm C is told beforehand that it waits on a\n");
    sb.Append("condition the registry does not hold, so it is never offered. Arm A is told nothing.\n\n");
    sb.Append("| arm | runs | goal reached | mean ticks | mean wall s | apply-patch attempts | apply-config attempts |\n");
    sb.Append("|---|---|---|---|---|---|---|\n");
    foreach (var arm in rows.Select(r => r.Arm).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal))
    {
        var g = rows.Where(r => r.Arm == arm).ToList();
        sb.Append("| ").Append(arm)
            .Append(" | ").Append(g.Count.ToString(inv))
            .Append(" | ").Append(g.Count(r => r.Goal).ToString(inv)).Append('/').Append(g.Count.ToString(inv))
            .Append(" | ").Append(g.Average(r => r.Ticks).ToString("0.0", inv))
            .Append(" | ").Append(g.Average(r => r.Seconds).ToString("0.0", inv))
            .Append(" | ").Append(g.Sum(r => r.PatchAttempts).ToString(inv))
            .Append(" | ").Append(g.Sum(r => r.ConfigAttempts).ToString(inv))
            .Append(" |\n");
    }

    sb.Append("\n| arm | repeat | goal | ticks | wall s | patch | config | actions | fault |\n|---|---|---|---|---|---|---|---|---|\n");
    foreach (var r in rows)
        sb.Append("| ").Append(r.Arm).Append(" | ").Append(r.Repeat.ToString(inv))
            .Append(" | ").Append(r.Goal ? "yes" : "**NO**")
            .Append(" | ").Append(r.Ticks.ToString(inv))
            .Append(" | ").Append(r.Seconds.ToString("0.0", inv))
            .Append(" | ").Append(r.PatchAttempts.ToString(inv))
            .Append(" | ").Append(r.ConfigAttempts.ToString(inv))
            .Append(" | ").Append(r.Actions.ToString(inv))
            .Append(" | ").Append(r.Fault ?? "").Append(" |\n");
    return sb.ToString();
}

void Report(string title, AgentRun run, TimeSpan wall)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine($"goal={run.GoalReached} ticks={run.Ticks} escalations={run.Escalations} blocks={run.Blocks} wall={wall.TotalSeconds:F1}s");
    Console.WriteLine($"per tick: {(run.Ticks == 0 ? 0 : wall.TotalMilliseconds / run.Ticks):F0} ms");
    Console.WriteLine();
    foreach (var row in run.Trace.Take(20))
        Console.WriteLine($"  {row.Tick,3} {row.Level,-6} {row.Action,-20} {row.Kind,-12} {Cut(row.Detail)}");
}

string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

static string Cut(string? text) => text is null ? "" : text.Length <= 90 ? text : text[..90] + "...";

internal sealed record PilotRow(string Arm, int Repeat, bool Goal, int Ticks, double Seconds,
    int PatchAttempts, int ConfigAttempts, int Actions, string? Fault);

internal sealed record LatchRow(string Arm, int Repeat, bool Probe, bool Open, bool Broken,
    int Left, int Right, int Ticks, double Seconds, string? Fault, int Notes, int Retrievals, string Gate);
