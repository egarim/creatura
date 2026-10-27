namespace Creatura.Orienting;

/// <summary>What the agent has learned to expect of one tool on one goal: how much promise, how much threat.</summary>
public sealed record ToolWeight(double Promise, double Threat, int Seen)
{
    /// <summary>The unclassified carries both at full strength (design-sokolov.md, claim 5).</summary>
    public static ToolWeight Unclassified { get; } = new(1.0, 1.0, 0);

    public Valence.Expectation Expectation =>
        Seen == 0 ? Valence.Expectation.Unclassified : Promise >= Threat ? Valence.Expectation.Reward : Valence.Expectation.Punishment;

    /// <summary>The choice score: promise minus caution times threat.</summary>
    public double Score(double caution) => Promise - caution * Threat;
}

/// <summary>
/// The operant part: per tool and goal (claim 3: the threat belongs to the relation between the goal and the tool, not
/// to the tool), weights move toward the consequence each time (frequency up for what rewarded, down for what
/// punished). A tool with no weights of its own on a goal starts from the weights of tools that grant the same goal
/// fact, scaled toward the unclassified prior (claim 9, classification by function).
/// </summary>
public sealed class ValenceWeights
{
    private readonly Dictionary<(string Tool, string Goal), ToolWeight> _weights = new();
    private readonly Dictionary<string, HashSet<string>> _function = new(StringComparer.Ordinal);

    /// <summary>How fast a weight moves toward a consequence.</summary>
    public double Rate { get; init; } = 0.5;

    /// <summary>How much of a sibling's learned weight a newcomer inherits; 0 switches generalisation off.</summary>
    public double Generalisation { get; init; } = 0.5;

    /// <summary>Record what a tool grants, so tools with the same function can be found.</summary>
    public void Declare(string tool, IEnumerable<string> grants)
    {
        foreach (var g in grants)
        {
            if (!_function.TryGetValue(g, out var set)) _function[g] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(tool);
        }
    }

    public ToolWeight Get(string tool, string goal)
    {
        if (_weights.TryGetValue((tool, goal), out var own)) return own;
        if (Generalisation <= 0) return ToolWeight.Unclassified;
        var siblings = _function.Values.Where(s => s.Contains(tool)).SelectMany(s => s).Where(t => t != tool).Distinct()
            .Select(t => _weights.TryGetValue((t, goal), out var w) ? w : null).Where(w => w is { Seen: > 0 }).ToList();
        if (siblings.Count == 0) return ToolWeight.Unclassified;
        var p = siblings.Average(w => w!.Promise);
        var th = siblings.Average(w => w!.Threat);
        return new ToolWeight(1.0 + (p - 1.0) * Generalisation, 1.0 + (th - 1.0) * Generalisation, 0);
    }

    /// <summary>Move a tool's weights on a goal toward what this outcome was.</summary>
    public ToolWeight Learn(string tool, string goal, ValenceReading reading)
    {
        var w = _weights.TryGetValue((tool, goal), out var own) ? own : Get(tool, goal);
        var promiseTarget = reading.Has(ValenceKind.Satisfaction) || reading.Has(ValenceKind.Promise) || reading.Has(ValenceKind.Relief) ? 1.0 : 0.0;
        var threatTarget = reading.Has(ValenceKind.Punishment) ? reading.PunishmentWeight : reading.Has(ValenceKind.Threat) ? 0.5 : 0.0;
        var next = new ToolWeight(w.Promise + (promiseTarget - w.Promise) * Rate, w.Threat + (threatTarget - w.Threat) * Rate, w.Seen + 1);
        _weights[(tool, goal)] = next;
        return next;
    }
}
