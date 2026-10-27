using System.Net;
using System.Text;
using System.Text.Json;
using Creatura.Seats;

namespace Creatura.Tests;

/// <summary>An HTTP handler for the seat tests: records every request, answers from a script of (status, body) pairs, the last one repeating.</summary>
internal sealed class SeatStubHandler : HttpMessageHandler
{
    private readonly List<(HttpStatusCode Status, string Body)> _replies;

    public SeatStubHandler(params (HttpStatusCode Status, string Body)[] replies) => _replies = replies.ToList();

    /// <summary>A handler that always answers 200 with an OpenAI-shaped reply whose content is <paramref name="content"/>.</summary>
    public static SeatStubHandler OpenAi(string content) => new((HttpStatusCode.OK, OpenAiReply(content)));

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public List<string?> Authorizations { get; } = [];

    public string? LastBody => Bodies.Count == 0 ? null : Bodies[^1];

    public static string OpenAiReply(string content, int promptTokens = 100, int completionTokens = 10) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content }, finish_reason = "stop" } },
        usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens },
    });

    public static string OpenAiToolCallReply(string name, string argumentsJson) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content = "", tool_calls = new[] { new { type = "function", function = new { name, arguments = argumentsJson } } } }, finish_reason = "tool_calls" } },
        usage = new { prompt_tokens = 80, completion_tokens = 12 },
    });

    public static string OllamaReply(string content) => JsonSerializer.Serialize(new
    {
        message = new { role = "assistant", content },
        done_reason = "stop",
        prompt_eval_count = 50,
        eval_count = 7,
        prompt_eval_duration = 200_000_000L,
        eval_duration = 700_000_000L,
        load_duration = 10_000_000L,
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        Authorizations.Add(request.Headers.Authorization?.ToString() ?? (request.Headers.TryGetValues("api-key", out var keys) ? "api-key " + string.Join("", keys) : null));
        var (status, body) = _replies[Math.Min(Requests.Count - 1, _replies.Count - 1)];
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

internal static class SeatFixtures
{
    public static ModelEndpoint Json(string name = "stub-json") => new(name, Provider.OpenAiCompatible, "https://stub.example", "stub", "STUB_KEY", NativeTools: false, Reasoning: false, 0.1m, 0.2m);

    public static ModelEndpoint Tools(string name = "stub-tools") => new(name, Provider.OpenAiCompatible, "https://stub.example", "stub", "STUB_KEY", NativeTools: true, Reasoning: false, 0.1m, 0.2m);

    public static ModelEndpoint Ollama(string name = "stub-ollama") => new(name, Provider.Ollama, "http://localhost:11434", "stub", null, NativeTools: false, Reasoning: false, 0m, 0m);

    public static ModelEndpoint Foundry(string name = "stub-foundry") => new(name, Provider.AzureFoundry, "https://foundry.example", "stub", "STUB_KEY", NativeTools: false, Reasoning: false, null, null);

    /// <summary>A client over the handler with the test key and no retry pause.</summary>
    public static ModelClient Client(HttpMessageHandler handler, string key = "test-key-value-0123456789") =>
        new(new HttpClient(handler), keys: e => e.KeyEnv is null ? null : key) { RetryDelay = TimeSpan.Zero };

    /// <summary>The restore domain with apply-config hidden by the caller, as the first seat's tests had it.</summary>
    public static PolicyContext Context(IReadOnlyList<string>? history = null, IReadOnlyList<string>? notes = null) => new()
    {
        Facts = new HashSet<string> { "permission.deploy" },
        Tools = RestoreServiceDomain.Tools().Where(t => t.Id != "apply-config").ToList(),
        Goal = RestoreServiceDomain.Goal,
        GoalText = RestoreServiceDomain.GoalText,
        Tick = 1,
        MaxTicks = 30,
        Freedom = 0.6,
        SelfModel = "role: operator for joche",
        Stage = "3 (social self)",
        Preferred = RestoreServiceDomain.Habit,
        History = history ?? ["inspect-logs → effect landed"],
        Notes = notes ?? [],
    };
}
