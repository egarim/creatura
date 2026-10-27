namespace Creatura;

/// <summary>Where a story stands. Figure 6 keeps the frame and changes the sequence; Figure 7 breaks the frame.</summary>
public enum StoryState
{
    /// <summary>The map holds: act from the procedure.</summary>
    Intact,

    /// <summary>An unpredicted outcome is pending examination; the next move is fixed (approach it).</summary>
    Anomalous,

    /// <summary>The should-be holds; the procedure that got here is procedural memory now.</summary>
    Closed,

    /// <summary>No procedure reaches the should-be with the tools still known; the problem went up one level.</summary>
    Collapsed,
}

/// <summary>
/// Peterson's unit of the known (Figure 1): a present, a desired state and the way between them. Stories nest
/// (Figures 14 to 16): a sub-story is opened when a step needs a fact that does not hold, and the missing fact
/// becomes its should-be. The procedure is the sequence as it is actually run, not a plan: the policy proposes one
/// step at a time, which is how a procedure can be known before it can be stated (Figure 49).
/// </summary>
public sealed class Story
{
    public required string Name { get; init; }

    public required Func<IReadOnlySet<string>, bool> ShouldBe { get; init; }

    /// <summary>For a sub-story: the tool whose precondition this story exists to make true.</summary>
    public string? EnablesTool { get; init; }

    /// <summary>For a sub-story: the fact it exists to make true.</summary>
    public string? Fact { get; init; }

    public Story? Parent { get; init; }

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    public StoryState State { get; set; } = StoryState.Intact;

    /// <summary>The tool that lied and has not been approached yet, while the story is anomalous.</summary>
    public string? PendingAnomaly { get; set; }

    /// <summary>The tools this story's procedure has run, in order. A liar found here is a dependency broken.</summary>
    public List<string> Used { get; } = [];

    public override string ToString() => $"{Name} ({State.ToString().ToLowerInvariant()}{(PendingAnomaly is null ? "" : $", {PendingAnomaly} pending")})";
}

/// <summary>One anomaly, as the episodic memory keeps it: what broke, how much, and what the approach showed.</summary>
public sealed record Episode(int Tick, string Tool, string Story, double Magnitude, bool? RetryLanded);

/// <summary>
/// The agent's map of the territory in Peterson's three forms and in his order (Figures 12, 13 and 49):
/// procedures that closed a story, episodes of anomaly, and rules written only when the episodes repeat.
/// Tools that lied twice are unknown territory, kept apart from tools that merely failed.
/// </summary>
public sealed class TerritoryMap
{
    /// <summary>How many times a tool must be found unreliable before a rule is written about it.</summary>
    public const int RuleAfter = 3;

    public Dictionary<string, List<string>> Procedures { get; } = new(StringComparer.Ordinal);

    public List<Episode> Episodes { get; } = [];

    public HashSet<string> Unknown { get; } = new(StringComparer.Ordinal);

    /// <summary>Tools whose enabling sub-story collapsed: the route to them is gone for this run.</summary>
    public HashSet<string> Unreachable { get; } = new(StringComparer.Ordinal);

    /// <summary>Facts no story could make true: a sub-story for them collapsed, and none is opened again.</summary>
    public HashSet<string> Dead { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _unreliable = new(StringComparer.Ordinal);

    public void Closed(Story story)
    {
        if (story.Used.Count > 0) Procedures[story.Name] = [.. story.Used];
    }

    public void Lied(string tool)
    {
        _unreliable[tool] = _unreliable.GetValueOrDefault(tool) + 1;
    }

    /// <summary>Semantic memory: what the episodes let the agent say, with the count that earned it.</summary>
    public IEnumerable<string> Rules() =>
        _unreliable.Where(kv => kv.Value >= RuleAfter).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key} cannot be relied on: found lying {kv.Value} times");

    public int Unreliability(string tool) => _unreliable.GetValueOrDefault(tool);

    /// <summary>The map as data, in Peterson's order: procedures, episodes, then what the rules are written from.</summary>
    public TerritorySnapshot Snapshot() => new(
        Procedures.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal),
        [.. Episodes],
        Unknown.Order(StringComparer.Ordinal).ToList(),
        Unreachable.Order(StringComparer.Ordinal).ToList(),
        Dead.Order(StringComparer.Ordinal).ToList(),
        _unreliable.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));

    /// <summary>Replace the map with a snapshot's, in place (the map-keeper holds the instance for the run).</summary>
    public void Restore(TerritorySnapshot snapshot)
    {
        Procedures.Clear();
        foreach (var (k, v) in snapshot.Procedures) Procedures[k] = [.. v];
        Episodes.Clear();
        Episodes.AddRange(snapshot.Episodes);
        Unknown.Clear();
        Unknown.UnionWith(snapshot.Unknown);
        Unreachable.Clear();
        Unreachable.UnionWith(snapshot.Unreachable);
        Dead.Clear();
        Dead.UnionWith(snapshot.Dead);
        _unreliable.Clear();
        foreach (var (k, v) in snapshot.Unreliable) _unreliable[k] = v;
    }
}

/// <summary>A <see cref="TerritoryMap"/> as data: what a record keeps of it. <c>Unreliable</c> is the count behind each rule, so the rules come back exactly.</summary>
public sealed record TerritorySnapshot(Dictionary<string, List<string>> Procedures, List<Episode> Episodes, List<string> Unknown, List<string> Unreachable, List<string> Dead, Dictionary<string, int> Unreliable);
