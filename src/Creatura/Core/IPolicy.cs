namespace Creatura;

/// <summary>
/// Everything a decision component is allowed to see at one tick. The architecture decides what goes in
/// here — which tools are visible, what the self-model says, what the stage permits — so the policy cannot
/// route around the design's constraints even when the policy is a language model.
/// </summary>
public sealed record PolicyContext
{
    public required IReadOnlySet<string> Facts { get; init; }

    public required IReadOnlyList<Tool> Tools { get; init; }

    public required Func<IReadOnlySet<string>, bool> Goal { get; init; }

    public required string GoalText { get; init; }

    public required int Tick { get; init; }

    public required int MaxTicks { get; init; }

    public required double Freedom { get; init; }

    /// <summary>The rendered self-model, when the agent has one. Null for the Levin colony's tissues.</summary>
    public string? SelfModel { get; init; }

    public string? Stage { get; init; }

    public IReadOnlyList<string> History { get; init; } = [];

    public IReadOnlyList<string> Preferred { get; init; } = [];

    /// <summary>Lines an extension wants the policy to read this tick, such as the agent's biology. Empty by default.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>One decision: the tool to run next, or null when the policy sees no way forward.</summary>
/// <param name="Tool">The tool to run, or null when the policy sees no way forward.</param>
/// <param name="Rationale">One line for the trace — the model's own words, kept short.</param>
/// <param name="Content">The payload, when the chosen tool needs one (e.g. code to write).</param>
public sealed record PolicySuggestion(string? Tool, string Rationale, string? Content = null);

/// <summary>
/// The seam where a model plugs in. Breadth-first search today; a language model in
/// <c>BiologicalAgents.DeepSeek</c>. Everything around this call — set points, stress, coherence, stage
/// caps, quarantine — is unchanged whichever policy is in place, which is the whole point.
/// </summary>
public interface IPolicy
{
    string Name { get; }

    Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default);
}

/// <summary>Deterministic policy: BFS over tool effects, habitual tool first. The baseline for the LLM test.</summary>
public sealed class PlannerPolicy : IPolicy
{
    private readonly int _maxDepth;

    public PlannerPolicy(int maxDepth = 8) => _maxDepth = maxDepth;

    public string Name => "bfs";

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        // BFS cannot produce a payload (it has no generator), so tools that need one are out of its reach.
        var reachable = context.Tools.Where(t => !t.NeedsPayload).ToList();
        var plan = Planner.Find(context.Facts, reachable, context.Goal, _maxDepth, context.Preferred);
        var suggestion = plan is null or { Count: 0 }
            ? new PolicySuggestion(null, "no path with the visible tools")
            : new PolicySuggestion(plan[0], plan.Count > 1 ? $"plan: {string.Join(" → ", plan)}" : "last step");
        return Task.FromResult(suggestion);
    }
}

/// <summary>
/// A decision layer that throws is a failed decision, not a crashed agent. Models time out, connections drop,
/// JSON comes back truncated — none of that is a reason to lose the run.
/// </summary>
public static class PolicyGuard
{
    public static async Task<PolicySuggestion> NextAsync(
        IPolicy policy,
        PolicyContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await policy.NextAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new PolicySuggestion(null, $"policy error: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
