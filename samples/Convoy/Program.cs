using Creatura.Scenarios;

// dotnet run --project samples/Convoy -- [--arm A|B|C|D|E] [--seed n] [--replay run.jsonl] [--quiet]
//                                         --battery [--seeds 50]
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

if (args.Contains("--battery"))
{
    var seeds = int.Parse(Option("--seeds") ?? "50");
    var report = await ConvoyBattery.RunAsync(seeds, progress: line => Console.Error.WriteLine(line));
    Console.WriteLine(report.Render());
    return 0;
}

var arm = Enum.Parse<ConvoyArm>(Option("--arm") ?? "A", ignoreCase: true);
var seed = int.Parse(Option("--seed") ?? "1");
using var file = Option("--replay") is { } path ? new StreamWriter(path) : null;
var observer = new StoryPrinter(Console.Out, args.Contains("--quiet"), file is null ? null : new JsonlReplayWriter(file));
var run = await ConvoyScenario.Create(arm, seed).RunAsync(observer);
foreach (var error in run.Errors) Console.Error.WriteLine(error);
return run.Errors.Count == 0 ? 0 : 1;

/// <summary>Prints the story as it happens, and passes every line on to a replay writer.</summary>
sealed class StoryPrinter(TextWriter output, bool quiet, IScenarioObserver? also) : IScenarioObserver
{
    public void Start(ReplayHeader header)
    {
        also?.Start(header);
        output.WriteLine($"convoy, arm {header.Arm} ({header.ArmText}), seed {header.Seed}");
        foreach (var fault in header.Faults) output.WriteLine($"  {fault}");
    }

    public void Tick(ReplayTick tick)
    {
        also?.Tick(tick);
        if (quiet) return;
        foreach (var e in tick.Events.Where(e => e.Kind is not ("message" or "delayed"))) output.WriteLine($"t{tick.Tick,-3} {e.Text}");
    }

    public void End(ReplaySummary summary)
    {
        also?.End(summary);
        foreach (var line in summary.Outcome) output.WriteLine(line);
    }
}
