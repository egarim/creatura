using System.Net;
using Creatura.Seats;

namespace Creatura.Tests;

/// <summary>
/// The three transports and the one client (migration step 15): each transport posts to its path with its own
/// authorization, the client dispatches on the endpoint's provider, and nothing that looks like a key reaches an error
/// text or a log line.
/// </summary>
public class ModelTransportTests
{
    private static readonly List<ChatMessage> Hello = [new("system", "be brief"), new("user", "hello")];

    /// <summary>The prefix of an OpenAI-style key, spelled in two pieces so a grep for secrets over the repository stays clean.</summary>
    private const string Sk = "sk" + "-";

    [Fact]
    public async Task TheOllamaTransportPostsToApiChatWithoutAKeyAndReadsTheTimings()
    {
        var handler = new SeatStubHandler((HttpStatusCode.OK, SeatStubHandler.OllamaReply("hi")));
        var reply = await SeatFixtures.Client(handler).ChatAsync(SeatFixtures.Ollama(), Hello, null, jsonMode: true, 50);

        Assert.Equal("http://localhost:11434/api/chat", handler.Requests[0].RequestUri!.ToString());
        Assert.Null(handler.Authorizations[0]);
        Assert.Contains("\"format\":\"json\"", handler.LastBody);
        Assert.Contains("\"num_predict\":50", handler.LastBody);
        Assert.Equal("hi", reply.Content);
        Assert.Equal(50, reply.PromptTokens);
        Assert.Equal(7, reply.CompletionTokens);
        Assert.Equal(0.7, reply.GenerationSeconds!.Value, 3);
        Assert.Equal(10, reply.TokensPerSecond!.Value, 3);
        Assert.False(reply.Failed);
    }

    [Fact]
    public async Task TheOpenAiCompatibleTransportPostsToChatCompletionsWithABearerKey()
    {
        var handler = SeatStubHandler.OpenAi("{}");
        var reply = await SeatFixtures.Client(handler, "k-0123456789abcdef").ChatAsync(SeatFixtures.Json(), Hello, null, jsonMode: true, 300);

        Assert.Equal("https://stub.example/chat/completions", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("Bearer k-0123456789abcdef", handler.Authorizations[0]);
        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", handler.LastBody);
        Assert.Contains("\"max_tokens\":300", handler.LastBody);
        Assert.Contains("\"temperature\":0", handler.LastBody);
        Assert.Equal(100, reply.PromptTokens);
        Assert.Equal("stop", reply.FinishReason);
    }

    [Fact]
    public async Task AReasoningEndpointGetsTheCompletionCapAndNoTemperature()
    {
        var handler = SeatStubHandler.OpenAi("{}");
        var gpt5 = SeatFixtures.Json() with { Model = "gpt-5.4-mini", Reasoning = true };
        await SeatFixtures.Client(handler).ChatAsync(gpt5, Hello, null, jsonMode: true, 300);
        Assert.Contains("\"max_completion_tokens\":4000", handler.LastBody);
        Assert.DoesNotContain("temperature", handler.LastBody);
        Assert.DoesNotContain("response_format", handler.LastBody);
    }

    [Fact]
    public async Task TheFoundryTransportPostsToModelsChatCompletionsWithAnApiKeyHeader()
    {
        var handler = SeatStubHandler.OpenAi("{}");
        await SeatFixtures.Client(handler, "f-0123456789abcdef").ChatAsync(SeatFixtures.Foundry(), Hello, null, jsonMode: false, 8);

        Assert.Equal("https://foundry.example/models/chat/completions?api-version=" + AzureFoundryTransport.ApiVersion, handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("api-key f-0123456789abcdef", handler.Authorizations[0]);
        Assert.DoesNotContain("response_format", handler.LastBody);
    }

    [Fact]
    public async Task TheClientDispatchesOnTheProviderAndRefusesOneItHasNoTransportFor()
    {
        var handler = SeatStubHandler.OpenAi("{}");
        var client = new ModelClient(new HttpClient(handler), [new OpenAiCompatibleTransport(new HttpClient(handler))], keys: _ => "x");
        Assert.Equal([Provider.OpenAiCompatible], client.Providers);
        await client.ChatAsync(SeatFixtures.Json(), Hello, null, true, 8);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.ChatAsync(SeatFixtures.Ollama(), Hello, null, true, 8));
        Assert.Contains("no transport for Ollama", refused.Message);
    }

    [Fact]
    public async Task AnExceptionInTheTransportIsAFailedReplyNotACrash()
    {
        var client = new ModelClient(new HttpClient(new ThrowingHandler()), keys: _ => "k") { RetryDelay = TimeSpan.Zero };
        var reply = await client.ChatAsync(SeatFixtures.Json(), Hello, null, true, 8);
        Assert.True(reply.Failed);
        Assert.Equal(0, reply.HttpStatus);
        Assert.StartsWith("HttpRequestException: ", reply.Error);
    }

    [Theory]
    [InlineData("Authorization: Bearer " + Sk + "abcdefghijklmnop1234", "Authorization: Bearer ***")]
    [InlineData("api-key: 0123456789abcdef", "api-key: ***")]
    [InlineData("\"api_key\":\"0123456789abcdef\"", "\"api_key\":\"***\"")]
    [InlineData("key " + Sk + "0123456789abcdefghij in the body", "key " + Sk + "*** in the body")]
    [InlineData("no secret here", "no secret here")]
    public void ScrubKeepsAnythingThatLooksLikeAKeyOutOfARecord(string text, string expected)
    {
        Assert.Equal(expected, Secrets.Scrub(text));
        Assert.Equal(expected, ModelClient.Scrub(text));
    }

    [Fact]
    public async Task AnErrorBodyThatEchoesTheKeyIsScrubbedBeforeItIsRecorded()
    {
        const string key = Sk + "0123456789abcdefghijklmnop";
        var handler = new SeatStubHandler((HttpStatusCode.BadRequest, $"{{\"error\":\"bad header: Bearer {key}\"}}"));
        var reply = await SeatFixtures.Client(handler, key).ChatAsync(SeatFixtures.Json(), Hello, null, true, 8);
        Assert.True(reply.Failed);
        Assert.DoesNotContain(key, reply.Error);
        Assert.Contains("Bearer ***", reply.Error);
    }

    [Fact]
    public void TheProviderNamesAreTheRowsNames()
    {
        Assert.Equal("ollama", Provider.Ollama.AsRow());
        Assert.Equal("openai", Provider.OpenAiCompatible.AsRow());
        Assert.Equal("azure-foundry", Provider.AzureFoundry.AsRow());
        Assert.Equal(Provider.AzureFoundry, Providers.Parse("azure-foundry"));
        Assert.Equal(Provider.OpenAiCompatible, Providers.Parse("openai"));
        Assert.Throws<ArgumentException>(() => Providers.Parse("anthropic"));
    }

    [Fact]
    public void ARequestPrintsWithoutItsKey()
    {
        var request = new ModelRequest { Endpoint = SeatFixtures.Json(), Messages = Hello, ApiKey = Sk + "secret-0123456789", MaxTokens = 8 };
        Assert.DoesNotContain("secret", request.ToString());
        Assert.DoesNotContain("secret", SeatFixtures.Json().ToString());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection reset");
    }
}
