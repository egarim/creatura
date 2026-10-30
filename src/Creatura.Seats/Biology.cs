using System.Globalization;
using Creatura.Bench;

namespace Creatura.Seats;

/// <summary>
/// The body the mind runs on: what the provider publishes about the model and what the bench has measured about it.
/// Published numbers are the sheet; measured numbers come from the seat bench's scoreboard rows for the same model.
/// Anything not known is null and rendered as unknown, never as zero.
/// </summary>
public sealed record ModelBiology(
    string Model,
    string Provider,
    bool Local,
    string Protocol,
    bool Reasoning,
    int? ContextWindow,
    int? ContextAsRun,
    int? MaxOutput,
    string? KnowledgeCutoff,
    decimal? InputPerMillion,
    decimal? OutputPerMillion,
    double? MedianCallSeconds,
    double? TokensPerSecond,
    double? InvalidRate,
    int? MeasuredRuns,
    string? Caveat)
{
    /// <summary>The record as the policy reads it: short lines, numbers with units, unknowns said.</summary>
    public IReadOnlyList<string> Lines()
    {
        static string N(int? v) => v is null ? "unknown" : v.Value.ToString("N0", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"model {Model} via {Provider}{(Local ? " (local, on this machine)" : " (cloud)")}",
            $"protocol {Protocol}: {(Protocol == "tools" ? "you choose by calling exactly one function" : "you answer with one JSON object; prose is not an answer")}",
            $"reasoning {(Reasoning ? "yes: your thinking counts against your output cap" : "no: answer directly")}",
            $"context window {N(ContextWindow)} tokens{(ContextAsRun is not null && ContextAsRun != ContextWindow ? $", run here with {N(ContextAsRun)}" : "")}; max output {N(MaxOutput)} tokens",
            $"price {(InputPerMillion is null ? "unknown" : string.Create(TraceText.Culture, $"${InputPerMillion:0.00} in / ${OutputPerMillion:0.00} out per million tokens"))}",
        };
        if (MeasuredRuns is > 0)
        {
            lines.Add(string.Create(TraceText.Culture, $"measured over {MeasuredRuns} runs here: median call {MedianCallSeconds:0.0} s, {TokensPerSecond:0} tokens/s, {InvalidRate:P0} of your answers were invalid"));
        }

        if (KnowledgeCutoff is not null) lines.Add($"knowledge cutoff {KnowledgeCutoff}");
        if (Caveat is not null) lines.Add($"note: {Caveat}");
        return lines;
    }

    /// <summary>The record as a <see cref="Vessel"/>: the model half only. The machine half is the host's to state.</summary>
    public Vessel Vessel() => new()
    {
        Model = Model,
        Protocol = Protocol,
        Reasoning = Reasoning,
        ContextWindow = ContextAsRun ?? ContextWindow,
        SecondsPerCall = MedianCallSeconds,
        TokensPerSecond = TokensPerSecond,
        InputPerMillion = InputPerMillion,
        OutputPerMillion = OutputPerMillion,
        Network = Local ? "local, on this machine" : $"cloud via {Provider}",
    };

    public string OneLine() => $"{Model} ({Protocol}{(Reasoning ? ", reasoning" : "")}, ctx {(ContextAsRun ?? ContextWindow)?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"}, {(MedianCallSeconds is null ? "unmeasured" : string.Create(TraceText.Culture, $"{MedianCallSeconds:0.0}s/call"))})";
}

