using System.Text.Json;
using Creatura.Bench;

namespace Creatura.Scenarios;

/// <summary>A tool as the seat saw it: what it needs, what it promises, its kind.</summary>
public sealed record ToolRecord(string Id, IReadOnlyList<string> Requires, IReadOnlyList<string> Grants, string Kind)
{
    public bool NeedsPayload { get; init; }

    public string? PayloadHint { get; init; }

    public Tool ToTool() => new(Id, Requires, Grants, Kind) { NeedsPayload = NeedsPayload, PayloadHint = PayloadHint };
}

/// <summary>
/// Everything a seat was given for one decision (the <see cref="PolicyContext"/> without its goal test), kept so the
/// decision can be rendered later in exactly the form a seat reads it.
/// </summary>
public sealed record SeatContext(IReadOnlyList<string> Facts, IReadOnlyList<ToolRecord> Tools, IReadOnlyList<string> Notes, string GoalText,
    int Tick, int MaxTicks, double Freedom, string? Stage, string? SelfModel, IReadOnlyList<string> Preferred, IReadOnlyList<string> History)
{
    public static SeatContext Of(PolicyContext c) => new(
        c.Facts.Order(StringComparer.Ordinal).ToList(),
        c.Tools.Select(t => new ToolRecord(t.Id, t.Requires, t.Grants, t.Kind) { NeedsPayload = t.NeedsPayload, PayloadHint = t.PayloadHint }).ToList(),
        c.Notes.ToList(), c.GoalText, c.Tick, c.MaxTicks, c.Freedom, c.Stage, c.SelfModel, c.Preferred.ToList(), c.History.ToList());

    public PolicyContext ToPolicyContext() => new()
    {
        Facts = Facts.ToHashSet(StringComparer.Ordinal),
        Tools = Tools.Select(t => t.ToTool()).ToList(),
        Goal = facts => facts.Contains("convoy.done"),
        GoalText = GoalText,
        Tick = Tick,
        MaxTicks = MaxTicks,
        Freedom = Freedom,
        Stage = Stage,
        SelfModel = SelfModel,
        Preferred = Preferred,
        History = History,
        Notes = Notes,
    };
}

/// <summary>
/// One decision as the recorder keeps it: who and when, what the seat was given and chose (with the features, the mask
/// over the action vocabulary and the chosen index), and what it came to (the control layer's verdict, what the world
/// reported, and the facts afterwards). A decision whose action never ran has no verdict.
/// </summary>
public sealed class DecisionRecord
{
    public required string Truck { get; init; }
    public required int Tick { get; init; }
    public required SeatContext Seat { get; init; }
    public required IReadOnlyList<double> Features { get; init; }
    public required IReadOnlyList<int> Mask { get; init; }
    public string? Chosen { get; set; }
    public string? Rationale { get; set; }
    public int? ActionIndex { get; set; }
    public string? Verdict { get; set; }
    public bool? Reported { get; set; }
    public string? Observation { get; set; }
    public IReadOnlyList<string> FactsAfter { get; set; } = [];
}

/// <summary>One recorded run: its setup, what the judge read from the world, and every decision in the order made.</summary>
public sealed record RecordedRun(ConvoySetup Setup, ConvoyMeasures Measures, IReadOnlyList<DecisionRecord> Decisions)
{
    public string Id => $"{Setup.Arm}/{Setup.Seed}";
}

/// <summary>
/// The convoy's recorder (docs/design-trained-models.md, step 1). Per truck it puts a recording wrapper around the
/// seat, which keeps exactly what the seat was given and what it chose, and an observer extension, which adds what the
/// action came to. Beside them it attaches Creatura's generic fine-tune seam, <see cref="ExampleRecorder"/>, unchanged,
/// into <see cref="Examples"/>. It observes only: a run recorded replays byte for byte as one that is not (T1). Runs
/// accumulate in <see cref="Runs"/>; <see cref="WriteJsonl"/> keeps them on disk and <see cref="ReadJsonl"/> reads them back.
/// </summary>
public sealed class ConvoyRecorder
{
    private readonly List<RecordedRun> _runs = [];
    private ConvoySetup? _setup;
    private List<DecisionRecord> _decisions = [];

    public IReadOnlyList<RecordedRun> Runs => _runs;

    /// <summary>What the generic seam wrote, for every run recorded.</summary>
    public InMemoryExampleSink Examples { get; } = new();

    internal void BeginRun(ConvoySetup setup)
    {
        _setup = setup;
        _decisions = [];
    }

