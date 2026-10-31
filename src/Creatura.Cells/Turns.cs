using System.Text.Json.Nodes;

namespace Creatura.Cells;

/// <summary>What a party says to a proposal (docs/design-turns.md): yes, or no and why.</summary>
public enum AnswerVerdict
{
    Accept,
    Object,
}

/// <summary>What <c>creatura cell turn</c> tells a cell: its move, or not yet.</summary>
public enum Move
{
    Act,
    Wait,
}

/// <summary>
/// A wait that stands: the Progress entry that says it (<see cref="ProgressPayload.WaitingOn"/>), what it waits on, a
/// cell id or an entry id, and the cell that comes to. It stands until its cell's next entry that is not a wait.
/// </summary>
public sealed record StandingWait(SignedEntry Entry, string On, string OnCell)
{
    public string Cell => Entry.Cell;
}

/// <summary>A proposal on a topic: its entry and payload, and its hash, by which competing proposals are settled.</summary>
public sealed record Proposal(SignedEntry Entry, ProgressPayload Payload)
{
    public string Id => Entry.Id;

    public string Cell => Entry.Cell;

    public string Topic => Payload.Topic ?? "";

    /// <summary>The proposal entry ids on the topic its cell held when it proposed.</summary>
    public IReadOnlyList<string> Seen => Payload.Seen ?? [];

    /// <summary>The cells it names as its parties, when its cell named any.</summary>
    public IReadOnlyList<string> With => Payload.With ?? [];

    /// <summary>The entry's hash (<see cref="SignedEntry.Hash"/>): among competitors the lowest wins, in every home alike, whatever the clocks said.</summary>
    public string Hash { get; } = Entry.Hash();
}

/// <summary>
/// Where a topic stands, as one cell's <c>turn</c> sees it: the live proposal (the one being answered), the competitors
/// it was picked from when there were any, who has to answer it and who has, and whether it is settled; and the plan,
/// the latest proposal every party accepted, which a counter-proposal replaces only once it is accepted itself.
/// </summary>
public sealed record TopicState(
    string Topic,
    Proposal Live,
    IReadOnlyList<Proposal> Competitors,
    IReadOnlyList<string> Parties,
    IReadOnlyList<string> Accepted,
    IReadOnlyList<string> Objected,
    IReadOnlyList<string> Unanswered,
    IReadOnlyList<string> Silent,
    bool Settled,
    bool BySilence,
    Proposal? Plan);

/// <summary>
/// The answer of <c>creatura cell turn</c>: act or wait, the rule that decided it (T1 to T7 of docs/design-turns.md),
/// one reason, this cell's standing wait if any, and the topics it is a party to.
/// </summary>
public sealed record TurnResult(Move Move, string Rule, string Reason, StandingWait? Waiting, IReadOnlyList<TopicState> Topics)
{
    /// <summary>0 to act, 3 to wait, so a script can loop on it.</summary>
    public int ExitCode => Move == Move.Act ? 0 : 3;

    /// <summary>The same as an object: <c>{ "move": "act"|"wait", "rule": "T3", "reason": ..., "waiting": ..., "topics": [...] }</c>.</summary>
    public string ToJson()
    {
        var topics = new JsonArray();
        foreach (var t in Topics)
        {
            var topic = new JsonObject
            {
                ["topic"] = t.Topic,
                ["proposal"] = t.Live.Id,
                ["by"] = t.Live.Cell,
                ["row"] = t.Live.Payload.Row,
                ["competitors"] = Ids(t.Competitors.Select(c => c.Id)),
                ["parties"] = Ids(t.Parties),
                ["accepted"] = Ids(t.Accepted),
                ["objected"] = Ids(t.Objected),
                ["unanswered"] = Ids(t.Unanswered),
                ["silent"] = Ids(t.Silent),
                ["settled"] = t.Settled,
                ["bySilence"] = t.BySilence,
                ["plan"] = t.Plan?.Id,
            };
            if (t.Live.Payload.Artifact is { } artifact) topic["artifact"] = artifact;
            topics.Add(topic);
        }

        var result = new JsonObject
        {
            ["move"] = Move.ToString().ToLowerInvariant(),
            ["rule"] = Rule,
            ["reason"] = Reason,
            ["waiting"] = Waiting is null ? null : new JsonObject { ["entry"] = Waiting.Entry.Id, ["on"] = Waiting.On, ["cell"] = Waiting.OnCell },
            ["topics"] = topics,
        };
        return result.ToJsonString();
    }

