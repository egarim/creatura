using Creatura.Scenarios;

using Creatura.Seats;

// dotnet run --project samples/Convoy -- [--arm A..L|S|N] [--seed n] [--replay run.jsonl] [--quiet]
//     [--model <catalog name>] [--models-file models.json] [--calls calls.jsonl] [--ticks n] [--network <weights.json>]
// dotnet run --project samples/Convoy -- --battery [--seeds 50] [--arms J,S] [--model <name>] ...
// dotnet run --project samples/Convoy -- --dataset <dir> [--arms J] [--seeds 100-599] [--all-runs] [--only-landed]
// --record <file.jsonl> keeps every decision of a run (or a battery) for training (docs/design-trained-models.md).
// A model (arm S, or --model with any arm) is described in docs/convoy-models.md. Arm N's network is the embedded
// convoy-policy-j unless --network names a weights file from tools/train/train_policy.py; arm R's is convoy-policy-j-trial.
// dotnet run --project samples/Convoy -- --rollouts runs.jsonl [--seeds 1000-1063] [--network w.json] [--temperature 2] [--sample-seed k]
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var catalog = Option("--models-file") is { } modelsFile ? ModelCatalog.Load(modelsFile) : null;
using var calls = Option("--calls") is { } callsFile ? new CallLog(callsFile) : null;
var model = Option("--model");
var ticks = Option("--ticks") is { } t ? int.Parse(t) : (int?)null;
var network = Option("--network");

// --model fills the seat of the arms that carry a model (arm S). It reaches another arm only when that arm is named
// with --arm for a single run: in a battery, "--arms J,S --model x" must leave J's planner as the baseline.
ConvoySetup Adjust(ConvoySetup setup) => setup with
{
    SeatModel = model is not null && (setup.SeatModel is not null || Option("--arm") is not null) ? model : setup.SeatModel,
    SeatNetwork = network ?? setup.SeatNetwork,
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

IReadOnlyList<ConvoyArm> Arms(IReadOnlyList<ConvoyArm> fallback) => Option("--arms") is { } list
    ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => Enum.Parse<ConvoyArm>(a, ignoreCase: true)).ToList()
    : fallback;

// Trial runs for training by trial (tools/train/train_trial.py): record only, with the network's choices sampled.
if (Option("--rollouts") is { } rolloutsFile)
{
    var range = (Option("--seeds") ?? "1000-1063").Split('-');
    var (from, to) = (int.Parse(range[0]), int.Parse(range[^1]));
    if (from < ConvoyDatasets.FirstTrainingSeed)
    {
        Console.Error.WriteLine($"seeds 0 to {ConvoyDatasets.FirstTrainingSeed - 1} belong to the battery and are never trained on");
        return 2;
    }
    var temperature = Option("--temperature") is { } tv ? double.Parse(tv, System.Globalization.CultureInfo.InvariantCulture) : (double?)null;
    var sampleSeed = int.Parse(Option("--sample-seed") ?? "0");
    var trials = new ConvoyRecorder();
    foreach (var trialArm in Arms([ConvoyArm.N]))
    {
        for (var s = from; s <= to; s++)
        {
            var trial = await new ConvoyScenario(Adjust(ConvoySetup.For(trialArm, s)) with { SeatTemperature = temperature, SampleSeed = sampleSeed }, recorder: trials).RunAsync();
            foreach (var error in trial.Errors) Console.Error.WriteLine($"{trialArm} seed {s}: {error}");
        }
    }
    using (var trialFile = new StreamWriter(rolloutsFile)) trials.WriteJsonl(trialFile);
    var all = trials.Runs.Select(r => r.Measures).ToList();
    Console.WriteLine($"{all.Count} runs: delivered {all.Sum(m => m.Delivered)}/{all.Sum(m => m.Demand)}, lies {all.Sum(m => m.LiesBelieved)}, stranded {all.Sum(m => m.Stranded)}, wasted {all.Sum(m => m.WastedTrips)}");
    return 0;
}

// A training dataset: record the arms over a range of seeds outside the battery's, then export (docs/design-trained-models.md).
if (Option("--dataset") is { } datasetDir)
{
    var range = (Option("--seeds") ?? "100-599").Split('-');
    var (from, to) = (int.Parse(range[0]), int.Parse(range[^1]));
    if (from < ConvoyDatasets.FirstTrainingSeed)
    {
        Console.Error.WriteLine($"seeds 0 to {ConvoyDatasets.FirstTrainingSeed - 1} belong to the battery and are never trained on; start at {ConvoyDatasets.FirstTrainingSeed} or above");
        return 2;
    }
    var recorder = new ConvoyRecorder();
    foreach (var datasetArm in Arms([ConvoyArm.J]))
    {
        for (var s = from; s <= to; s++)
        {
            var recorded = await new ConvoyScenario(Adjust(ConvoySetup.For(datasetArm, s)), calls: calls, catalog: catalog, recorder: recorder).RunAsync();
            foreach (var error in recorded.Errors) Console.Error.WriteLine($"{datasetArm} seed {s}: {error}");
        }
        Console.Error.WriteLine($"arm {datasetArm}: seeds {from} to {to} recorded");
    }
    Directory.CreateDirectory(datasetDir);
    using (var runsFile = new StreamWriter(Path.Combine(datasetDir, "runs.jsonl"))) recorder.WriteJsonl(runsFile);
    var card = ConvoyDatasets.Export(recorder.Runs, new DatasetOptions { OnlyServed = !args.Contains("--all-runs"), OnlyLanded = args.Contains("--only-landed") }, datasetDir);
    Console.WriteLine($"dataset {Path.GetFullPath(datasetDir)}: {card.Runs} of {recorder.Runs.Count} runs kept, {card.Decisions:N0} decisions, {card.Transitions:N0} transitions, "
        + $"{card.Features.Count} features (schema {card.SchemaHash}), {card.Actions.Count} actions");
    return 0;
}

var recordTo = Option("--record");
var recorderForRun = recordTo is null ? null : new ConvoyRecorder();

if (args.Contains("--battery"))
{
    var seeds = int.Parse(Option("--seeds") ?? "50");
    var arms = Arms(ConvoyBattery.DefaultArms);
    var report = await ConvoyBattery.RunAsync(seeds, arms, setup => new ConvoyScenario(Adjust(setup), calls: calls, catalog: catalog, recorder: recorderForRun), line => Console.Error.WriteLine(line));
    Console.WriteLine(report.Render());
    if (recordTo is not null) { using var recordFile = new StreamWriter(recordTo); recorderForRun!.WriteJsonl(recordFile); }
    return 0;
}

var arm = Enum.Parse<ConvoyArm>(Option("--arm") ?? (model is not null ? "S" : network is not null ? "N" : "A"), ignoreCase: true);
var seed = int.Parse(Option("--seed") ?? "1");
using var file = Option("--replay") is { } path ? new StreamWriter(path) : null;
var observer = new StoryPrinter(Console.Out, args.Contains("--quiet"), file is null ? null : new JsonlReplayWriter(file));
var run = await new ConvoyScenario(Adjust(ConvoySetup.For(arm, seed)), calls: calls, catalog: catalog, recorder: recorderForRun).RunAsync(observer);
if (recordTo is not null)
{
    using var recordFile = new StreamWriter(recordTo);
    recorderForRun!.WriteJsonl(recordFile);
    Console.WriteLine($"Recorded {recorderForRun.Runs.Sum(r => r.Decisions.Count)} decisions to {recordTo}.");
}
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
