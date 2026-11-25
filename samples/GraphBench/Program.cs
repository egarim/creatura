using System.Globalization;
using Creatura;
using Creatura.Graph;
using Creatura.Graph.Bench;
using Creatura.Worlds;

// The cognitive graph benchmark (issue #110, docs/cognitive-graph/experimental-methodology.md): fourteen fixtures,
// four memory arms, one answerer, and thirteen hypotheses whose thresholds were written down before this program
// was ever run. Nothing here needs a model, a key or a network.
//
//   dotnet run --project samples/GraphBench                          one pass, the fixtures as written
//   dotnet run --project samples/GraphBench -- --variants 8          eight name-permuted variants of each
//   dotnet run --project samples/GraphBench -- --only G01,G05        two scenarios
//   dotnet run --project samples/GraphBench -- --seed 20261125       a published seed, so a run reproduces
//
// The exit code is the decision gate from #110: non-zero when an arm surfaced knowledge it should not have.

var variants = 1;
var seed = 20261125;
var output = Path.Combine("experiments", "graph");
var quiet = false;
var only = (IReadOnlyList<string>?)null;
var gate = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--variants" when i + 1 < args.Length:
            variants = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--seed" when i + 1 < args.Length:
            seed = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--out" when i + 1 < args.Length:
            output = args[++i];
            break;
        case "--only" when i + 1 < args.Length:
            only = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            break;
        case "--quiet":
            quiet = true;
            break;
        case "--gate":
            gate = true;
            break;
        default:
            Console.Error.WriteLine($"unknown argument '{args[i]}' (try --variants, --seed, --out, --only, --quiet, --gate)");
            return 2;
    }
}

if (gate) return await Gate();

Console.WriteLine($"cognitive graph benchmark: {Scenarios.All.Count} scenarios, 4 arms, {variants} variant(s), seed {seed}");
Console.WriteLine($"pass rule: {Preregistration.PassRule}");
Console.WriteLine();

var results = Runner.Run(new RunOptions
{
    Variants = variants,
    Seed = seed,
    Only = only,
    Log = quiet ? null : line => Console.WriteLine("  " + line),
});

Directory.CreateDirectory(output);
var jsonl = Runner.WriteJsonl(results, Path.Combine(output, "results.jsonl"));
var report = GraphReport.Write(results, output);

Console.WriteLine();
Console.WriteLine(GraphReport.ToMarkdown(results));
Console.WriteLine($"seed {seed}; {results.Count} rows; raw results {jsonl}; report {report}");

var cleared = Preregistration.GateCleared(results);
Console.WriteLine(cleared
    ? $"decision gate cleared: {Preregistration.DecisionGate}"
    : $"DECISION GATE FAILED: an arm surfaced forbidden knowledge in {string.Join(", ", Preregistration.SecurityViolations(results).Select(v => $"{v.Scenario}/{v.Arm}"))}");
return cleared ? 0 : 1;

// ------------------------------------------------------------------ the gate

/// <summary>
/// What demand-gating costs, measured with no model in the seat — context cost is not stochastic, so paying DeepSeek
/// to count notes would be paying for noise. The world is the plain restore case: the planner reaches the goal
/// unaided, so every note the memory hands it is a note it did not need. That is the objection in a fixture —
/// "the memory is not useful in small scenarios" — and the only honest answer is a count.
/// </summary>
async Task<int> Gate()
{
    var arms = new (string Name, GraphMemoryOptions Options)[]
    {
        ("C graph  always", GraphMemoryOptions.Default),
        ("E demand gated", new GraphMemoryOptions { Trigger = GraphMemoryTrigger.OnDemand }),
    };
    var rows = new List<GateRow>();
    foreach (var (name, options) in arms)
    {
        var extension = new GraphMemoryExtension(TaskGraph(), options);
        IAgent agent = new ReferenceLoop(new GateRegulator(), new PlannerPolicy(), RestoreServiceDomain.Task,
            AgentOptions.With(extension), name: "gate");
        var run = await agent.RunAsync(RestoreServiceDomain.NewWorld(), 20);
        var freedom = run.Trace.Select(t => t.Freedom).ToList();
        rows.Add(new GateRow(name, run.GoalReached, run.Ticks, extension.NotesEmitted,
            extension.NoteCharacters, extension.Retrievals, extension.Episodes,
            freedom.Count == 0 ? 0 : freedom.Min(), freedom.Count == 0 ? 0 : freedom.Max()));
    }

    Console.WriteLine();
    Console.WriteLine("the gate: plain restore world, planner in the seat, task knowledge the agent does not need");
    Console.WriteLine();
    Console.WriteLine("| arm | goal | ticks | notes emitted | note chars | retrievals | episodes | freedom min | freedom max |");
    Console.WriteLine("|---|---|---|---|---|---|---|---|---|");
    foreach (var r in rows)
        Console.WriteLine($"| {r.Name} | {(r.Goal ? "yes" : "**NO**")} | {r.Ticks} | {r.Notes} | {r.Characters} | {r.Retrievals} | {r.Episodes} | {r.FreedomMin:0.00} | {r.FreedomMax:0.00} |");

    var (always, demand) = (rows[0], rows[1]);
    Console.WriteLine();
    Console.WriteLine(demand.Notes < always.Notes
        ? $"Gating cut the context from {always.Notes} notes / {always.Characters} characters to {demand.Notes} / {demand.Characters}, same outcome."
        : $"Gating cut nothing: {demand.Notes} notes against {always.Notes}. The threshold is freedom < {GraphMemoryOptions.Default.PressureBelowFreedom:0.00} and this run never rose above {demand.FreedomMax:0.00}, so the gate was open on every tick — it is wired, but on this regulator it never closes.");
    Console.WriteLine();
    Console.WriteLine("Read this as the cost half of H14 only. A planner ignores notes, so identical goals and ticks here are");
    Console.WriteLine("true by construction and prove nothing about success parity; that half needs a model in the seat.");
    return 0;
}

/// <summary>
/// Knowledge about the task that is accurate and useless: what each tool produces. Deliberately no <c>blockedBy</c>
/// edges — those would withhold tools the agent genuinely needs, and then the run would measure the intervention
/// instead of its price.
/// </summary>
ICognitiveGraph TaskGraph()
{
    var caller = new GraphCaller("gate");
    var graph = new InMemoryGraphStore(GraphSchema.Benchmark()).Open(caller);
    var known = GraphWarrant.Plain with
    {
        Verification = VerificationStatus.Observed,
        Provenance = new GraphProvenance(ProvenanceKind.Observation, "prior-run"),
        Scope = caller.Scope,
    };
    foreach (var tool in RestoreServiceDomain.Tools())
    {
        graph.Put(new GraphNode(tool.Id, "Tool", GraphCategory.Task) { Warrant = known });
        foreach (var grant in tool.Grants)
        {
            graph.Put(new GraphNode(grant, "Fact", GraphCategory.Task) { Warrant = known });
            graph.Put(new GraphEdge($"{tool.Id}-produced-{grant}", tool.Id, "produced", grant, GraphCategory.Task)
            {
                Warrant = known,
            });
        }
    }

    return graph;
}

internal sealed class GateRegulator : IRegulator
{
    public Identity Identity { get; } = new("gate", "you");
}

internal sealed record GateRow(string Name, bool Goal, int Ticks, int Notes, int Characters, int Retrievals,
    int Episodes, double FreedomMin, double FreedomMax);
