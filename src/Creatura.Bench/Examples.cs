using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Creatura.Bench;

/// <summary>
/// One training example as a run leaves it: what the seat could read at the tick, what was suggested, and what the
/// registry said it came to. The context is the <see cref="TickContext"/> rendered as text (facts, visible tools, freedom,
/// the self-model, the recent history), not the seat's prompt: the loop never shows an extension the prompt.
/// </summary>
/// <param name="Run">Which run the example belongs to: the agent's name and a number per recorder.</param>
/// <param name="Tick">The loop tick.</param>
/// <param name="Context">The rendered context the decision was made in.</param>
/// <param name="Suggestion">The tool that was run.</param>
/// <param name="Rationale">The decision row's detail for that tick, when the trace holds one.</param>
/// <param name="Verdict">What the action came to.</param>
public sealed record Example(string Run, int Tick, string Context, string Suggestion, string? Rationale, ActionVerdict Verdict);

/// <summary>Where a run's examples go, and how the run's outcome class is attached to them at the end.</summary>
public interface IExampleSink
{
    void Add(Example example);

    /// <summary>The run is over: its class, as the oracle read it, for every example added under this run.</summary>
    void Close(string run, OutcomeClass outcome);
}

/// <summary>Examples in memory, with the class of each closed run.</summary>
public sealed class InMemoryExampleSink : IExampleSink
{
    private readonly List<Example> _examples = [];
    private readonly Dictionary<string, OutcomeClass> _outcomes = new(StringComparer.Ordinal);

    public IReadOnlyList<Example> Examples => _examples;

    public IReadOnlyDictionary<string, OutcomeClass> Outcomes => _outcomes;

    public void Add(Example example) => _examples.Add(example);

    public void Close(string run, OutcomeClass outcome) => _outcomes[run] = outcome;

    /// <summary>The examples of one run with its class, or null while the run is open.</summary>
    public OutcomeClass? OutcomeOf(string run) => _outcomes.TryGetValue(run, out var o) ? o : null;
}

/// <summary>
/// Examples as JSON lines, appended to one file: an <c>example</c> line per action and a <c>close</c> line per run,
/// in the order they happened. A reader joins them on the run name.
/// </summary>
public sealed class JsonlExampleSink : IExampleSink, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public JsonlExampleSink(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
    }

    public string Path { get; }

    public void Add(Example example)
    {
        lock (_gate)
        {
            _writer.WriteLine(JsonSerializer.Serialize(new { example.Run, example.Tick, example.Context, example.Suggestion, example.Rationale, Verdict = example.Verdict.ToString() }, Json));
            _writer.Flush();
        }
    }

    public void Close(string run, OutcomeClass outcome)
    {
        lock (_gate)
        {
            _writer.WriteLine(JsonSerializer.Serialize(new { Close = run, Outcome = outcome.AsRow() }, Json));
            _writer.Flush();
        }
    }

    public void Dispose() => _writer.Dispose();
}

/// <summary>
/// The fine-tune seam. An observer-only extension that writes one <see cref="Example"/> per judged action (the rendered
/// context, the suggestion, the verdict) and, at sleep, the run's outcome class as the oracle reads it, into an
/// <see cref="IExampleSink"/>. It shapes nothing, vetoes nothing and changes no row. Nothing consumes what it writes:
/// no trainer, no export to a tuning format, no model reads these examples yet. The seam is here so that when something
/// does, the rows it needs already exist, with the registry's verdict and not the agent's word on each.
/// </summary>
public sealed class ExampleRecorder : IAgentExtension
{
    private readonly IExampleSink _sink;
    private readonly IOracle? _oracle;
    private int _runs;
    private string _run = "";

    /// <param name="sink">Where the examples go.</param>
    /// <param name="oracle">The oracle that classifies the run at sleep; the registry oracle over the run's task when null.</param>
    public ExampleRecorder(IExampleSink sink, IOracle? oracle = null)
    {
        _sink = sink;
        _oracle = oracle;
    }

    public string Name => "examples";

    public IExampleSink Sink => _sink;

    /// <summary>The run's name in the sink: the agent's name and this recorder's run count.</summary>
    public string CurrentRun => _run;

    public void BeforeRun(TickContext context) => _run = $"{context.Agent}#{++_runs}";

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        if (_run.Length == 0) _run = $"{context.Agent}#{++_runs}";
        var rationale = context.Trace.LastOrDefault(e => e.Tick == context.Tick && e.Level != "cell" && e.Action == tool.Id)?.Detail;
        _sink.Add(new Example(_run, context.Tick, Render(context), tool.Id, rationale, verdict));
    }

    public void Sleep(SleepContext context)
    {
        var oracle = _oracle ?? new RegistryOracle(context.Task);
        var run = new AgentRun(context.Agent, context.GoalReached, context.LastTick, context.Trace, context.Notes);
        _sink.Close(_run, oracle.Classify(run, context.World));
    }

    /// <summary>The context as the seat could have read it: facts, the tools on offer with their promises, freedom, the self-model, the recent history.</summary>
    public static string Render(TickContext c)
    {
        var sb = new StringBuilder();
        sb.Append("goal: ").Append(c.Task.GoalText).Append('\n');
        sb.Append("facts: ").Append(c.World.Facts.Count == 0 ? "none" : string.Join(", ", c.World.Facts.Order(StringComparer.Ordinal))).Append('\n');
        sb.Append("tools:\n");
        foreach (var t in c.Visible)
        {
            sb.Append("  ").Append(t.Id).Append(" requires [").Append(string.Join(", ", t.Requires)).Append("] grants [").Append(string.Join(", ", t.Grants)).Append("] (").Append(t.Kind).Append(")\n");
        }

        sb.Append("tick ").Append(c.Tick).Append(" of ").Append(c.MaxTicks).Append("; freedom ").Append(c.Freedom.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        if (c.SelfModel is { } self) sb.Append("self-model:\n").Append(self.Render()).Append('\n');
        if (c.History.Count > 0) sb.Append("history: ").Append(string.Join(" | ", c.History)).Append('\n');
        return sb.ToString();
    }
}
