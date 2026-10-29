using System.Runtime.CompilerServices;
using Creatura;
using Microsoft.Extensions.AI;

namespace Creatura.Hosts.SemanticKernel;

/// <summary>
/// Principle 3 at the model boundary: the agent decides what the model sees. It sits under Semantic Kernel's
/// function-calling loop, so it sees every model request the loop makes: each one is one tick, and before it leaves,
/// the world functions the control layer does not currently trust are removed from it, so the model never gets their
/// schema. Semantic Kernel's own loop keeps the full list, so a function the model names from memory still reaches the
/// layer's filter, which refuses it a second time.
///
/// This is where the reserved design had a prompt-render filter. Semantic Kernel renders a prompt once per invocation,
/// and its automatic loop then makes several requests without rendering again, so the offer would have been decided
/// once per round instead of once per request; and its function-choice behaviours cannot be extended outside the
/// package. The chat-client path is the one hook that runs before every request.
/// </summary>
public sealed class OfferingChatClient(IChatClient inner, ControlLayer control, WorldPlugin plugin, Action<UsageDetails?>? usage = null) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        control.BeginTick();
        var response = await base.GetResponseAsync(messages, Filter(options), cancellationToken).ConfigureAwait(false);
        usage?.Invoke(response.Usage);
        return response;
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
        filtered.Tools = options.Tools.Where(t => plugin.ToolId(t.Name) is { } id && offered.Contains(id)).ToList();
        return filtered;
    }
}