    private static JsonArray Ids(IEnumerable<string> ids) => new([.. ids.Select(id => (JsonNode?)JsonValue.Create(id))]);
}

/// <summary>
/// Turns (docs/design-turns.md): waits, proposals and answers are optional fields on Progress entries, and
/// <see cref="Turn"/> reads the verified ledger and says whose move it is, the same in every home that holds the same
/// entries. Nothing here assigns roles or splits work; it only makes waiting and proposing visible and settles who
/// moves, with no clock deciding who wins: competing proposals are settled by entry hash, a cycle of waits by the
/// latest wait in ledger order. A cell's own clock decides only how long a peer has been quiet, which changes when a
/// cell may act, never who wins.
/// </summary>
public static class TurnLedger
{
    /// <summary>The quiet time unless an offer or <c>--quiet</c> says otherwise: a peer that wrote nothing for this long is not waited for.</summary>
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMinutes(10);

    /// <summary>A wait on a cell or an entry, as a step of the claim.</summary>
    public static ProgressPayload Wait(string claim, string on, string why) => new(claim, why) { WaitingOn = on };

    /// <summary>An answer to a proposal, as a step of the claim.</summary>
    public static ProgressPayload Answer(string claim, string proposal, AnswerVerdict verdict, string row) => new(claim, row) { Answers = proposal, Verdict = verdict };

    /// <summary>
    /// A proposal on a topic, as a step of the claim, with <see cref="ProgressPayload.Seen"/> filled from this ledger:
    /// the proposal entry ids on the topic it holds now. Two proposals that saw nothing of each other are competitors;
    /// one that saw the other is a counter-proposal.
    /// </summary>
    public static ProgressPayload Propose(this WorkLedger ledger, string claim, string topic, string row, IEnumerable<string>? with = null, string? artifact = null)
    {
        var name = topic.Trim();
        if (name.Length == 0) throw new ArgumentException("a proposal needs a topic", nameof(topic));
        var named = with?.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new ProgressPayload(claim, row)
        {
            Topic = name,
            Seen = ledger.ProposalsOn(name).Select(p => p.Id).ToList(),
            With = named is { Count: > 0 } ? named : null,
            Artifact = artifact,
        };
    }

    /// <summary>The cell an id names: itself for a cell id, the writer for an entry id ("cell#sequence").</summary>
    public static string CellOf(string id) => id.IndexOf('#') is var i and >= 0 ? id[..i] : id;

    public static bool IsWait(SignedEntry entry) => entry.Kind == EntryKind.Progress && entry.Body<ProgressPayload>().WaitingOn is not null;

    public static bool IsProposal(SignedEntry entry) => entry.Kind == EntryKind.Progress && entry.Body<ProgressPayload>() is { Topic: not null, Answers: null };

    public static bool IsAnswer(SignedEntry entry) => entry.Kind == EntryKind.Progress && entry.Body<ProgressPayload>().Answers is not null;

    /// <summary>
    /// Each cell's standing wait: its latest entry that is not a heartbeat, when that entry is a wait. A later entry of
    /// any other kind ends it. A heartbeat says the vessel is alive, not that the cell moved, so it neither ends a wait
    /// nor counts as writing for the quiet time.
    /// </summary>
    public static IReadOnlyDictionary<string, StandingWait> StandingWaits(this WorkLedger ledger)
    {
        var waits = new Dictionary<string, StandingWait>(StringComparer.Ordinal);
        foreach (var group in ledger.Entries.Where(e => e.Kind != EntryKind.Heartbeat).GroupBy(e => e.Cell))
        {
            var latest = group.MaxBy(e => e.Sequence)!;
            if (latest.Kind == EntryKind.Progress && latest.Body<ProgressPayload>().WaitingOn is { } on) waits[group.Key] = new StandingWait(latest, on, CellOf(on));
        }
        return waits;
    }

    /// <summary>When the cell last wrote anything but a heartbeat, by its own clock; null when this ledger holds nothing of its.</summary>
    public static DateTimeOffset? LastWriteOf(this WorkLedger ledger, string cell) =>
        ledger.Entries.Where(e => e.Cell == cell && e.Kind != EntryKind.Heartbeat).Select(e => (DateTimeOffset?)e.Timestamp).Max();

