using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Creatura.Seats;

/// <summary>Ollama's native chat API: <c>POST {base}/api/chat</c>, non-streaming, exact timings in nanoseconds, thinking text, native tools where the model has them.</summary>
public sealed class OllamaTransport : IModelTransport
{
    private readonly HttpClient _http;

    public OllamaTransport(HttpClient http) => _http = http;

    public Provider Provider => Provider.Ollama;

    public async Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var e = request.Endpoint;
        var body = new JsonObject
        {
            ["model"] = e.Model,
            ["stream"] = false,
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_predict"] = request.MaxTokens },
            ["messages"] = new JsonArray(request.Messages.Select(m =>
            {
                var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
                if (m.Images is { Count: > 0 } images) o["images"] = new JsonArray(images.Select(i => (JsonNode)Convert.ToBase64String(i.Data)).ToArray());
                if (m.Name is not null) o["tool_name"] = m.Name;
                return (JsonNode)o;
            }).ToArray()),
        };
        if (request.JsonMode) body["format"] = "json";
        if (request.Tools is { Count: > 0 } && e.NativeTools) body["tools"] = ToolsJson.Array(request.Tools);

        var sw = Stopwatch.StartNew();
        using var response = await _http.PostAsJsonAsync(e.BaseUrl.TrimEnd('/') + "/api/chat", body, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (!response.IsSuccessStatusCode) return ModelReply.Failure(sw.Elapsed.TotalSeconds, (int)response.StatusCode, Secrets.Truncate(payload));

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var message = root.GetProperty("message");
        var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var thinking = message.TryGetProperty("thinking", out var th) ? th.GetString() : null;
        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in tc.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                calls.Add(new ToolCall(fn.GetProperty("name").GetString() ?? "", fn.TryGetProperty("arguments", out var a) ? a.GetRawText() : "{}"));
            }
        }

        static double? Seconds(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.TryGetInt64(out var ns) ? ns / 1e9 : null;
        static long? Count(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;

        return new ModelReply(
            content, thinking, calls,
            Count(root, "prompt_eval_count"), Count(root, "eval_count"), null, null,
            sw.Elapsed.TotalSeconds, null,
            Seconds(root, "prompt_eval_duration"), Seconds(root, "eval_duration"), Seconds(root, "load_duration"),
            root.TryGetProperty("done_reason", out var dr) ? dr.GetString() : null,
            (int)response.StatusCode, null);
    }
}

/// <summary>The tools array in the OpenAI function shape, which Ollama's native API shares.</summary>
internal static class ToolsJson
{
    public static JsonArray Array(IReadOnlyList<ToolSpec> tools) =>
        new(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.Parameters.DeepClone(),
            },
        }).ToArray());

    public static long? Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;
}
