using System.Globalization;
using Creatura.Graph.Bench;

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
IReadOnlyList<string>? only = null;
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
        default:
            Console.Error.WriteLine($"unknown argument '{args[i]}' (try --variants, --seed, --out, --only, --quiet)");
            return 2;
    }
}

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