    /// <summary>
    /// The cells this one is on a goal with: the cells with an active claim whose goal text is its active claim's,
    /// which is what taking an offer gives them (a claim that takes an offer carries its goal), and the cells a
    /// proposal names with <c>--with</c>, by this cell or naming it. The builder's choice for the note's "Parties".
    /// </summary>
    public static IReadOnlySet<string> PartiesOf(this WorkLedger ledger, string cell)
    {
        var parties = new HashSet<string>(StringComparer.Ordinal);
        var entries = ledger.Entries;
        if (ledger.LatestActiveClaimOf(cell) is { } mine)
        {
            var goal = GoalOf(mine);
            foreach (var claim in entries.Where(e => e.Kind == EntryKind.Claim && e.Cell != cell && GoalOf(e) == goal && ledger.StateOf(e.Id) == ClaimState.Active))
            {
                parties.Add(claim.Cell);
            }
        }

        foreach (var entry in entries.Where(e => e.Kind == EntryKind.Progress))
        {
            var progress = entry.Body<ProgressPayload>();
            if (progress.Topic is null || progress.Answers is not null || progress.With is not { Count: > 0 } named) continue;
            if (entry.Cell == cell)
            {
                parties.UnionWith(named);
            }
            else if (named.Contains(cell, StringComparer.Ordinal))
            {
                parties.Add(entry.Cell);
                parties.UnionWith(named);
            }
        }

        parties.Remove(cell);
        return parties;
    }

    /// <summary>The proposals on a topic this ledger holds, by any cell, in ledger order: what a proposer has seen.</summary>
    public static IReadOnlyList<Proposal> ProposalsOn(this WorkLedger ledger, string topic) => ledger.Entries
        .Where(e => e.Kind == EntryKind.Progress)
        .Select(e => (Entry: e, Payload: e.Body<ProgressPayload>()))
        .Where(p => p.Payload.Answers is null && string.Equals(p.Payload.Topic, topic, StringComparison.Ordinal))
        .Select(p => new Proposal(p.Entry, p.Payload))
        .ToList();

    /// <summary>The quiet time the owner set in the offer this cell's active claim takes, or null when it takes none or the offer set none.</summary>
    public static TimeSpan? QuietOf(this WorkLedger ledger, string cell) =>
        ledger.LatestActiveClaimOf(cell)?.Body<ClaimPayload>().Offer is { } offer && ledger.OfferState(offer)?.Offer.Quiet is { } minutes ? TimeSpan.FromMinutes(minutes) : null;

