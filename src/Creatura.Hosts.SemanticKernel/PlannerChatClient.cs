using System.Runtime.CompilerServices;
using Creatura;
using Microsoft.Extensions.AI;

namespace Creatura.Hosts.SemanticKernel;

/// <summary>
/// An offline stand-in for the model: breadth-first search over the tools the request actually offers, read back from
/// the names Semantic Kernel gave them. It exists so the control layer can be tested on this host without a key, and so
/// the same run can be compared with the Agent Framework host's, which seats the same search. It sees exactly what the
/// model would see (only the tools in the request), which is what makes the quarantine test honest.
/// </summary>
public sealed class PlannerChatClient(IWorld world, AgentTask task) : IChatClient
{
    private int _calls;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The offered names as SK spells them, each to its tool; BFS has no generator, so payload tools are out of its reach.
        var offered = (options?.Tools ?? []).ToDictionary(t => WorldToolId(t.Name) ?? t.Name, t => t.Name, StringComparer.Ordinal);
        var tools = world.AllTools.Where(t => offered.ContainsKey(t.Id) && !t.NeedsPayload).ToList();
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
            reply = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{++_calls}", offered[plan[0]], new Dictionary<string, object?>())]);
        }

        return Task.FromResult(new ChatResponse(reply) { ModelId = "bfs" });
    }

    private string? WorldToolId(string name) =>
        world.AllTools.FirstOrDefault(t => name == WorldPlugin.PluginName + "_" + WorldPlugin.FunctionName(t.Id) || name == WorldPlugin.FunctionName(t.Id))?.Id;

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
