using System.Globalization;
using System.Text.Json.Serialization;

namespace Creatura.Ratings;

/// <summary>How a memory has been used: how many runs retrieved it, and how many of those ended well.</summary>
public sealed class MemoryUse
{
    public int Shown { get; set; }

    public int Good { get; set; }

    /// <summary>The share of the runs that retrieved it which ended well.</summary>
    [JsonIgnore]
    public double Rate => Shown == 0 ? 0 : (double)Good / Shown;
}

/// <summary>Where a memory stands with its persona.</summary>
public enum MemoryStanding
{
    /// <summary>Never retrieved yet.</summary>
    Unmet,

    /// <summary>Retrieved, nothing settled: shown in its place.</summary>
    Kept,

    /// <summary>Followed by good runs more often than the persona's base rate: shown first.</summary>
    Promoted,

    /// <summary>Retrieved often and never followed by a good run: not shown.</summary>
    Pruned,
}

/// <summary>
/// Memory credit (docs/design-ratings.md, R4), not TrueSkill: a persona's memories are not opponents and do not beat
/// each other, so each is credited on its own. Every run counts the memories it retrieved, and is credited to each as
/// ended well or not; the persona's own runs give the base rate. A memory retrieved <see cref="PruneAfter"/> times and
/// never followed by a good run is pruned (not shown); one retrieved at least <see cref="PromoteAfter"/> times and
/// followed by good runs more often than the base rate is promoted (shown first). Kept as a part of the persona's record.
/// </summary>
public sealed class MemoryCredit
{
    public const int PruneAfter = 3;

    public const int PromoteAfter = 2;

    /// <summary>The persona's runs so far, and how many ended well: the base rate.</summary>
    public int Runs { get; set; }

    public int Good { get; set; }

    public Dictionary<string, MemoryUse> Memories { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public double BaseRate => Runs == 0 ? 0 : (double)Good / Runs;

    /// <summary>Counts a run: the memories it retrieved, and whether it ended well, credited to each of them.</summary>
    public void Credit(IEnumerable<string> retrieved, bool endedWell)
    {
        Runs++;
        if (endedWell) Good++;
        foreach (var memory in retrieved.Distinct(StringComparer.Ordinal))
        {
            if (!Memories.TryGetValue(memory, out var use)) Memories[memory] = use = new MemoryUse();
            use.Shown++;
            if (endedWell) use.Good++;
        }
    }

    public MemoryUse? UseOf(string memory) => Memories.GetValueOrDefault(memory);

    public MemoryStanding StandingOf(string memory)
    {
        if (!Memories.TryGetValue(memory, out var use) || use.Shown == 0) return MemoryStanding.Unmet;
        if (use.Shown >= PruneAfter && use.Good == 0) return MemoryStanding.Pruned;
        if (use.Shown >= PromoteAfter && use.Rate > BaseRate) return MemoryStanding.Promoted;
        return MemoryStanding.Kept;
    }

    /// <summary>The memories to show, in order: the promoted ones first (the best rate first, then as given), then the rest as given; the pruned ones left out.</summary>
    public IReadOnlyList<string> Order(IEnumerable<string> memories)
    {
        var given = memories.Select((memory, index) => (Memory: memory, Index: index, Standing: StandingOf(memory))).ToList();
        var promoted = given.Where(m => m.Standing == MemoryStanding.Promoted).OrderByDescending(m => Memories[m.Memory].Rate).ThenBy(m => m.Index);
        var rest = given.Where(m => m.Standing is MemoryStanding.Kept or MemoryStanding.Unmet).OrderBy(m => m.Index);
        return promoted.Concat(rest).Select(m => m.Memory).ToList();
    }

    /// <summary>The credit, for a person: the base rate, then every memory with its standing, the best rate first.</summary>
    public string Render()
    {
        var ic = CultureInfo.InvariantCulture;
        var lines = new List<string> { string.Create(ic, $"  {Runs} run{(Runs == 1 ? "" : "s")}, {Good} ended well (base rate {BaseRate:0.00}); pruned after {PruneAfter} retrievals without one, promoted from {PromoteAfter} above the base rate") };
        foreach (var (memory, use) in Memories.OrderByDescending(kv => kv.Value.Rate).ThenByDescending(kv => kv.Value.Shown).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            lines.Add(string.Create(ic, $"  {StandingOf(memory).ToString().ToLowerInvariant(),-9}{use.Good,3} of {use.Shown,-3} {memory}"));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
