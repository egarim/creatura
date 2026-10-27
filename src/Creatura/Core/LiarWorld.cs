namespace Creatura;

/// <summary>
/// A world where tools lie: the first call of each (or every call, when <c>always</c>) with its preconditions met
/// reports success and changes nothing, without the inner world being asked. With no tools named every tool lies
/// once, which is the panic test's world: stress has nowhere to go but up. <see cref="Barrier.LieOnce"/> and
/// <see cref="Barrier.LieAlways"/> build the named forms.
/// </summary>
public sealed class LiarWorld : IWorld, ICloneableWorld
{
    private readonly IWorld _inner;
    private readonly IReadOnlySet<string>? _tools;
    private readonly bool _always;
    private readonly HashSet<string> _lied = new(StringComparer.Ordinal);

    public LiarWorld(IWorld inner) : this(inner, null)
    {
    }

    /// <param name="inner">The world under the lies.</param>
    /// <param name="tools">The tools that lie; null for every tool.</param>
    /// <param name="always">Lie on every call instead of the first.</param>
    public LiarWorld(IWorld inner, IEnumerable<string>? tools, bool always = false)
    {
        _inner = inner;
        _tools = tools is null ? null : new HashSet<string>(tools, StringComparer.Ordinal);
        _always = always;
    }

    private LiarWorld(IWorld inner, IReadOnlySet<string>? tools, bool always, HashSet<string> lied, int lies) : this(inner, tools, always)
    {
        _lied.UnionWith(lied);
        Lies = lies;
    }

    public IWorld Inner => _inner;

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
        if (tool is not null && (_tools is null || _tools.Contains(toolId)) && tool.Requires.All(_inner.Facts.Contains) && (_always || _lied.Add(toolId)))
        {
            // Counted as this world judges it: a lie is a reported success the registry does not back. A tool whose
            // promise the registry already holds (by another road) reports a success that is not a lie.
            var lie = new ActionOutcome(toolId, true, [], "reported success");
            if (((IWorldView)this).Verdict(tool, lie) == ActionVerdict.Lied) Lies++;
            return lie;
        }

        return _inner.Act(toolId, payload);
    }

    public IWorld Clone() => new LiarWorld(ICloneableWorld.CloneOf(_inner), _tools, _always, _lied, Lies);
}
