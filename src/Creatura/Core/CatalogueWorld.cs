namespace Creatura;

/// <summary>What a <see cref="FaultRule"/> does when its tool is run with its preconditions met.</summary>
public enum FaultKind
{
    /// <summary>The tool reports failure and grants nothing. Not a lie; another means usually exists.</summary>
    HonestFailure,

    /// <summary>The tool reports success and grants nothing: the lie convention.</summary>
    Lie,

    /// <summary>The tool lands as written and the registry also learns something that changes what a later step needs.</summary>
    ChangedRule,

    /// <summary>The tool is taken away: hidden from <see cref="IWorldView.AvailableTools"/>, "tool unavailable" when run.</summary>
    Revoked,
}

/// <summary>
/// One row of a <see cref="CatalogueWorld"/>'s fault table: which tool, what happens instead of the grant, what the
/// registry learns on the way (<see cref="Adds"/>), until which fact the row holds (<see cref="Until"/>, for an honest
/// failure that a later step repairs), and whether it fires once or every time. Rows are read in order; the first
/// that applies wins. Barriers are data here, not branches in <c>Act</c>.
/// </summary>
public sealed record FaultRule
{
    public required string Tool { get; init; }

    public required FaultKind Kind { get; init; }

    /// <summary>What the tool says. Null for the toy world's silent grant; the samples use their <c>obs.*</c> keys.</summary>
    public string? Observation { get; init; }

    /// <summary>Facts the registry gains when the row fires, whatever the kind: the doubt or the condition the world reveals.</summary>
    public IReadOnlyList<string> Adds { get; init; } = [];

    /// <summary>The row applies while this fact is absent; null means always.</summary>
    public string? Until { get; init; }

    /// <summary>Spent after the first time it fires.</summary>
    public bool Once { get; init; }

    /// <summary>Reports failure and grants nothing, every time until <paramref name="until"/> holds (or always when null).</summary>
    public static FaultRule HonestFailure(string tool, string? observation = null, string? until = null, params string[] adds) =>
        new() { Tool = tool, Kind = FaultKind.HonestFailure, Observation = observation, Until = until, Adds = adds };

    /// <summary>Reports success and grants nothing, the first time only.</summary>
    public static FaultRule LieOnce(string tool, string? observation = "reported success", params string[] adds) =>
        new() { Tool = tool, Kind = FaultKind.Lie, Observation = observation, Once = true, Adds = adds };

    /// <summary>Reports success and grants nothing, every time.</summary>
    public static FaultRule LieAlways(string tool, string? observation = "reported success", params string[] adds) =>
        new() { Tool = tool, Kind = FaultKind.Lie, Observation = observation, Adds = adds };

    /// <summary>Lands as written, says <paramref name="observation"/> instead of the usual, and the registry learns <paramref name="adds"/>.</summary>
    public static FaultRule Reveals(string tool, string? observation, params string[] adds) =>
        new() { Tool = tool, Kind = FaultKind.ChangedRule, Observation = observation, Adds = adds };

    /// <summary>The tool no longer exists for this agent.</summary>
    public static FaultRule Revoked(string tool) => new() { Tool = tool, Kind = FaultKind.Revoked };
}

/// <summary>
/// The base of every catalogue-style world: a fact registry (the only truth), a tool catalogue, and a fault table.
/// It pins the laws the hand-rolled worlds used to re-encode one by one:
/// <list type="bullet">
/// <item><see cref="EffectsLanded"/> is <c>tool.Grants.All(Has)</c>, read live from the registry, never a before/after
/// diff; a tool that promises nothing has landed (all of nothing is true).</item>
/// <item>The refusal order in <see cref="Act"/>: unknown tool → "tool unavailable" → "preconditions unmet: a, b" →
/// the fault table → the grant. <see cref="Ticks"/> moves on every call, refused or not.</item>
/// <item>The lie convention: a lie reports success and grants nothing.</item>
/// <item><see cref="Shape"/> rewrites a tool from what the registry has learned (a step that now needs a deed the
/// office found missing); <see cref="Offered"/> hides a tool from <see cref="AvailableTools"/> (a shortcut switch).</item>
/// <item><see cref="Clone"/> gives an isolated copy for a habitat to dream in.</item>
/// </list>
/// A derived world supplies its catalogue, its starting facts and its rows, and overrides only what it must.
/// </summary>
public abstract class CatalogueWorld : IWorld, ICloneableWorld
{
    private sealed class Row(FaultRule rule)
    {
        public FaultRule Rule { get; } = rule;

        public bool Spent { get; set; }

        public Row Copy() => new(Rule) { Spent = Spent };
    }

    private readonly IReadOnlyList<Tool> _catalogue;
    private HashSet<string> _facts;
    private List<Row> _rows;

    protected CatalogueWorld(IEnumerable<Tool> catalogue, IEnumerable<string> facts, IEnumerable<FaultRule>? faults = null)
    {
        _catalogue = catalogue.ToList();
        _facts = new HashSet<string>(facts, StringComparer.Ordinal);
        _rows = (faults ?? []).Select(f => new Row(f)).ToList();
    }

