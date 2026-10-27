namespace Creatura.Orienting;

/// <summary>What an action came to, reduced to what the trace compares: whether it delivered, and what it added.</summary>
public sealed record OutcomeSignature(bool Delivered, IReadOnlyList<string> Gained)
{
    public bool Matches(OutcomeSignature other) =>
        Delivered == other.Delivered && Gained.Order(StringComparer.Ordinal).SequenceEqual(other.Gained.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    public override string ToString() => $"{(Delivered ? "delivered" : "did not deliver")}{(Gained.Count == 0 ? "" : $" +{string.Join(",", Gained)}")}";
}

/// <summary>The result of comparing an outcome with the trace of the last one: orient or not, and the count after.</summary>
public sealed record TraceReading(bool Orient, bool Habituated, int Matches, OutcomeSignature? Previous);

/// <summary>
/// Sokolov's trace, with Peterson's correction. Per action and situation the agent keeps the outcome it saw last
/// and a count of matches. A differing outcome, better or worse, resets the count and orients (design-sokolov.md,
/// claims 1 and 2: the orienting reflex is produced by "signals of discrepancy", and the slightest change wakes it).
/// Only a match that also delivered what the agent wanted counts toward habituation (claim 2 as revised after part
/// 07: habituation is the consequence of control, not of repetition), so a tool that fails the same way five times
/// stays a threat. At <see cref="Threshold"/> controlled matches the action is habituated and its outcomes no longer
/// orient unless they differ. Nothing here expires with time.
/// </summary>
public sealed class HabituationByMatch
{
    private sealed class Entry
    {
        public OutcomeSignature? Last;
        public int Matches;
    }

    private readonly Dictionary<(string Action, string Situation), Entry> _entries = new();

    /// <summary>Sokolov's five to fifteen repetitions; the low end.</summary>
    public int Threshold { get; init; } = 5;

    /// <summary>Compare an outcome with the trace for this action in this situation, then store it.</summary>
    public TraceReading Observe(string action, string situation, OutcomeSignature outcome)
    {
        var e = _entries.TryGetValue((action, situation), out var found) ? found : _entries[(action, situation)] = new Entry();
        var previous = e.Last;
        e.Last = outcome;
        if (previous is null)
        {
            // First meeting: the unclassified is oriented to (claim 5).
            e.Matches = outcome.Delivered ? 1 : 0;
            return new TraceReading(Orient: true, Habituated: false, e.Matches, null);
        }

        if (!previous.Matches(outcome))
        {
            e.Matches = outcome.Delivered ? 1 : 0;
            return new TraceReading(Orient: true, Habituated: false, e.Matches, previous);
        }

        var wasHabituated = e.Matches >= Threshold;
        if (outcome.Delivered) e.Matches++;
        // A matching failure neither orients again nor habituates: the threat stays where consequence left it.
        return new TraceReading(Orient: false, Habituated: wasHabituated || e.Matches >= Threshold, e.Matches, previous);
    }

    public bool IsHabituated(string action, string situation) =>
        _entries.TryGetValue((action, situation), out var e) && e.Matches >= Threshold;

    public int Matches(string action, string situation) =>
        _entries.TryGetValue((action, situation), out var e) ? e.Matches : 0;

    /// <summary>The trace table, for the sleep's account: every action and situation met, the outcome last seen there and the matches.</summary>
    public IEnumerable<(string Action, string Situation, OutcomeSignature? Last, int Matches)> Entries =>
        _entries.OrderBy(kv => kv.Key.Action, StringComparer.Ordinal).ThenBy(kv => kv.Key.Situation, StringComparer.Ordinal)
            .Select(kv => (kv.Key.Action, kv.Key.Situation, kv.Value.Last, kv.Value.Matches));

    /// <summary>The actions habituated in at least one situation.</summary>
    public IEnumerable<string> Habituated => _entries.Where(kv => kv.Value.Matches >= Threshold).Select(kv => kv.Key.Action).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>The situation key: the facts an action's known preconditions read, or every fact when none are known.</summary>
    public static string Situation(IReadOnlySet<string> facts, IReadOnlyList<string>? reads = null)
    {
        IEnumerable<string> keys = reads is { Count: > 0 } ? reads.Where(facts.Contains) : facts;
        return string.Join('|', keys.Order(StringComparer.Ordinal));
    }
}