/// <summary>The published facts of a catalogued model (its endpoint's sheet) joined with the measured ones read from scoreboard rows.</summary>
public static class Biology
{
    public static ModelBiology For(ModelEndpoint endpoint, IReadOnlyList<Row>? measured = null)
    {
        var sheet = endpoint.Sheet ?? ModelSheet.Unknown;
        var rows = measured?.Where(r => string.Equals(r.Seat, endpoint.Name, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        double? median = rows.Count == 0 ? null : rows.Select(r => r.MedianCallSeconds).Order().ElementAt(rows.Count / 2);
        var tps = rows.Where(r => r.TokensPerSecond is not null).Select(r => r.TokensPerSecond!.Value).Order().ToList();
        var calls = rows.Sum(r => r.Calls);
        return new ModelBiology(
            endpoint.Name, endpoint.ProviderName, endpoint.IsLocal, endpoint.Protocol, endpoint.Reasoning,
            sheet.ContextWindow, sheet.ContextAsRun, sheet.MaxOutput, sheet.KnowledgeCutoff, endpoint.InputPerMillion, endpoint.OutputPerMillion,
            median, tps.Count == 0 ? null : tps[tps.Count / 2], calls == 0 ? null : (double)rows.Sum(r => r.Invalid) / calls, rows.Count == 0 ? null : rows.Count, sheet.Caveat);
    }

    /// <summary>Read every scoreboard under a folder (the seat bench's local and cloud runs) for the measured part.</summary>
    public static IReadOnlyList<Row> ReadMeasurements(string modelsRoot)
    {
        var rows = new List<Row>();
        if (!Directory.Exists(modelsRoot)) return rows;
        foreach (var csv in Directory.EnumerateFiles(modelsRoot, "scoreboard.csv", SearchOption.AllDirectories))
        {
            rows.AddRange(LegacyCsv.ReadAll(csv, LegacyShape.Scoreboard));
        }

        return rows;
    }
}

/// <summary>
/// Battery 4: the agent knows what it runs on. Writes the biology into the record the policy reads every
/// tick (and into the self-model's vessel, for the designs that keep one), and at sleep asks the model to
/// state its own limits so the answer can be scored against the record. The design's test is Bach's: a
/// biology the agent is told but never acts on is decoration.
/// </summary>
public sealed class BiologyExtension : IAgentExtension
{
    private readonly ModelBiology _biology;
    private readonly ModelEndpoint? _endpoint;
    private readonly ModelClient? _client;
    private readonly CallLog? _log;

    public BiologyExtension(ModelBiology biology, ModelEndpoint? endpoint = null, ModelClient? client = null, CallLog? log = null)
    {
        _biology = biology;
        _endpoint = endpoint;
        _client = client;
        _log = log;
    }

    public string Name => "biology";

    public ModelBiology Record => _biology;

    /// <summary>The model's own account of its limits, asked at sleep. Null when no model is seated.</summary>
    public string? SelfReport { get; private set; }

    /// <summary>How many of the record's checkable facts the self-report got right: protocol, window, price or locality. 0 to 3.</summary>
    public int SelfReportScore { get; private set; }

    public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible)
    {
        if (context.SelfModel is not null) context.SelfModel.Vessel ??= _biology.Vessel();
        return visible;
    }

    public IEnumerable<string> PolicyNotes(TickContext context) => _biology.Lines();

    public void Sleep(SleepContext context)
    {
        if (_endpoint is null || _client is null) return;
        var (report, reply) = AskAsync(_biology, _endpoint, _client, withRecord: true).GetAwaiter().GetResult();
        SelfReport = report;
        _log?.Write(CallRecord.Of(_endpoint, "self-report", "prose", context.Agent, [], [], reply, !reply.Failed, null));
        SelfReportScore = ScoreReport(_biology, SelfReport);
        context.Notes.Add($"self-report ({SelfReportScore}/3 facts right): {(SelfReport ?? "no answer").Replace('\n', ' ')}");
    }

    /// <summary>
    /// The self-report question. With the record it is a recall test (can the seat read its biology back); without
    /// it, it is what the weights know about the body they run in, which is the baseline the recall is measured against.
    /// </summary>
    public static async Task<(string? Report, ModelReply Reply)> AskAsync(ModelBiology biology, ModelEndpoint endpoint, ModelClient client, bool withRecord)
    {
        var question = "In three short lines, state the limits of the model you are running as: your answer protocol (one function call, or one JSON object), your context window in tokens as run, " +
                       "and your price per million tokens or whether you run locally for free. Facts only.";
        var messages = new List<ChatMessage>
        {
            new("system", withRecord
                ? "You are the decision component of an agent. Answer about yourself from the record you were given during the run, not from guesses."
                : $"You are the decision component of an agent, running as the model '{biology.Model}' through {biology.Provider}. Answer about yourself from what you know; say 'unknown' where you do not know."),
            new("user", (withRecord ? "RECORD GIVEN DURING THE RUN:\n" + string.Join("\n", biology.Lines()) + "\n\n" : "") + question),
        };
        var reply = await client.ChatAsync(endpoint, messages, null, jsonMode: false, maxTokens: endpoint.Reasoning ? 2000 : 200).ConfigureAwait(false);
        return (reply.Failed ? null : reply.Content.Trim(), reply);
    }

    /// <summary>How many of the record's checkable facts a report got right: protocol, window (as run), price or locality. 0 to 3.</summary>
    public static int ScoreReport(ModelBiology biology, string? report)
    {
        if (string.IsNullOrWhiteSpace(report)) return 0;
        var r = report.ToLowerInvariant();
        var score = 0;
        // Negations are read: "one JSON object (no function call)" is a JSON answer, not a tools one.
        var saysTools = (r.Contains("function") || r.Contains("tool")) && !r.Contains("no function") && !r.Contains("not a function") && !r.Contains("no tool") && !r.Contains("not tool");
        var saysJson = r.Contains("json") && !r.Contains("no json") && !r.Contains("not json") && !r.Contains("not a json");
        if (biology.Protocol == "tools" ? saysTools && !(saysJson && !saysTools) : saysJson && !saysTools) score++;
        var window = biology.ContextAsRun ?? biology.ContextWindow;
        if (window is not null && (r.Contains(window.Value.ToString(CultureInfo.InvariantCulture)) || r.Contains(window.Value.ToString("N0", CultureInfo.InvariantCulture)) || r.Contains($"{window.Value / 1000}k") || r.Contains($"{window.Value / 1000},"))) score++;
        if (biology.Local ? r.Contains("local") || r.Contains("free") || r.Contains("$0.00") || r.Contains("$0 ") || r.Contains("0.00 per million") : biology.InputPerMillion is not null && r.Contains(biology.InputPerMillion.Value.ToString("0.00", CultureInfo.InvariantCulture))) score++;
        return score;
    }
}

/// <summary>
/// The biology extension as a name on the switchboard. <see cref="BiologyExtension"/> needs what no
/// <see cref="Creatura.Worlds.ExtensionRequest"/> carries: the seat's endpoint, its measured biology and a client for the self-report
/// at sleep. So a runner binds the seat it is about to run (<see cref="Seat"/>) and the catalog factory
/// registered under <c>biology</c> reads it from here; with the planner in the seat the name is refused, since the
/// planner has no body to describe. The self-report calls made this way go to no call log.
/// </summary>
public static class BiologySwitch
{
    public const string Name = "biology";

