using System.Text.Json;
using System.Text.Json.Serialization;

namespace Creatura.Seats;

/// <summary>
/// One model call, as it is written to the per-run log: what went in, what came back, what it cost and how
/// long it took. The prompt and the thinking text are kept verbatim. This is the file to open when a number looks wrong.
/// </summary>
public sealed record CallRecord(
    DateTimeOffset At,
    string Model,
    string Provider,
    string Purpose,
    string Protocol,
    string Context,
    IReadOnlyList<ChatMessage> Prompt,
    IReadOnlyList<string> ToolsOffered,
    string Answer,
    string? Reasoning,
    IReadOnlyList<ToolCall> ToolCalls,
    long? PromptTokens,
    long? CompletionTokens,
    long? ReasoningTokens,
    long? CachedTokens,
    double WallSeconds,
    double? TimeToFirstTokenSeconds,
    double? PromptEvalSeconds,
    double? GenerationSeconds,
    double? LoadSeconds,
    double? TokensPerSecond,
    bool Valid,
    string? Note,
    string? Error)
{
    /// <summary>The record of one reply, from the seat that asked: the usage and timings copied over, the prompt as sent.</summary>
    public static CallRecord Of(ModelEndpoint endpoint, string purpose, string protocol, string context, IReadOnlyList<ChatMessage> prompt, IReadOnlyList<string> toolsOffered, ModelReply reply, bool valid, string? note) =>
        new(DateTimeOffset.UtcNow, endpoint.Name, endpoint.ProviderName, purpose, protocol, context, prompt, toolsOffered, reply.Content, reply.Reasoning, reply.ToolCalls,
            reply.PromptTokens, reply.CompletionTokens, reply.ReasoningTokens, reply.CachedTokens,
            reply.WallSeconds, reply.TimeToFirstTokenSeconds, reply.PromptEvalSeconds, reply.GenerationSeconds, reply.LoadSeconds, reply.TokensPerSecond,
            valid, note, reply.Error);
}

/// <summary>
/// Append-only JSON Lines writer, one file per run. Safe to share between the policy and a judge. Every line is passed
/// through <see cref="Secrets.Scrub"/> before it is written: the prompt is verbatim, but nothing that looks like a key
/// reaches the disk, whichever field it was echoed in. With a null path nothing is written and only the count moves.
/// </summary>
public sealed class CallLog : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();

    public CallLog(string? path)
    {
        if (path is null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: true);
        Path = path;
    }

    /// <summary>A log that keeps the count and writes nothing.</summary>
    public static CallLog None => new(null);

    public string? Path { get; }

    public int Count { get; private set; }

    public void Write(CallRecord record)
    {
        lock (_gate)
        {
            Count++;
            _writer?.WriteLine(Secrets.Scrub(JsonSerializer.Serialize(record, Json)));
            _writer?.Flush();
        }
    }

    public void Dispose() => _writer?.Dispose();
}

/// <summary>Running totals for one seat over one run: the numbers a scoreboard row is made of.</summary>
public sealed class SeatTelemetry
{
    public int Calls { get; private set; }
    public int Failed { get; private set; }
    public int Invalid { get; private set; }
    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }
    public long ReasoningTokens { get; private set; }
    public long CachedTokens { get; private set; }
    public double Seconds { get; private set; }
    public double GenerationSeconds { get; private set; }
    public bool TokensKnown { get; private set; } = true;
    private readonly List<double> _callSeconds = [];

    public double MedianCallSeconds => _callSeconds.Count == 0 ? 0 : _callSeconds.Order().ElementAt(_callSeconds.Count / 2);

    /// <summary>Completion tokens per second: over generation time where the provider reports it, else over wall time.</summary>
    public double? TokensPerSecond => !TokensKnown || CompletionTokens == 0 ? null : CompletionTokens / (GenerationSeconds > 0 ? GenerationSeconds : Seconds);

    public void Add(ModelReply reply, bool valid)
    {
        Calls++;
        if (reply.Failed) Failed++;
        if (!valid) Invalid++;
        if (reply.PromptTokens is null && !reply.Failed) TokensKnown = false;
        PromptTokens += reply.PromptTokens ?? 0;
        CompletionTokens += reply.CompletionTokens ?? 0;
        ReasoningTokens += reply.ReasoningTokens ?? 0;
        CachedTokens += reply.CachedTokens ?? 0;
        Seconds += reply.WallSeconds;
        GenerationSeconds += reply.GenerationSeconds ?? 0;
        _callSeconds.Add(reply.WallSeconds);
    }
}
