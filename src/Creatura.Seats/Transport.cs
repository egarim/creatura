using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Creatura.Seats;

/// <summary>
/// One message. <paramref name="Images"/> go to a model that can see (a screenshot, when a web persona is stuck): sent as
/// content parts on the OpenAI shape and as <c>images</c> on Ollama's, and never written to a call log, which keeps their
/// sizes in its note instead.
/// </summary>
public sealed record ChatMessage(string Role, string Content, string? ToolCallId = null, string? Name = null,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<ChatImage>? Images = null);

/// <summary>An image in a message: its media type (<c>image/png</c>, <c>image/jpeg</c>) and its bytes.</summary>
public sealed record ChatImage(string MediaType, byte[] Data)
{
    public string DataUrl => $"data:{MediaType};base64,{Convert.ToBase64String(Data)}";
}

/// <summary>A function the model may call. Parameters is a JSON schema object; empty means no arguments.</summary>
public sealed record ToolSpec(string Name, string Description, JsonObject Parameters);

public sealed record ToolCall(string Name, string ArgumentsJson);

/// <summary>Everything one call cost and returned. Fields a provider does not report stay null, never zero.</summary>
public sealed record ModelReply(
    string Content,
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
    string? FinishReason,
    int HttpStatus,
    string? Error)
{
    /// <summary>Completion tokens per second of generation: exact where the provider times generation, else over the wall.</summary>
    public double? TokensPerSecond =>
        CompletionTokens is null ? null
        : GenerationSeconds is > 0 ? CompletionTokens / GenerationSeconds
        : WallSeconds > 0 ? CompletionTokens / WallSeconds
        : null;

    public bool Failed => Error is not null;

    /// <summary>A failed call: no content, the status (0 when no response came) and the error, scrubbed of anything that looks like a key.</summary>
    public static ModelReply Failure(double wallSeconds, int status, string error) =>
        new("", null, [], null, null, null, null, wallSeconds, null, null, null, null, null, status, Secrets.Scrub(error));
}

/// <summary>The three request shapes a catalogued model is reached through. DeepSeek and Azure OpenAI v1 are endpoints of the OpenAI-compatible shape, not providers of their own.</summary>
public enum Provider
{
    /// <summary>Ollama's native chat API (<c>/api/chat</c>): exact timings, thinking text, no key.</summary>
    Ollama,

    /// <summary>The OpenAI-compatible chat API (<c>/chat/completions</c>): DeepSeek, Azure OpenAI v1, Ollama's own <c>/v1</c>, and any other server that speaks it.</summary>
    OpenAiCompatible,

    /// <summary>Azure AI Foundry's model-inference endpoint (<c>/models/chat/completions</c>), keyed by an <c>api-key</c> header.</summary>
    AzureFoundry,
}

/// <summary>The provider names as the scoreboard rows have always written them (<c>ollama</c>, <c>openai</c>, <c>azure-foundry</c>).</summary>
public static class Providers
{
    public static string AsRow(this Provider provider) => provider switch
    {
        Provider.Ollama => "ollama",
        Provider.OpenAiCompatible => "openai",
        Provider.AzureFoundry => "azure-foundry",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static Provider Parse(string name) => name.Trim().ToLowerInvariant() switch
    {
        "ollama" => Provider.Ollama,
        "openai" or "openai-compatible" or "openaicompatible" => Provider.OpenAiCompatible,
        "azure-foundry" or "azurefoundry" or "foundry" => Provider.AzureFoundry,
        _ => throw new ArgumentException($"unknown provider '{name}'; known: ollama, openai, azure-foundry"),
    };
}

/// <summary>
/// One call as a transport sees it. The key is resolved by the client from the endpoint's environment-variable name
/// and travels here for the one request; it is never written anywhere, and <see cref="ToString"/> leaves it out.
/// </summary>
public sealed class ModelRequest
{
    public required ModelEndpoint Endpoint { get; init; }

    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public IReadOnlyList<ToolSpec>? Tools { get; init; }

    public bool JsonMode { get; init; }

    public int MaxTokens { get; init; }

    /// <summary>The key for this call, or null for a keyless endpoint (Ollama). Never logged.</summary>
    public string? ApiKey { get; init; }

    public override string ToString() => $"{Endpoint.Name}: {Messages.Count} messages, {Tools?.Count ?? 0} tools, json {JsonMode}, max {MaxTokens} tokens";
}

/// <summary>
/// The provider seam: one request shape in, one reply out. A transport owns no retry rule and no key; the client
/// retries, the endpoint names the key. A transport that throws is a failed call, which the client records as such.
/// </summary>
public interface IModelTransport
{
    Provider Provider { get; }

    Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>What keeps a key out of every log line, error message and exception this project writes.</summary>
public static partial class Secrets
{
    /// <summary>Error bodies and exception messages can echo headers; anything that looks like a bearer token, an api-key value or an OpenAI-style key (<c>sk</c> + dash) is cut to <c>***</c>.</summary>
    public static string Scrub(string s) => string.IsNullOrEmpty(s) ? s : Pattern().Replace(s, "$1***");

    [GeneratedRegex(@"(?i)(bearer\s+|api[_-]?key[""']?\s*[\s:=]\s*[""']?|authorization[""']?\s*[:=]\s*[""']?|sk[\-])[A-Za-z0-9_\-\.]{8,}")]
    private static partial Regex Pattern();

    /// <summary>A response body for an error record: at most 300 characters, scrubbed.</summary>
    public static string Truncate(string s) => Scrub(s.Length <= 300 ? s : s[..300] + "…");
}