    private static readonly object Gate = new();
    private static ModelClient? _client;
    private static string? _modelsRoot;
    private static IReadOnlyList<Row>? _measured;
    private static ModelEndpoint? _endpoint;

    /// <summary>Registers <c>biology</c> in the <see cref="Creatura.Worlds.ExtensionCatalog"/>; the client and the measurements folder serve every seat bound after.</summary>
    public static void Register(ModelClient client, string modelsRoot)
    {
        lock (Gate)
        {
            _client = client;
            _modelsRoot = modelsRoot;
            _measured = null;
        }

        Creatura.Worlds.ExtensionCatalog.Register(Name, _ => Make());
    }

    /// <summary>The seat the next <c>biology</c> extension describes; null (the planner) unbinds.</summary>
    public static void Seat(ModelEndpoint? endpoint)
    {
        lock (Gate) _endpoint = endpoint;
    }

    public static bool Bound
    {
        get { lock (Gate) return _endpoint is not null && _client is not null; }
    }

    /// <summary>The extension for the bound seat, or an error naming what is missing.</summary>
    public static BiologyExtension Make()
    {
        ModelEndpoint endpoint;
        ModelClient client;
        IReadOnlyList<Row> measured;
        lock (Gate)
        {
            if (_client is null || _modelsRoot is null) throw new InvalidOperationException("the biology extension is not registered: call BiologySwitch.Register first");
            if (_endpoint is null) throw new InvalidOperationException("the biology extension needs a model in the seat; the planner has no body to describe (use --seat <model>, or --battery biology)");
            endpoint = _endpoint;
            client = _client;
            measured = _measured ??= Biology.ReadMeasurements(_modelsRoot);
        }

        return new BiologyExtension(Biology.For(endpoint, measured), endpoint, client, CallLog.None);
    }
}
