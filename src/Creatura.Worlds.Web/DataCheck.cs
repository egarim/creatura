using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Creatura.Worlds.Web;

/// <summary>
/// The honest success check, declared: one HTTP request to the app's own API and one assertion on the JSON it
/// answers. The assertion is read at <see cref="Path"/> (dot-separated; a number is an array index; empty is the
/// whole answer) and is one of: <see cref="Exists"/>, <see cref="Is"/> (<c>"equals"</c> in the file), <see cref="Contains"/>
/// (an array holds an element whose fields include these, or a string holds this text), <see cref="Count"/> or
/// <see cref="MinCount"/> (an array's length). The page and the model are never asked.
/// </summary>
public sealed record DataCheck : HttpCall
{
    public string Path { get; init; } = "";

    public bool? Exists { get; init; }

    [JsonPropertyName("equals")]
    public JsonNode? Is { get; init; }

    public JsonNode? Contains { get; init; }

    public int? Count { get; init; }

    public int? MinCount { get; init; }

    /// <summary>Text is compared without case: in <see cref="Is"/>, in <see cref="Contains"/> and in the fields it names.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>What is wrong with the check as written, or null.</summary>
    public string? Problem()
    {
        var n = (Exists is null ? 0 : 1) + (Is is null ? 0 : 1) + (Contains is null ? 0 : 1) + (Count is null ? 0 : 1) + (MinCount is null ? 0 : 1);
        if (string.IsNullOrWhiteSpace(Url)) return "has no url";
        return n == 1 ? null : $"needs exactly one assertion (exists, equals, contains, count, minCount); it has {n}";
    }

    /// <summary>The assertion on an answer already read: whether it holds, and one line saying what was seen.</summary>
    public CheckResult Evaluate(JsonNode? root)
    {
        var (found, node) = At(root, Path);
        var where = Path.Length == 0 ? "the answer" : Path;
        if (Exists is { } exists)
        {
            var present = found && node is not null;
            return new(present == exists, present ? $"{where} is present" : $"{where} is absent");
        }

        if (!found) return new(false, $"{where} is absent");
        if (Is is { } expected)
        {
            var same = Same(node, expected, IgnoreCase);
            return new(same, same ? $"{where} = {Show(expected)}" : $"{where} = {Show(node)}, not {Show(expected)}");
        }

        if (Count is { } count || MinCount is not null)
        {
            if (node is not JsonArray array) return new(false, $"{where} is not a list");
            return Count is { } c
                ? new(array.Count == c, string.Create(CultureInfo.InvariantCulture, $"{where} has {array.Count}, wanted {c}"))
                : new(array.Count >= MinCount!.Value, string.Create(CultureInfo.InvariantCulture, $"{where} has {array.Count}, wanted at least {MinCount}"));
        }

        var wanted = Contains!;
        switch (node)
        {
            case JsonArray items:
                var hit = items.Any(item => Includes(item, wanted, IgnoreCase));
                return new(hit, hit ? $"{where} holds {Show(wanted)}" : string.Create(CultureInfo.InvariantCulture, $"{where} ({items.Count} items) holds no {Show(wanted)}"));
            case JsonValue v when v.GetValueKind() == JsonValueKind.String && wanted.GetValueKind() == JsonValueKind.String:
                var text = v.GetValue<string>().Contains(wanted.GetValue<string>(), IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                return new(text, text ? $"{where} holds {Show(wanted)}" : $"{where} = {Show(node)}, without {Show(wanted)}");
            default:
                return new(false, $"{where} is neither a list nor text");
        }
    }

    /// <summary>An item includes the wanted value: every field of a wanted object is in the item with the same value; anything else is equal.</summary>
    public static bool Includes(JsonNode? item, JsonNode? wanted, bool ignoreCase = false)
    {
        if (wanted is JsonObject fields)
        {
            return item is JsonObject obj && fields.All(f => obj.TryGetPropertyValue(f.Key, out var v) && Includes(v, f.Value, ignoreCase));
        }

        return Same(item, wanted, ignoreCase);
    }

    private static bool Same(JsonNode? a, JsonNode? b, bool ignoreCase) =>
        ignoreCase && a is JsonValue x && b is JsonValue y && x.GetValueKind() == JsonValueKind.String && y.GetValueKind() == JsonValueKind.String
            ? string.Equals(x.GetValue<string>(), y.GetValue<string>(), StringComparison.OrdinalIgnoreCase)
            : JsonNode.DeepEquals(a, b);

    /// <summary>The node at a dot path: (false, null) when a step is missing.</summary>
    public static (bool Found, JsonNode? Node) At(JsonNode? root, string path)
    {
        var node = root;
        if (string.IsNullOrEmpty(path)) return (true, node);
        foreach (var step in path.Split('.'))
        {
            switch (node)
            {
                case JsonObject obj when obj.TryGetPropertyValue(step, out var next):
                    node = next;
                    break;
                case JsonArray array when int.TryParse(step, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < array.Count:
                    node = array[i];
                    break;
                default:
                    return (false, null);
            }
        }

        return (true, node);
    }

    private static string Show(JsonNode? node)
    {
        var s = node?.ToJsonString() ?? "null";
        return s.Length <= 80 ? s : s[..80] + "…";
    }

    /// <summary>Makes the request with <paramref name="http"/> (resolved against the adapter's base url) and evaluates the answer. A failed request is a check that does not hold, with the reason.</summary>
    public async Task<CheckResult> RunAsync(HttpClient http, UxAdapter adapter, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.SendAsync(http, adapter, this, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, string.Create(CultureInfo.InvariantCulture, $"{Method} {Url} answered {(int)response.StatusCode}"));
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return new(false, $"{Method} {Url} did not answer JSON");
            }

            return Evaluate(root);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(false, $"{Method} {Url} failed: {e.Message}");
        }
    }
}

/// <summary>Whether the check holds, and what was seen.</summary>
public sealed record CheckResult(bool Done, string Detail);

/// <summary>The harness's own requests: the check, the reset. Never through the browser, so never through a barrier.</summary>
public static class Http
{
    public static Task<HttpResponseMessage> SendAsync(HttpClient http, UxAdapter adapter, HttpCall call, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(new HttpMethod(call.Method), adapter.Resolve(call.Url));
        foreach (var (name, value) in call.Headers) request.Headers.TryAddWithoutValidation(name, Environment(value));
        if (call.Body is not null)
        {
            request.Content = new StringContent(call.Body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return http.SendAsync(request, cancellationToken);
    }

    /// <summary>A header value of the form <c>env:NAME</c> is read from the environment at call time, so a token is never in the file.</summary>
    public static string Environment(string value) =>
        value.StartsWith("env:", StringComparison.Ordinal) ? System.Environment.GetEnvironmentVariable(value[4..]) ?? "" : value;
}
