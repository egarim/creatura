namespace Creatura;

/// <summary>A tool: preconditions in, effects out. The world never tells you which tools are lies.</summary>
public sealed record Tool(string Id, IReadOnlyList<string> Requires, IReadOnlyList<string> Grants, string Kind)
{
    /// <summary>True when the policy must supply a payload (e.g. the code to write) with the call.</summary>
    public bool NeedsPayload { get; init; }

    /// <summary>Extra guidance for a policy that has to produce a payload.</summary>
    public string? PayloadHint { get; init; }
}

/// <summary>What the world reports back — which is not the same as what actually happened.</summary>
public sealed record ActionOutcome(string Tool, bool Succeeded, IReadOnlyList<string> Granted, string? Observation);

/// <summary>
/// The world as read: the fact registry (the only truth), the tool catalogue, and the one honest success
/// check. Everything that measures a world (a task's progress, a set point, the quarantine's visible set, the
/// control layer) codes against this; only the loop that acts needs <see cref="IWorld"/>.
/// </summary>
public interface IWorldView
{
    IReadOnlySet<string> Facts { get; }

    IReadOnlyList<Tool> AvailableTools { get; }

    IReadOnlyList<Tool> AllTools { get; }

    bool Has(string fact);

    Tool? Tool(string id);

    bool EffectsLanded(Tool tool);
}

/// <summary>
/// The surface every agent codes against: the view, plus acting on it. <see cref="World"/> is the toy
/// service-restore domain; <c>BiologicalAgents.CodeWorld.CodeWorld</c> is a real one that edits files and
/// runs the compiler.
/// </summary>
public interface IWorld : IWorldView
{
    int Ticks { get; }

    ActionOutcome Act(string toolId, string? payload = null);
}

/// <summary>
/// A tiny deterministic world: a set of facts, a set of tools, and injected faults.
/// Faults are the barriers the designs are evaluated against.
/// </summary>
public sealed class World : IWorld
{
    private readonly HashSet<string> _facts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tool> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liars = new(StringComparer.Ordinal);

    public World(IEnumerable<Tool> tools, IEnumerable<string> facts)
    {
        foreach (var tool in tools)
        {
            _tools[tool.Id] = tool;
        }

        foreach (var fact in facts)
        {
            _facts.Add(fact);
        }
    }

    public int Ticks { get; private set; }

    public IReadOnlySet<string> Facts => _facts;

    public IReadOnlyList<Tool> AvailableTools =>
        _tools.Values.Where(t => !_revoked.Contains(t.Id)).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<Tool> AllTools => _tools.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();

    public bool Has(string fact) => _facts.Contains(fact);

    public Tool? Tool(string id) => _tools.GetValueOrDefault(id);

    /// <summary>A barrier: the tool no longer exists for this agent.</summary>
    public void Revoke(string toolId) => _revoked.Add(toolId);

    /// <summary>
    /// The nastiest barrier: the tool reports success and grants nothing.
    /// An agent that trusts self-report never notices; one that checks postconditions does.
    /// </summary>
    public void MakeLiar(string toolId) => _liars.Add(toolId);

    public bool IsRevoked(string toolId) => _revoked.Contains(toolId);

    public ActionOutcome Act(string toolId, string? payload = null)
    {
        Ticks++;
        if (!_tools.TryGetValue(toolId, out var tool))
        {
            return new ActionOutcome(toolId, false, [], "unknown tool");
        }

        if (_revoked.Contains(toolId))
        {
            return new ActionOutcome(toolId, false, [], "tool unavailable");
        }

        var missing = tool.Requires.Where(r => !_facts.Contains(r)).ToList();
        if (missing.Count > 0)
        {
            return new ActionOutcome(toolId, false, [], $"preconditions unmet: {string.Join(", ", missing)}");
        }

        if (_liars.Remove(toolId))
        {
            // Reports success, changes nothing.
            return new ActionOutcome(toolId, true, [], "reported success");
        }

        _facts.UnionWith(tool.Grants);
        return new ActionOutcome(toolId, true, tool.Grants, null);
    }

    /// <summary>Did the last action's stated effects actually land? The only honest success check.</summary>
    public bool EffectsLanded(Tool tool) => tool.Grants.All(_facts.Contains);
}