    /// <summary>
    /// The topics this cell is a party to, each with its live proposal. On a topic, the proposals nobody's proposal
    /// has seen are the frontier; among them (competitors, when more than one) the lowest hash is live. A proposal that
    /// saw an earlier one is a counter: it takes the earlier one out of the frontier, and replaces it as the plan only
    /// once every other party accepts it. A party's latest answer to the live proposal counts; a party that has not
    /// answered and has written nothing for the quiet time is silent.
    /// </summary>
    public static IReadOnlyList<TopicState> Topics(this WorkLedger ledger, string cell, DateTimeOffset now, TimeSpan quiet)
    {
        var scope = new HashSet<string>(ledger.PartiesOf(cell), StringComparer.Ordinal) { cell };
        var progress = ledger.Entries.Where(e => e.Kind == EntryKind.Progress).Select(e => (Entry: e, Payload: e.Body<ProgressPayload>())).ToList();
        var proposals = progress.Where(p => p.Payload is { Topic: not null, Answers: null } && scope.Contains(p.Entry.Cell)).Select(p => new Proposal(p.Entry, p.Payload)).ToList();
        if (proposals.Count == 0) return [];

        // Each party's latest answer to each proposal, by that party's own sequence.
        var answers = new Dictionary<(string Cell, string Proposal), (AnswerVerdict Verdict, SignedEntry Entry)>();
        foreach (var (entry, payload) in progress.Where(p => p.Payload.Answers is not null).OrderBy(p => p.Entry.Sequence))
        {
            answers[(entry.Cell, payload.Answers!)] = (payload.Verdict ?? AnswerVerdict.Object, entry);
        }

        var parties = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        IReadOnlyList<string> PartiesOf(Proposal p)
        {
            if (!parties.TryGetValue(p.Cell, out var of)) parties[p.Cell] = of = ledger.PartiesOf(p.Cell);
            return of.Union(p.With).Where(c => c != p.Cell).Order(StringComparer.Ordinal).ToList();
        }

        bool Accepted(Proposal p, string party) => answers.TryGetValue((party, p.Id), out var a) && a.Verdict == AnswerVerdict.Accept;
        bool Quiet(string other, DateTimeOffset since) => now - (ledger.LastWriteOf(other) ?? since) > quiet;

        var topics = new List<TopicState>();
        foreach (var group in proposals.GroupBy(p => p.Topic, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var all = group.ToList();
            var seen = all.SelectMany(p => p.Seen).ToHashSet(StringComparer.Ordinal);
            var frontier = all.Where(p => !seen.Contains(p.Id)).ToList();
            if (frontier.Count == 0) frontier = all;
            var competitors = frontier.Count > 1 ? frontier.OrderBy(p => p.Hash, StringComparer.Ordinal).ToList() : [];
            var live = frontier.MinBy(p => p.Hash, StringComparer.Ordinal)!;

            var of = PartiesOf(live);
            var accepted = of.Where(party => Accepted(live, party)).ToList();
            var objected = of.Where(party => answers.TryGetValue((party, live.Id), out var a) && a.Verdict == AnswerVerdict.Object).ToList();
            var unanswered = of.Where(party => !answers.ContainsKey((party, live.Id))).ToList();
            var silent = unanswered.Where(party => Quiet(party, live.Entry.Timestamp)).ToList();
            var settled = unanswered.Count == 0 && objected.Count == 0;
            var bySilence = !settled && objected.Count == 0 && unanswered.All(silent.Contains);
            var plan = all.LastOrDefault(p => PartiesOf(p).All(party => Accepted(p, party)));
            topics.Add(new TopicState(group.Key, live, competitors, of, accepted, objected, unanswered, silent, settled, bySilence, plan));
        }
        return topics;
    }

    /// <summary>
    /// Whose move it is, for this cell, from the verified ledger alone (docs/design-turns.md, the rules T1 to T7 in
    /// order; the first that applies wins). <paramref name="now"/> is this cell's own clock, and decides only how long
    /// a peer has been quiet; <paramref name="quiet"/> overrides the offer's quiet time and the default.
    /// </summary>
    public static TurnResult Turn(this WorkLedger ledger, string cell, DateTimeOffset now, TimeSpan? quiet = null)
    {
        var quietTime = quiet ?? ledger.QuietOf(cell) ?? DefaultQuiet;
        var topics = ledger.Topics(cell, now, quietTime);
        var waits = ledger.StandingWaits();
        var mine = waits.GetValueOrDefault(cell);
        TurnResult Result(Move move, string rule, string reason) => new(move, rule, reason, mine, topics);
        bool Quiet(string other, DateTimeOffset since) => now - (ledger.LastWriteOf(other) ?? since) > quietTime;
        string Ago(string other, DateTimeOffset since) => Describe(now - (ledger.LastWriteOf(other) ?? since));
        string Objection(TopicState t, string party) => ledger.Entries
            .Where(e => e.Cell == party && e.Kind == EntryKind.Progress).Select(e => e.Body<ProgressPayload>())
            .LastOrDefault(p => p.Answers == t.Live.Id && p.Verdict == AnswerVerdict.Object)?.Row ?? "";

        // T1: a proposal from another party is waiting for this cell's answer. A competing proposal of this cell's
        // that lost is answered the same way (T6, the losers).
        foreach (var t in topics)
        {
            if (t.Live.Cell == cell || !t.Unanswered.Contains(cell)) continue;
            var lost = t.Competitors.FirstOrDefault(p => p.Cell == cell);
            return lost is null
                ? Result(Move.Act, "T1", $"answer {t.Live.Id} on '{t.Topic}' by {t.Live.Cell}: {t.Live.Payload.Row}")
                : Result(Move.Act, "T6", $"your proposal {lost.Id} on '{t.Topic}' lost to {t.Live.Id} by {t.Live.Cell} (the lower hash): answer it");
        }

        // T2: this cell's proposal was objected to.
        foreach (var t in topics)
        {
            if (t.Live.Cell != cell || t.Objected.Count == 0) continue;
            return Result(Move.Act, "T2", $"{Join(t.Objected)} objected to {t.Live.Id} on '{t.Topic}': {Objection(t, t.Objected[0])}; revise or counter");
        }

        if (mine is not null)
        {
            // T3: a cycle in the wait-for graph through this cell: the latest wait in ledger order moves (ties: the higher hash).
            if (CycleThrough(cell, waits) is { } cycle)
            {
                var mover = cycle.Select(c => waits[c]).OrderBy(w => w.Entry.Timestamp).ThenBy(w => w.Entry.Hash(), StringComparer.Ordinal).Last().Cell;
                var chain = string.Join(", ", cycle.Select((c, i) => $"{c} waits on {cycle[(i + 1) % cycle.Count]}"));
                return mover == cell
                    ? Result(Move.Act, "T3", $"break the deadlock: {chain}; your wait is the latest")
                    : Result(Move.Wait, "T3", $"deadlock: {chain}; {mover} has the move, its wait is the latest");
            }

            // T4: the cell this one waits on has written nothing for longer than the quiet time. T5: it is moving.
            var peer = mine.OnCell;
            if (Quiet(peer, mine.Entry.Timestamp))
            {
                return Result(Move.Act, "T4", $"the peer is quiet: {peer} wrote nothing for {Ago(peer, mine.Entry.Timestamp)}, longer than the quiet time of {Describe(quietTime)}");
            }
            return Result(Move.Wait, "T5", $"waiting on {mine.On}: {peer} last wrote {Ago(peer, mine.Entry.Timestamp)} ago");
        }

        // T6: this cell's proposal is not settled: it waits for the answers, or proceeds once the unanswered are silent;
        // and a party that objected waits for the proposer, who has the move, unless the proposer is quiet.
        foreach (var t in topics)
        {
            if (t.Live.Cell == cell && !t.Settled)
            {
                return t.BySilence
                    ? Result(Move.Act, "T6", $"proceed on '{t.Topic}': {t.Live.Id} is settled by silence, {Join(t.Silent)} wrote nothing for longer than the quiet time of {Describe(quietTime)}")
                    : Result(Move.Wait, "T6", $"{t.Live.Id} on '{t.Topic}' waits for {Join(t.Unanswered)} to answer");
            }
            if (t.Live.Cell != cell && t.Objected.Contains(cell))
            {
                return Quiet(t.Live.Cell, t.Live.Entry.Timestamp)
                    ? Result(Move.Act, "T6", $"{t.Live.Cell} is quiet after your objection to {t.Live.Id} on '{t.Topic}': it wrote nothing for {Ago(t.Live.Cell, t.Live.Entry.Timestamp)}")
                    : Result(Move.Wait, "T6", $"you objected to {t.Live.Id} on '{t.Topic}'; {t.Live.Cell} has the move");
            }
        }

        // T7: otherwise.
        var onMe = waits.Values.Where(w => w.OnCell == cell).Select(w => w.Cell).Order(StringComparer.Ordinal).ToList();
        return Result(Move.Act, "T7", onMe.Count == 0 ? "your move: nothing waits on you" : $"your move: {Join(onMe)} wait{(onMe.Count == 1 ? "s" : "")} on you");
    }

    /// <summary>The cells on the cycle of waits that passes through this cell, starting with it, or null when there is none.</summary>
    private static List<string>? CycleThrough(string cell, IReadOnlyDictionary<string, StandingWait> waits)
    {
        var path = new List<string> { cell };
        var current = cell;
        while (waits.TryGetValue(current, out var wait))
        {
            var next = wait.OnCell;
            if (next == cell) return path;
            if (path.Contains(next)) return null;
            path.Add(next);
            current = next;
        }
        return null;
    }

    private static string GoalOf(SignedEntry claim) => claim.Body<ClaimPayload>().Goal.Trim();

    private static string Join(IEnumerable<string> cells) => string.Join(", ", cells);

    /// <summary>A span in words, for a reason: "45 s", "12 min", "2 h 5 min".</summary>
    public static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return string.Create(TraceText.Culture, $"{(int)span.TotalSeconds} s");
        if (span.TotalMinutes < 60) return string.Create(TraceText.Culture, $"{(int)span.TotalMinutes} min");
        return string.Create(TraceText.Culture, $"{(int)span.TotalHours} h {span.Minutes} min");
    }
}
