using System.Collections;

namespace Creatura.Worlds;

/// <summary>A world that can be unblocked by a person: the release step stops being gated once approved.</summary>
public interface IApproval : IWorld
{
    bool ApprovedByOfficial { get; }

    void Approve();
}

/// <summary>What kind of fault a barrier switch turns on. The role is what a barrier set reads, never the id.</summary>
public enum BarrierRole
{
    /// <summary>A release-kind button with no preconditions that closes, logs or marks the case without doing it.</summary>
    Shortcut,

    /// <summary>A tool that reports success and grants nothing.</summary>
    Lie,

    /// <summary>A tool that fails and says so; another means usually exists.</summary>
    HonestFailure,

    /// <summary>A rule or a condition the registry learns mid-case, so what a step needs changes.</summary>
    ChangedRule,

    /// <summary>A tool taken away.</summary>
    Revoked,
}

/// <summary>One barrier switch of a world: its id, whether a page turns it on by default, and what it is.</summary>
public sealed record BarrierSwitch(string Id, bool DefaultOn, BarrierRole Role);

/// <summary>
/// Which barriers are on. A read-only dictionary from switch id to on/off, as the sample worlds' constructors have
/// always read it (a missing id is off), built from a manifest's switches by name, role or default.
/// </summary>
public sealed class BarrierSet : IReadOnlyDictionary<string, bool>
{
    private readonly Dictionary<string, bool> _on;

    public BarrierSet(IReadOnlyDictionary<string, bool> on) => _on = new Dictionary<string, bool>(on, StringComparer.Ordinal);

    public BarrierSet(IEnumerable<KeyValuePair<string, bool>> on) => _on = on.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>No barrier on at all: an empty set.</summary>
    public static BarrierSet Empty { get; } = new(new Dictionary<string, bool>());

    /// <summary>Every switch of the manifest on.</summary>
    public static BarrierSet All(WorldManifest manifest) => new(manifest.Barriers.ToDictionary(b => b.Id, _ => true, StringComparer.Ordinal));

    /// <summary>Only the shortcut switches on, as the demo page's "none" and the battery runner's --barriers none: the button stays, the faults go.</summary>
    public static BarrierSet None(WorldManifest manifest) => new(manifest.Barriers.ToDictionary(b => b.Id, b => b.Role == BarrierRole.Shortcut, StringComparer.Ordinal));

    /// <summary>Each switch as the page shows it first.</summary>
    public static BarrierSet Defaults(WorldManifest manifest) => new(manifest.Barriers.ToDictionary(b => b.Id, b => b.DefaultOn, StringComparer.Ordinal));

    /// <summary>As the runners read --barriers: <c>none</c>, <c>all</c>, or switch ids joined with <c>+</c>.</summary>
    public static BarrierSet Parse(WorldManifest manifest, string set) => set switch
    {
        "none" => None(manifest),
        "all" => All(manifest),
        _ => new BarrierSet(manifest.Barriers.ToDictionary(b => b.Id, b => set.Split('+').Contains(b.Id, StringComparer.Ordinal), StringComparer.Ordinal)),
    };

    /// <summary>Whether the switch is on; a switch the set does not know is off.</summary>
    public bool On(string id) => _on.GetValueOrDefault(id);

    /// <summary>This set with one switch changed.</summary>
    public BarrierSet With(string id, bool on)
    {
        var copy = new Dictionary<string, bool>(_on, StringComparer.Ordinal) { [id] = on };
        return new BarrierSet(copy);
    }

    /// <summary>The ids that are on, in order.</summary>
    public IEnumerable<string> Ons => _on.Where(kv => kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal);

    public bool this[string key] => _on[key];

    public IEnumerable<string> Keys => _on.Keys;

    public IEnumerable<bool> Values => _on.Values;

    public int Count => _on.Count;

    public bool ContainsKey(string key) => _on.ContainsKey(key);

    public bool TryGetValue(string key, out bool value) => _on.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, bool>> GetEnumerator() => _on.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join("+", Ons);
}

/// <summary>
/// The environment half of a demo: everything a runner, a battery or a page needs to put an agent into a world
/// by name. The presentation half (route, lessons, words) stays with whoever shows it, bound to the manifest's id.
/// </summary>
public sealed class WorldManifest
{
    public required string Id { get; init; }

    /// <summary>Builds the world with the given barriers on.</summary>
    public required Func<BarrierSet, IWorld> Create { get; init; }

    public required AgentTask Task { get; init; }

    /// <summary>The switches a page or a runner may turn on, with their defaults and roles.</summary>
    public IReadOnlyList<BarrierSwitch> Barriers { get; init; } = [];

    /// <summary>The facts the goal is made of, for a critic to cite and a series to walk back from.</summary>
    public IReadOnlyList<string> GoalEvidence { get; init; } = [];

    /// <summary>The steps a case file shows, in order.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>Whether the world it creates is an <see cref="IApproval"/>: a person can lift the release gate.</summary>
    public bool Approvable { get; init; }

    /// <summary>The world with the given set on, or the switches' defaults when none is given.</summary>
    public IWorld NewWorld(BarrierSet? barriers = null) => Create(barriers ?? BarrierSet.Defaults(this));

    /// <summary>The world as an <see cref="IApproval"/>, which it is when <see cref="Approvable"/> says so.</summary>
    public IApproval NewApprovable(BarrierSet? barriers = null) =>
        NewWorld(barriers) as IApproval ?? throw new InvalidOperationException($"the world '{Id}' cannot be approved");

    /// <summary>A barrier set by its runner name: <c>none</c>, <c>all</c>, or ids joined with <c>+</c>.</summary>
    public BarrierSet Set(string set) => BarrierSet.Parse(this, set);
}
