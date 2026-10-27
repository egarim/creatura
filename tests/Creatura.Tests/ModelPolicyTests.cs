using System.Net;
using Creatura.Seats;

namespace Creatura.Tests;

/// <summary>
/// The model seat without the network (migration step 15): a stub handler captures what the seat sends and answers
/// from a script. The prompt carries the facts, the visible tools and the record; a hidden tool is absent from it and
/// refused when named; no action is a valid answer; a failed call is retried once and then surfaces as a model-error
/// suggestion; the budget trims the prompt in the documented order.
/// </summary>
public class ModelPolicyTests
{
    [Fact]
    public async Task ThePromptCarriesTheFactsTheVisibleToolsAndTheRecord()
    {
        var handler = SeatStubHandler.OpenAi("""{"tool":"inspect-logs","why":"start here"}""");
        var policy = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(handler));

        var suggestion = await policy.NextAsync(SeatFixtures.Context(notes: ["model stub via openai (cloud)"]));

        Assert.Equal("inspect-logs", suggestion.Tool);
        Assert.Equal("start here", suggestion.Rationale);
        var body = handler.LastBody!;
        Assert.Contains("permission.deploy", body);
        Assert.Contains("inspect-logs", body);
        Assert.Contains("requires", body);
        Assert.Contains("role: operator for joche", body);
        Assert.Contains("3 (social self)", body);
        Assert.Contains("NOTES FROM THE ARCHITECTURE", body);
        Assert.Contains("model stub via openai (cloud)", body);
        Assert.Contains("inspect-logs \\u2192 effect landed", body);
        Assert.Equal(1, policy.Telemetry.Calls);
        Assert.Equal(0, policy.Telemetry.Invalid);
        Assert.Equal(100, policy.Telemetry.PromptTokens);
        Assert.Equal("json", policy.Protocol);
    }

    [Fact]
    public async Task TheToolsTheAgentHidesAreNotInThePromptNorInTheFunctions()
    {
        var json = SeatStubHandler.OpenAi("""{"tool":"inspect-logs","why":"ok"}""");
        await new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(json)).NextAsync(SeatFixtures.Context());
        Assert.DoesNotContain("apply-config", json.LastBody);

        var tools = new SeatStubHandler((HttpStatusCode.OK, SeatStubHandler.OpenAiToolCallReply("inspect-logs", """{"why":"ok"}""")));
        var policy = new ModelPolicy(SeatFixtures.Tools(), SeatFixtures.Client(tools));
        var suggestion = await policy.NextAsync(SeatFixtures.Context());
        Assert.Equal("inspect-logs", suggestion.Tool);
        Assert.DoesNotContain("apply-config", tools.LastBody);
        Assert.Contains("\"name\":\"no_action\"", tools.LastBody);
        Assert.Equal("tools", policy.Protocol);
    }

    [Fact]
    public async Task AnAnswerNamingAHiddenToolIsRefusedNotObeyed()
    {
        var json = SeatStubHandler.OpenAi("""{"tool":"apply-config","why":"try the alternative"}""");
        var policy = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(json));
        var suggestion = await policy.NextAsync(SeatFixtures.Context());
        Assert.Null(suggestion.Tool);
        Assert.StartsWith("proposed unavailable action 'apply-config'", suggestion.Rationale);
        Assert.Equal(1, policy.Telemetry.Invalid);

        var tools = new SeatStubHandler((HttpStatusCode.OK, SeatStubHandler.OpenAiToolCallReply("apply-config", """{"why":"try it"}""")));
        var called = new ModelPolicy(SeatFixtures.Tools(), SeatFixtures.Client(tools));
        var callSuggestion = await called.NextAsync(SeatFixtures.Context());
        Assert.Null(callSuggestion.Tool);
        Assert.StartsWith("proposed unavailable action 'apply-config'", callSuggestion.Rationale);
        Assert.Equal(1, called.Telemetry.Invalid);
    }

    [Fact]
    public async Task NoActionIsAValidAnswerInBothProtocols()
    {
        var json = SeatStubHandler.OpenAi("""{"tool":null,"why":"no tool grants deployed"}""");
        var policy = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(json));
        var suggestion = await policy.NextAsync(SeatFixtures.Context());
        Assert.Null(suggestion.Tool);
        Assert.Equal("no tool grants deployed", suggestion.Rationale);
        Assert.Equal(0, policy.Telemetry.Invalid);

        var tools = new SeatStubHandler((HttpStatusCode.OK, SeatStubHandler.OpenAiToolCallReply(ModelPolicy.NoAction, """{"why":"nothing can move"}""")));
        var called = new ModelPolicy(SeatFixtures.Tools(), SeatFixtures.Client(tools));
        var callSuggestion = await called.NextAsync(SeatFixtures.Context());
        Assert.Null(callSuggestion.Tool);
        Assert.Equal("nothing can move", callSuggestion.Rationale);
        Assert.Equal(0, called.Telemetry.Invalid);
    }

    [Fact]
    public async Task UnparseableOutputDegradesToNoActionAndCountsAsInvalid()
    {
        var handler = SeatStubHandler.OpenAi("I think you should inspect the logs.");
        var policy = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(handler));
        var suggestion = await policy.NextAsync(SeatFixtures.Context());
        Assert.Null(suggestion.Tool);
        Assert.StartsWith("unparseable answer", suggestion.Rationale);
        Assert.Equal(1, policy.Telemetry.Invalid);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task AFailedCallIsRetriedOnceThenSurfacesAsAModelError(HttpStatusCode status)
    {
        var handler = new SeatStubHandler((status, """{"error":"no"}"""));
        var policy = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(handler));

        var suggestion = await policy.NextAsync(SeatFixtures.Context());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(suggestion.Tool);
        Assert.StartsWith(TraceText.ModelError + ": ", suggestion.Rationale);
        Assert.Equal(1, policy.Telemetry.Failed);
        Assert.Equal(1, policy.Telemetry.Invalid);
    }

    [Fact]
    public async Task ARetryThatSucceedsIsOneDecision()
    {
        var handler = new SeatStubHandler((HttpStatusCode.ServiceUnavailable, "busy"), (HttpStatusCode.OK, SeatStubHandler.OpenAiReply("""{"tool":"inspect-logs","why":"now"}""")));
        var policy = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(handler));
        var suggestion = await policy.NextAsync(SeatFixtures.Context());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("inspect-logs", suggestion.Tool);
        Assert.Equal(0, policy.Telemetry.Failed);
    }

    [Fact]
    public async Task ABadRequestIsNotRetried()
    {
        var handler = new SeatStubHandler((HttpStatusCode.BadRequest, "malformed"));
        var policy = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(handler));
        var suggestion = await policy.NextAsync(SeatFixtures.Context());
        Assert.Single(handler.Requests);
        Assert.StartsWith(TraceText.ModelError, suggestion.Rationale);
    }

    [Fact]
    public async Task AMissingKeyIsAFailureNamedByTheVariableNeverByAValue()
    {
        var handler = SeatStubHandler.OpenAi("{}");
        var client = new ModelClient(new HttpClient(handler), keys: _ => null) { RetryDelay = TimeSpan.Zero };
        var suggestion = await new ModelPolicy(SeatFixtures.Json(), client).NextAsync(SeatFixtures.Context());
        Assert.Empty(handler.Requests);
        Assert.Equal($"{TraceText.ModelError}: no key in STUB_KEY", suggestion.Rationale);
    }

    [Fact]
    public async Task ThePromptBudgetTrimsHistoryFirstAndCountsTheTrim()
    {
        var history = Enumerable.Range(1, 40).Select(i => $"step {i}: inspect-logs → effect landed").ToList();
        var full = SeatStubHandler.OpenAi("""{"tool":null,"why":"ok"}""");
        var unbudgeted = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(full));
        await unbudgeted.NextAsync(SeatFixtures.Context(history));
        Assert.Contains("HISTORY (this run, oldest first)", full.LastBody);
        Assert.Equal(0, unbudgeted.PromptsTrimmed);

        var trimmed = SeatStubHandler.OpenAi("""{"tool":null,"why":"ok"}""");
        var budgeted = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(trimmed)) { PromptBudgetTokens = 700 };
        await budgeted.NextAsync(SeatFixtures.Context(history));
        Assert.Contains("HISTORY (this run, last 3 of 40)", trimmed.LastBody);
        Assert.Contains("permission.deploy", trimmed.LastBody);
        Assert.Contains("inspect-logs: requires", trimmed.LastBody);
        Assert.Equal(1, budgeted.PromptsTrimmed);
    }

    [Fact]
    public async Task AToolThatNeedsAPayloadIsRefusedWithoutOne()
    {
        var tools = RestoreServiceDomain.Tools().Select(t => t.Id == "apply-patch" ? t with { NeedsPayload = true, PayloadHint = "the patch" } : t).ToList();
        var context = SeatFixtures.Context() with { Tools = tools, Facts = new HashSet<string> { "component.identified" } };
        var handler = SeatStubHandler.OpenAi("""{"tool":"apply-patch","why":"fix it"}""");
        var policy = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(handler));
        var suggestion = await policy.NextAsync(context);
        Assert.Null(suggestion.Tool);
        Assert.Equal("apply-patch needs a payload and none was supplied", suggestion.Rationale);
        Assert.Contains("NEEDS PAYLOAD: the patch", handler.LastBody);
    }
}