    /// <summary>The catalogue as written, before <see cref="Shape"/> and <see cref="Offered"/>.</summary>
    public IReadOnlyList<Tool> Catalogue => _catalogue;

    /// <summary>The fault table, in the order it is read.</summary>
    public IReadOnlyList<FaultRule> Faults => _rows.Select(r => r.Rule).ToList();

    public IReadOnlySet<string> Facts => _facts;

    public int Ticks { get; private set; }

    /// <summary>Every tool of the catalogue, shaped by what the registry knows; a revoked tool stays here.</summary>
    public virtual IReadOnlyList<Tool> AllTools => _catalogue.Select(Shape).ToList();

    /// <summary>The tools the agent is offered: the catalogue without what is not <see cref="Offered"/> or is revoked, shaped.</summary>
    public virtual IReadOnlyList<Tool> AvailableTools => _catalogue.Where(t => Offered(t) && !IsRevoked(t.Id)).Select(Shape).ToList();

    public bool Has(string fact) => _facts.Contains(fact);

    /// <summary>The tool by id as the agent would see it, from <see cref="AllTools"/>; null is "unknown tool".</summary>
    public virtual Tool? Tool(string id) => AllTools.FirstOrDefault(t => t.Id == id);

    /// <summary>The one honest success check: the registry holds everything the tool promised. Vacuous for a tool that promises nothing.</summary>
    public bool EffectsLanded(Tool tool) => tool.Grants.All(_facts.Contains);

    /// <summary>Adds a row at the end of the fault table.</summary>
    public void Inject(FaultRule rule) => _rows.Add(new Row(rule));

    /// <summary>A barrier: the tool no longer exists for this agent.</summary>
    public void Revoke(string toolId)
    {
        if (!IsRevoked(toolId)) Inject(FaultRule.Revoked(toolId));
    }

    public bool IsRevoked(string toolId) => _rows.Any(r => r.Rule.Kind == FaultKind.Revoked && r.Rule.Tool == toolId);

    public ActionOutcome Act(string toolId, string? payload = null)
    {
        Ticks++;
        var tool = Tool(toolId);
        if (tool is null)
        {
            return new ActionOutcome(toolId, false, [], "unknown tool");
        }

        if (IsRevoked(toolId))
        {
            return new ActionOutcome(toolId, false, [], "tool unavailable");
        }

        var missing = tool.Requires.Where(r => !_facts.Contains(r)).ToList();
        if (missing.Count > 0)
        {
            return new ActionOutcome(toolId, false, [], $"preconditions unmet: {string.Join(", ", missing)}");
        }

        var row = _rows.FirstOrDefault(r => Applies(r, toolId));
        if (row is not null)
        {
            if (row.Rule.Once) row.Spent = true;
            _facts.UnionWith(row.Rule.Adds);
            switch (row.Rule.Kind)
            {
                case FaultKind.HonestFailure:
                    return new ActionOutcome(toolId, false, [], row.Rule.Observation);
                case FaultKind.Lie:
                    return new ActionOutcome(toolId, true, [], row.Rule.Observation);
                case FaultKind.ChangedRule:
                    return Land(tool, row.Rule.Observation);
            }
        }

        return Land(tool, Observation(tool));
    }

    private bool Applies(Row row, string toolId) =>
        row.Rule.Tool == toolId && !row.Spent && row.Rule.Kind != FaultKind.Revoked && (row.Rule.Until is null || !_facts.Contains(row.Rule.Until));

    /// <summary>The grant: the registry gains what the tool promised and the outcome says so.</summary>
    protected virtual ActionOutcome Land(Tool tool, string? observation)
    {
        _facts.UnionWith(tool.Grants);
        return new ActionOutcome(tool.Id, true, tool.Grants, observation);
    }

    /// <summary>What a landed act of this tool says. Null by default (the toy world says nothing).</summary>
    protected virtual string? Observation(Tool tool) => null;

    /// <summary>The tool as the registry's knowledge shapes it: fact-triggered <see cref="Tool.Requires"/> rewriting. Identity by default.</summary>
    protected virtual Tool Shape(Tool tool) => tool;

    /// <summary>Whether the tool is on offer at all: the shortcut switches. Everything by default.</summary>
    protected virtual bool Offered(Tool tool) => true;

    /// <summary>The registry learns a fact outside any tool: an approval, a condition the world reveals.</summary>
    protected void Reveal(string fact) => _facts.Add(fact);

    /// <summary>An independent copy: same facts, same catalogue, same rows with their state, same ticks. Acting on it leaves this world untouched.</summary>
    public virtual CatalogueWorld Clone()
    {
        var copy = (CatalogueWorld)MemberwiseClone();
        copy._facts = new HashSet<string>(_facts, StringComparer.Ordinal);
        copy._rows = _rows.Select(r => r.Copy()).ToList();
        return copy;
    }

    IWorld ICloneableWorld.Clone() => Clone();
}
