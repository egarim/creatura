using Creatura;
using Microsoft.Extensions.AI;

namespace Creatura.Hosts.AgentFramework.Tests;

/// <summary>
/// An <see cref="IChatClient"/> in the reference loop's seat: the loop owns the ticks, the client answers one question
/// per tick from the visible tools, and the control layer around it is the one every design gets.
/// </summary>
public class ChatClientPolicyTests
{
    private static World PatchLies()
    {
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("apply-patch");
        return world;
    }

    [Fact]
    public async Task ThePlannerBehindAChatClientTakesTheSameActionsAsThePlannerPolicy()
    {
        var reference = await new ReferenceLoop(new SelfModelRegulator(Identity.Bach), new PlannerPolicy(), name: "Bach").RunAsync(PatchLies(), 30);

        var world = PatchLies();
        var seat = new ChatClientPolicy(new PlannerChatClient(world, RestoreServiceDomain.Task), "bfs-chat");
        var run = await new ReferenceLoop(new SelfModelRegulator(Identity.Bach), seat, name: "Bach").RunAsync(world, 30);

        Assert.True(run.GoalReached);
        Assert.Equal(reference.Ticks, run.Ticks);
        Assert.Equal(reference.Trace.Select(e => (e.Tick, e.Action, e.Kind)), run.Trace.Select(e => (e.Tick, e.Action, e.Kind)));
    }

    [Fact]
    public async Task AHiddenToolNamedIsRefusedNotObeyed()
    {
        var seat = new ChatClientPolicy(new Replying(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "deploy")])));

        var suggestion = await seat.NextAsync(Context([Tool("check-logs")]));

        Assert.Null(suggestion.Tool);
        Assert.Equal("refused: deploy is not a visible tool", suggestion.Rationale);
    }

    [Fact]
    public async Task AReplyWithNoCallIsNoActionWithItsFirstLineAsTheRationale()
    {
        var seat = new ChatClientPolicy(new Replying(new ChatMessage(ChatRole.Assistant, "NO PATH: nothing offered helps.\nmore words")));

        var suggestion = await seat.NextAsync(Context([Tool("check-logs")]));

        Assert.Null(suggestion.Tool);
        Assert.Equal("NO PATH: nothing offered helps.", suggestion.Rationale);
    }

    [Fact]
    public async Task OnlyTheVisibleToolsAreDeclaredAndAPayloadComesBackAsContent()
    {
        var payload = new Tool("write-fix", [], ["fix.written"], "write") { NeedsPayload = true };
        var client = new Replying(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("c1", "write-fix", new Dictionary<string, object?> { ["content"] = "return 42;" })]));

        var suggestion = await new ChatClientPolicy(client).NextAsync(Context([Tool("check-logs"), payload]));

        Assert.Equal(["check-logs", "write-fix"], client.Offered);
        Assert.Equal("write-fix", suggestion.Tool);
        Assert.Equal("return 42;", suggestion.Content);
    }

    private static Tool Tool(string id) => new(id, [], [id + ".done"], "read");

    private static PolicyContext Context(IReadOnlyList<Tool> tools) => new()
    {
        Facts = new HashSet<string>(),
        Tools = tools,
        Goal = _ => false,
        GoalText = "the service is healthy",
        Tick = 1,
        MaxTicks = 30,
        Freedom = 0.5,
    };

    /// <summary>Answers every request with one fixed message and remembers which tools it was offered.</summary>
    private sealed class Replying(ChatMessage reply) : IChatClient
    {
        public List<string> Offered { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Offered.AddRange((options?.Tools ?? []).Select(t => t.Name));
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
