namespace Creatura;

/// <summary>
/// A world where every tool lies once: the first call of each reports success and changes nothing, the
/// second works. Stress has nowhere to go but up. Built for the panic test.
/// </summary>
public sealed class LiarWorld : IWorld
{
    private readonly IWorld _inner;
    private readonly HashSet<string> _lied = new(StringComparer.Ordinal);

    public LiarWorld(IWorld inner) => _inner = inner;

    public int Lies { get; private set; }

    public IReadOnlySet<string> Facts => _inner.Facts;

    public IReadOnlyList<Tool> AvailableTools => _inner.AvailableTools;

    public IReadOnlyList<Tool> AllTools => _inner.AllTools;

    public int Ticks => _inner.Ticks;

    public bool Has(string fact) => _inner.Has(fact);

    public Tool? Tool(string id) => _inner.Tool(id);

    public bool EffectsLanded(Tool tool) => _inner.EffectsLanded(tool);

    public ActionOutcome Act(string toolId, string? payload = null)
    {
        var tool = _inner.Tool(toolId);
        if (tool is not null && tool.Requires.All(_inner.Facts.Contains) && _lied.Add(toolId))
        {
            Lies++;
            return new ActionOutcome(toolId, true, [], "reported success");
        }

        return _inner.Act(toolId, payload);
    }
}
