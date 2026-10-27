

namespace Creatura;

/// <summary>
/// A demo world with the map taken away. Tools keep their ids and their kind (the office knows which of
/// its actions are releases; it does not know what they produce); preconditions and effects are hidden,
/// observations are reduced to "something changed", "reported success" or "nothing happened", and whether
/// an effect landed means whether the registry gained anything. Tools can be revoked, which is how the
/// harness blocks a route.
/// </summary>
public sealed class OpaqueWorld : IWorld
{
    private readonly IWorld _inner;
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>> _lastGain = new(StringComparer.Ordinal);

    public OpaqueWorld(IWorld inner, IEnumerable<string>? revoked = null)
    {
        _inner = inner;
        if (revoked is not null) _revoked.UnionWith(revoked);
    }

    public IWorld Inner => _inner;

    /// <summary>With the map left in place: tools keep their declared preconditions and effects. Only revocation applies.</summary>
    public bool Transparent { get; init; }

    public IReadOnlySet<string> Revoked => _revoked;

    /// <summary>Actions that reported success and changed nothing: the lies, counted at the source rather than from trace text.</summary>
    public int Lies { get; private set; }

    /// <summary>Actions that reported failure: honest, and counted apart from the lies.</summary>
    public int HonestFailures { get; private set; }

    public IReadOnlySet<string> Facts => _inner.Facts;

    public int Ticks => _inner.Ticks;

    public IReadOnlyList<Tool> AvailableTools => _inner.AvailableTools.Where(t => !_revoked.Contains(t.Id)).Select(Strip).ToList();

    public IReadOnlyList<Tool> AllTools => _inner.AllTools.Where(t => !_revoked.Contains(t.Id)).Select(Strip).ToList();

    public bool Has(string fact) => _inner.Has(fact);

    public Tool? Tool(string id) => _revoked.Contains(id) ? null : _inner.Tool(id) is { } t ? Strip(t) : null;

    /// <summary>The only success check an organism without the map can make: did the registry gain anything.</summary>
    public bool EffectsLanded(Tool tool) => Transparent ? _inner.EffectsLanded(_inner.Tool(tool.Id) ?? tool) : _lastGain.TryGetValue(tool.Id, out var gain) && gain.Count > 0;

    /// <summary>What the registry gained the last time this tool ran. Empty for a lie or a failure; null if never run.</summary>
    public IReadOnlySet<string>? LastGain(string toolId) => _lastGain.GetValueOrDefault(toolId);

    public ActionOutcome Act(string toolId, string? payload = null)
    {
        if (_revoked.Contains(toolId))
        {
            return new ActionOutcome(toolId, false, [], "nothing happened");
        }

        var before = new HashSet<string>(_inner.Facts, StringComparer.Ordinal);
        var outcome = _inner.Act(toolId, payload);
        var gained = new HashSet<string>(_inner.Facts, StringComparer.Ordinal);
        gained.ExceptWith(before);
        _lastGain[toolId] = gained;
        // A lie reports success and grants nothing; a repeat of an action already done grants what is already there and is not one.
        if (outcome.Succeeded && outcome.Granted.Count == 0) Lies++;
        if (!outcome.Succeeded) HonestFailures++;
        if (Transparent) return outcome;
        var observation = !outcome.Succeeded ? "nothing happened" : gained.Count > 0 ? "something changed" : "reported success";
        return new ActionOutcome(toolId, outcome.Succeeded, gained.ToList(), observation);
    }

    private Tool Strip(Tool t) => Transparent ? t : new Tool(t.Id, [], [], t.Kind) { NeedsPayload = t.NeedsPayload, PayloadHint = t.PayloadHint };
}

/// <summary>
/// The map the organism draws for itself. For each tool: what the registry gained when it landed, the facts
/// present each time it landed (their intersection is the working guess at preconditions), and how often it
/// was tried and failed. Carried across runs of the same agent so that a blocked route is not forgotten.
/// </summary>
public sealed class EffectMemory
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public sealed class Entry
    {
        public int Tried;
        public int Landed;
        public int Failed;
        public int Lied;
        public readonly HashSet<string> Gains = new(StringComparer.Ordinal);
        public HashSet<string>? RequiresGuess;
    }

    public IReadOnlyDictionary<string, Entry> Entries => _entries;

    public int ToolsTried => _entries.Count(e => e.Value.Tried > 0);

    public void Observe(string toolId, IReadOnlySet<string> factsBefore, ActionOutcome outcome, IReadOnlyList<string> gained)
    {
        var e = _entries.TryGetValue(toolId, out var x) ? x : _entries[toolId] = new Entry();
        e.Tried++;
        if (gained.Count > 0)
        {
            e.Landed++;
            e.Gains.UnionWith(gained);
            if (e.RequiresGuess is null) e.RequiresGuess = new HashSet<string>(factsBefore, StringComparer.Ordinal);
            else e.RequiresGuess.IntersectWith(factsBefore);
        }
        else if (outcome.Succeeded) e.Lied++;
        else e.Failed++;
    }

    public bool Tried(string toolId) => _entries.TryGetValue(toolId, out var e) && e.Tried > 0;

    /// <summary>The tool as the organism currently understands it. Untried tools promise nothing and need nothing.</summary>
    public Tool Learned(Tool opaque)
    {
        if (!_entries.TryGetValue(opaque.Id, out var e) || e.Landed == 0) return opaque;
        return opaque with { Requires = e.RequiresGuess?.Order(StringComparer.Ordinal).ToList() ?? [], Grants = e.Gains.Order(StringComparer.Ordinal).ToList() };
    }
}

/// <summary>
/// What the agent actually acts on: the opaque world, seen through the effect memory. Tools carry the
/// learned preconditions and effects, so the architecture's planner, quarantine and stage interlock work
/// unchanged over the organism's own map instead of the office's.
/// </summary>
public sealed class LearnedWorld : IWorld
{
    private readonly OpaqueWorld _opaque;
    private readonly EffectMemory _memory;

    public LearnedWorld(OpaqueWorld opaque, EffectMemory memory)
    {
        _opaque = opaque;
        _memory = memory;
    }

    public OpaqueWorld Opaque => _opaque;

    public EffectMemory Memory => _memory;

    public IReadOnlySet<string> Facts => _opaque.Facts;

    public int Ticks => _opaque.Ticks;

    public IReadOnlyList<Tool> AvailableTools => _opaque.AvailableTools.Select(_memory.Learned).ToList();

    public IReadOnlyList<Tool> AllTools => _opaque.AllTools.Select(_memory.Learned).ToList();

    public bool Has(string fact) => _opaque.Has(fact);

    public Tool? Tool(string id) => _opaque.Tool(id) is { } t ? _memory.Learned(t) : null;

    public bool EffectsLanded(Tool tool) => _opaque.EffectsLanded(tool);

    public ActionOutcome Act(string toolId, string? payload = null)
    {
        var before = new HashSet<string>(_opaque.Facts, StringComparer.Ordinal);
        var outcome = _opaque.Act(toolId, payload);
        _memory.Observe(toolId, before, outcome, outcome.Granted);
        return outcome;
    }
}
