using Creatura.Scenarios;

using Creatura.Seats;

// dotnet run --project samples/Convoy -- [--arm A..L|S] [--seed n] [--replay run.jsonl] [--quiet]
//     [--model <catalog name>] [--models-file models.json] [--calls calls.jsonl] [--ticks n]
// dotnet run --project samples/Convoy -- --battery [--seeds 50] [--arms J,S] [--model <name>] ...
// A model (arm S, or --model with any arm) is described in docs/convoy-models.md.
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var catalog = Option("--models-file") is { } modelsFile ? ModelCatalog.Load(modelsFile) : null;
using var calls = Option("--calls") is { } callsFile ? new CallLog(callsFile) : null;
var model = Option("--model");
var ticks = Option("--ticks") is { } t ? int.Parse(t) : (int?)null;

ConvoySetup Adjust(ConvoySetup setup) => setup with
{
    SeatModel = model ?? setup.SeatModel,
    MaxTicks = ticks ?? setup.MaxTicks,
};

if (Adjust(ConvoySetup.For(ConvoyArm.S, 0)).SeatModel is { } seatName && (args.Contains("--battery") ? Option("--arms")?.Contains('S') == true : Option("--arm") is "S" or "s" || model is not null))
{
    var endpoint = SeatFactory.Endpoint(seatName, catalog);
    if (SeatFactory.MissingKey(endpoint) is { } missing)
    {
        Console.Error.WriteLine($"{seatName} needs an API key in one of these environment variables: {missing.Replace("|", ", ")} (see docs/convoy-models.md)");
        return 2;
    }
    Console.Error.WriteLine($"seat: {endpoint.Name} = {endpoint.Model} through {endpoint.ProviderName} at {endpoint.BaseUrl}");
}

if (args.Contains("--battery"))
{
    var seeds = int.Parse(Option("--seeds") ?? "50");
    var arms = Option("--arms") is { } list
        ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => Enum.Parse<ConvoyArm>(a, ignoreCase: true)).ToList()
        : ConvoyBattery.DefaultArms;
    var report = await ConvoyBattery.RunAsync(seeds, arms, setup => new ConvoyScenario(Adjust(setup), calls: calls, catalog: catalog), line => Console.Error.WriteLine(line));
    Console.WriteLine(report.Render());
    return 0;
}

var arm = Enum.Parse<ConvoyArm>(Option("--arm") ?? (model is null ? "A" : "S"), ignoreCase: true);
var seed = int.Parse(Option("--seed") ?? "1");
using var file = Option("--replay") is { } path ? new StreamWriter(path) : null;
var observer = new StoryPrinter(Console.Out, args.Contains("--quiet"), file is null ? null : new JsonlReplayWriter(file));
var run = await new ConvoyScenario(Adjust(ConvoySetup.For(arm, seed)), calls: calls, catalog: catalog).RunAsync(observer);
if (run.Summary.Seat is { } seat)
{
    Console.WriteLine($"The seat ({seat.Model}) made {seat.Calls} calls ({seat.Failed} failed, {seat.Invalid} invalid), {seat.PromptTokens:N0} prompt and {seat.CompletionTokens:N0} completion tokens, "
        + $"cost {(seat.Cost is { } c ? $"${c:0.0000}" : "unknown")}, {seat.Seconds:0} s of model time.");
}
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
