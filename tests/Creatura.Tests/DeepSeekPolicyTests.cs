using System.Net;
using System.Text;
using System.Text.Json;
using Creatura;
using Creatura.Seats;

#pragma warning disable CS0618 // the shim is obsolete on purpose: these are the first seat's tests, kept as they were

namespace Creatura.Tests;

/// <summary>
/// The DeepSeek policy, tested without the network: a stub handler captures the request and returns a
/// canned reply, so the prompt construction, the JSON parsing and the visibility check are all covered
/// offline. The live behaviour is demonstrated by the research repository's <c>runs/deepseek-chat-full-suite.txt</c>.
/// Moved from the research repository at migration step 15, when <see cref="DeepSeekPolicy"/> became a shim over
/// <see cref="ModelPolicy"/>; the one wording change is "unavailable action" for "unavailable tool".
/// </summary>
public class DeepSeekPolicyTests
{
    private sealed class StubHandler(string reply) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = reply } } },
                usage = new { prompt_tokens = 100, completion_tokens = 10 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static PolicyContext Context() => new()
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
        History = ["inspect-logs → effect landed"],
    };

    [Fact]
    public async Task ThePromptCarriesTheFactsTheToolsAndTheRecord()
    {
        var handler = new StubHandler("""{"tool":"inspect-logs","why":"start here"}""");
        var policy = new DeepSeekPolicy(new DeepSeekClient("test-key", "deepseek-chat", new HttpClient(handler)));

        var suggestion = await policy.NextAsync(Context());

        Assert.Equal("inspect-logs", suggestion.Tool);
        Assert.Equal("start here", suggestion.Rationale);
        var body = handler.LastBody!;
        Assert.Contains("permission.deploy", body);
        Assert.Contains("inspect-logs", body);
        Assert.Contains("requires", body);
        Assert.Contains("role: operator for joche", body);
        Assert.Contains("3 (social self)", body);
    }

    [Fact]
    public async Task TheToolsTheAgentHidesAreNotInThePrompt()
    {
        var handler = new StubHandler("""{"tool":"inspect-logs","why":"ok"}""");
        var policy = new DeepSeekPolicy(new DeepSeekClient("test-key", "deepseek-chat", new HttpClient(handler)));

        await policy.NextAsync(Context());

        // apply-config was quarantined by the caller, so the model must never be offered it.
        Assert.DoesNotContain("apply-config", handler.LastBody);
    }

    [Fact]
    public async Task AToolOutsideTheVisibleSetIsRefusedNotObeyed()
    {
        var handler = new StubHandler("""{"tool":"apply-config","why":"try the alternative"}""");
        var policy = new DeepSeekPolicy(new DeepSeekClient("test-key", "deepseek-chat", new HttpClient(handler)));

        var suggestion = await policy.NextAsync(Context());

        Assert.Null(suggestion.Tool);
        Assert.Contains("unavailable action", suggestion.Rationale);
        Assert.Contains(policy.Log, line => line.Contains("refused"));
    }

    [Fact]
    public async Task ANullToolFromTheModelMeansNoPath()
    {
        var handler = new StubHandler("""{"tool":null,"why":"no tool grants deployed"}""");
        var policy = new DeepSeekPolicy(new DeepSeekClient("test-key", "deepseek-chat", new HttpClient(handler)));

        var suggestion = await policy.NextAsync(Context());

        Assert.Null(suggestion.Tool);
        Assert.Equal("no tool grants deployed", suggestion.Rationale);
    }

    [Fact]
    public async Task UnparseableOutputDegradesToNoActionInsteadOfCrashing()
    {
        var handler = new StubHandler("I think you should inspect the logs.");
        var policy = new DeepSeekPolicy(new DeepSeekClient("test-key", "deepseek-chat", new HttpClient(handler)));

        var suggestion = await policy.NextAsync(Context());

        Assert.Null(suggestion.Tool);
        Assert.Contains("unparseable", suggestion.Rationale);
    }

    [Fact]
    public void TheClientRefusesToStartWithoutAKey()
    {
        Assert.Throws<ArgumentException>(() => new DeepSeekClient(" "));
    }
}