    internal void EndRun(ConvoyMeasures measures)
    {
        if (_setup is null) return;
        _runs.Add(new RecordedRun(_setup, measures, _decisions));
        _setup = null;
    }

    /// <summary>The truck's seat, wrapped so every decision is recorded; the decision itself is the seat's, unchanged.</summary>
    internal IPolicy Seat(IPolicy seat, TruckSpec spec, RoadMap map) => new RecordingSeat(this, seat, spec, map);

    /// <summary>The truck's observers: the verdict on each recorded decision, and the generic seam.</summary>
    internal IEnumerable<IAgentExtension> Extensions(TruckSpec spec) => [new Outcomes(this, spec.Name), new ExampleRecorder(Examples)];

    private sealed class RecordingSeat(ConvoyRecorder recorder, IPolicy inner, TruckSpec spec, RoadMap map) : IPolicy
    {
        public string Name => inner.Name;

        public async Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
        {
            var record = new DecisionRecord
            {
                Truck = spec.Name,
                Tick = context.Tick,
                Seat = SeatContext.Of(context),
                Features = ConvoyFeatures.Encode(map, spec.Machine, context.Facts, context.Notes, context.Tick, context.MaxTicks),
                Mask = ConvoyActions.Mask(map, context.Tools.Select(t => t.Id)),
            };
            recorder._decisions.Add(record);
            var suggestion = await inner.NextAsync(context, cancellationToken).ConfigureAwait(false);
            record.Chosen = suggestion.Tool;
            record.Rationale = suggestion.Rationale;
            if (suggestion.Tool is { } tool && ConvoyActions.Vocabulary(map).IndexOf(tool) is var i and >= 0) record.ActionIndex = i;
            return suggestion;
        }
    }

    private sealed class Outcomes(ConvoyRecorder recorder, string truck) : IAgentExtension
    {
        public string Name => "convoy-recorder";

        public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
        {
            var decision = recorder._decisions.LastOrDefault(d => d.Truck == truck && d.Tick == context.Tick && d.Chosen == tool.Id && d.Verdict is null);
            if (decision is null) return;
            decision.Verdict = verdict.ToString();
            decision.Reported = outcome.Succeeded;
            decision.Observation = outcome.Observation;
            decision.FactsAfter = context.World.Facts.Order(StringComparer.Ordinal).ToList();
        }
    }

    // ------------------------------------------------------------------ on disk

    private static readonly JsonSerializerOptions Json = new(JsonlReplayWriter.Options);

    private sealed record RunLine(string Type, string Arm, int Seed, string? SeatModel, int MaxTicks, ConvoyMeasures Measures, int Decisions);

    private sealed record DecisionLine(string Type, string Run, DecisionRecord Decision);

    /// <summary>
    /// The recorded runs as JSON lines: a <c>run</c> line (arm, seed, measures), then a <c>decision</c> line for each of its
    /// decisions. A run is rebuilt from its arm and seed when read back, so the map and trucks come from the code.
    /// </summary>
    public void WriteJsonl(TextWriter output)
    {
        foreach (var run in _runs)
        {
            output.WriteLine(JsonSerializer.Serialize(new RunLine("run", run.Setup.Arm.ToString(), run.Setup.Seed, run.Setup.SeatModel, run.Setup.MaxTicks, run.Measures, run.Decisions.Count), Json));
            foreach (var d in run.Decisions) output.WriteLine(JsonSerializer.Serialize(new DecisionLine("decision", run.Id, d), Json));
        }
        output.Flush();
    }

    public static IReadOnlyList<RecordedRun> ReadJsonl(string path)
    {
        var runs = new List<RecordedRun>();
        ConvoySetup? setup = null;
        ConvoyMeasures? measures = null;
        var decisions = new List<DecisionRecord>();
        void Flush()
        {
            if (setup is not null && measures is not null) runs.Add(new RecordedRun(setup, measures, decisions));
        }
        foreach (var line in File.ReadLines(path).Where(l => l.Length > 0))
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("type").GetString() == "run")
            {
                Flush();
                var r = JsonSerializer.Deserialize<RunLine>(line, Json)!;
                setup = ConvoySetup.For(Enum.Parse<ConvoyArm>(r.Arm), r.Seed) with { SeatModel = r.SeatModel, MaxTicks = r.MaxTicks };
                measures = r.Measures;
                decisions = [];
            }
            else
            {
                decisions.Add(JsonSerializer.Deserialize<DecisionLine>(line, Json)!.Decision);
            }
        }
        Flush();
        return runs;
    }
}
