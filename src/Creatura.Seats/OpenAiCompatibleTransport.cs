using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Creatura.Seats;

/// <summary>
/// The OpenAI-compatible chat API: <c>POST {base}/chat/completions</c> with a bearer key. DeepSeek, Azure OpenAI's v1
/// endpoint and Ollama's <c>/v1</c> are endpoints of this one shape. Reasoning models get <c>max_completion_tokens</c>
/// and no temperature; the reasoner's <c>reasoning_content</c> is kept.
/// </summary>
public sealed class OpenAiCompatibleTransport : IModelTransport
{
    private readonly HttpClient _http;

    public OpenAiCompatibleTransport(HttpClient http) => _http = http;

    public Provider Provider => Provider.OpenAiCompatible;

    public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
        OpenAiStyle.CompleteAsync(_http, request, request.Endpoint.BaseUrl.TrimEnd('/') + "/chat/completions", r =>
        {
            if (request.ApiKey is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        }, cancellationToken);
}

/// <summary>Azure AI Foundry's model-inference endpoint: <c>POST {base}/models/chat/completions</c>, keyed by an <c>api-key</c> header, the same body and reply as the OpenAI shape.</summary>
public sealed class AzureFoundryTransport : IModelTransport
{
    public const string ApiVersion = "2024-05-01-preview";

    private readonly HttpClient _http;

    public AzureFoundryTransport(HttpClient http) => _http = http;

    public Provider Provider => Provider.AzureFoundry;

    public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
        OpenAiStyle.CompleteAsync(_http, request, request.Endpoint.BaseUrl.TrimEnd('/') + "/models/chat/completions?api-version=" + ApiVersion, r =>
        {
            if (request.ApiKey is not null) r.Headers.Add("api-key", request.ApiKey);
        }, cancellationToken);
}

/// <summary>The body and the reply the OpenAI-compatible and the Foundry shapes share.</summary>
internal static class OpenAiStyle
{
    public static async Task<ModelReply> CompleteAsync(HttpClient http, ModelRequest request, string url, Action<HttpRequestMessage> authorize, CancellationToken ct)
    {
        var e = request.Endpoint;
        var body = new JsonObject
        {
            ["model"] = e.Model,
            ["messages"] = new JsonArray(request.Messages.Select(m =>
            {
                var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
                if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
                if (m.Name is not null) o["name"] = m.Name;
                return (JsonNode)o;
            }).ToArray()),
        };
        if (e.OpenAiReasoningStyle)
        {
            // Reasoning models: the cap covers thinking and answer together, and temperature is fixed.
            body["max_completion_tokens"] = Math.Max(request.MaxTokens, 4000);
        }
        else
        {
            body["temperature"] = 0;
            // A model that thinks before it answers spends the cap on thinking first; give it room or the answer is empty.
            body["max_tokens"] = e.Reasoning ? Math.Max(request.MaxTokens, 4000) : request.MaxTokens;
        }

        if (request.JsonMode && !e.Reasoning) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (request.Tools is { Count: > 0 } && e.NativeTools) body["tools"] = ToolsJson.Array(request.Tools);

        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        authorize(message);

        var sw = Stopwatch.StartNew();
        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        sw.Stop();
        if (!response.IsSuccessStatusCode) return ModelReply.Failure(sw.Elapsed.TotalSeconds, (int)response.StatusCode, Secrets.Truncate(payload));

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var reply = choice.GetProperty("message");
        var content = reply.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        var reasoning = reply.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString()
            : reply.TryGetProperty("reasoning", out var r2) && r2.ValueKind == JsonValueKind.String ? r2.GetString() : null;
        var calls = new List<ToolCall>();
        if (reply.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in tc.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                calls.Add(new ToolCall(fn.GetProperty("name").GetString() ?? "", fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}"));
            }
        }

        long? prompt = null, completion = null, reasoningTokens = null, cached = null;
        double? ttft = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            prompt = ToolsJson.Long(usage, "prompt_tokens");
            completion = ToolsJson.Long(usage, "completion_tokens");
            if (usage.TryGetProperty("completion_tokens_details", out var cd) && cd.ValueKind == JsonValueKind.Object) reasoningTokens = ToolsJson.Long(cd, "reasoning_tokens");
            if (usage.TryGetProperty("prompt_tokens_details", out var pd) && pd.ValueKind == JsonValueKind.Object) cached = ToolsJson.Long(pd, "cached_tokens");
            cached ??= ToolsJson.Long(usage, "prompt_cache_hit_tokens");
            // Azure OpenAI reports latency checkpoints; user-visible time to first token is the one that matters here.
            if (usage.TryGetProperty("latency_checkpoint", out var lc) && lc.ValueKind == JsonValueKind.Object && ToolsJson.Long(lc, "user_visible_ttft_ms") is { } ms) ttft = ms / 1000.0;
        }

        return new ModelReply(content, reasoning, calls, prompt, completion, reasoningTokens, cached,
            sw.Elapsed.TotalSeconds, ttft, null, null, null,
            choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null,
            (int)response.StatusCode, null);
    }
}
