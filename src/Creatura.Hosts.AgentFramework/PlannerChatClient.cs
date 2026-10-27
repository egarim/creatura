using System.Runtime.CompilerServices;
using Creatura;
using Microsoft.Extensions.AI;

namespace Creatura.Hosts.AgentFramework;

/// <summary>
/// An offline stand-in for the model: breadth-first search over the tools the request actually offers.
/// It exists so the control layer can be tested without a key, and so the same run can be compared with
/// a real model in the same seat. It sees exactly what the model would see — only the tools in the
/// request — which is what makes the quarantine test honest.
/// </summary>
public sealed class PlannerChatClient(IWorld world, AgentTask task) : IChatClient
{
    private int _calls;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var offered = (options?.Tools ?? []).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        // BFS has no generator, so tools that need a payload are out of its reach (same rule as PlannerPolicy).
        var tools = world.AllTools.Where(t => offered.Contains(t.Id) && !t.NeedsPayload).ToList();
        var plan = Planner.Find(world.Facts, tools, task.Goal, 8, task.Habit);

        ChatMessage reply;
        if (plan is null)
        {
            reply = new ChatMessage(ChatRole.Assistant, "NO PATH: no offered tool can make progress toward the goal.");
        }
        else if (plan.Count == 0)
        {
            reply = new ChatMessage(ChatRole.Assistant, "The goal is already met.");
        }
        else
        {
            reply = new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent($"call-{++_calls}", plan[0], new Dictionary<string, object?>()),
            ]);
        }

        return Task.FromResult(new ChatResponse(reply) { ModelId = "bfs" });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
