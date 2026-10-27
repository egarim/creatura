namespace Creatura.Seats;

/// <summary>What the provider publishes about a model: its window, the window it is run with here, its output cap, its knowledge cutoff, a caveat. Unknown is null, never zero.</summary>
public sealed record ModelSheet(int? ContextWindow, int? ContextAsRun, int? MaxOutput, string? KnowledgeCutoff, string? Caveat)
{
    public static readonly ModelSheet Unknown = new(null, null, null, null, null);
}

/// <summary>
/// Where a model lives and what it can do. The key is named by environment variable (several names joined with
/// <c>|</c>, the first set one wins) and read at call time; it is never held here, so an endpoint can be printed.
/// </summary>
public sealed record ModelEndpoint(
    string Name,
    Provider Provider,
    string BaseUrl,
    string Model,
    string? KeyEnv,
    bool NativeTools,
    bool Reasoning,
    decimal? InputPerMillion,
    decimal? OutputPerMillion)
{
    /// <summary>The published sheet, when the catalog has one.</summary>
    public ModelSheet? Sheet { get; init; }

    /// <summary>The provider as the rows write it.</summary>
    public string ProviderName => Provider.AsRow();

    /// <summary>Reasoning models on the OpenAI side take max_completion_tokens and no temperature.</summary>
    public bool OpenAiReasoningStyle => Provider == Provider.OpenAiCompatible && Model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);

    public bool IsLocal => Provider == Provider.Ollama;

    /// <summary>The seat protocol this endpoint is driven with: <c>tools</c> (one function call) or <c>json</c> (one object).</summary>
    public string Protocol => NativeTools ? "tools" : "json";

    /// <summary>The key from the first set variable named in <see cref="KeyEnv"/>, or null. Never log the result.</summary>
    public string? ApiKey()
    {
        if (KeyEnv is null) return null;
        foreach (var name in KeyEnv.Split('|'))
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    public decimal? Cost(long promptTokens, long completionTokens) =>
        InputPerMillion is null || OutputPerMillion is null
            ? null
            : promptTokens * InputPerMillion.Value / 1_000_000m + completionTokens * OutputPerMillion.Value / 1_000_000m;
}
