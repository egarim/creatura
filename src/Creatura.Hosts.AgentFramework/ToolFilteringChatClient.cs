using System.Runtime.CompilerServices;
using Creatura;
using Microsoft.Extensions.AI;

namespace Creatura.Hosts.AgentFramework;

/// <summary>
/// Principle 3 at the model boundary: the agent decides what the model sees. Every model call is one tick;
/// before the request leaves, tools the control layer does not currently trust are removed from the
/// request, so the model never gets their schema. The function middleware refuses them a second time if the
/// model names one from memory.
/// </summary>
public sealed class ToolFilteringChatClient(IChatClient inner, ControlLayer control) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        control.BeginTick();
        return base.GetResponseAsync(messages, Filter(options), cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        control.BeginTick();
        await foreach (var update in base.GetStreamingResponseAsync(messages, Filter(options), cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private ChatOptions? Filter(ChatOptions? options)
    {
        if (options?.Tools is null)
        {
            return options;
        }

        var offered = control.Offer().Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var filtered = options.Clone();
        filtered.Tools = options.Tools.Where(t => offered.Contains(t.Name)).ToList();
        return filtered;
    }
}
