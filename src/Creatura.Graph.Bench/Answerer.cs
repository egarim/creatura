using System.Globalization;
using System.Text.RegularExpressions;

namespace Creatura.Graph.Bench;

/// <summary>
/// The one answerer every arm is scored by. It reads only the text an arm handed back, in the corpus's own line
/// notation, and answers the question from that and nothing else — so an arm cannot win by parsing better, only by
/// retrieving better. That is the point: arm C hands back graph edges rendered as the same lines arm B hands back
/// raw, which takes structure away as an advantage at answer time and leaves it as an advantage only at retrieval
/// time, where it is the thing under investigation.
/// </summary>
public static partial class Answerer
{
    /// <summary>The retrieval did not surface enough to answer. Distinct from a conflict: absence, not disagreement.</summary>
    public const string Unknown = "unknown";

    /// <summary>The retrieval surfaced more than one answer and nothing to choose between them. Preserving this is the right answer in G04 and G09.</summary>
    public const string Unresolved = "unresolved";

    /// <summary>Nothing supports a cause. The right answer in G08, where the only candidate is a coincidence.</summary>
    public const string NoCause = "none";

    /// <summary>What an action question answers when nothing retrieved holds it back.</summary>
    public const string Proceed = "proceed";

    /// <summary>What an action question answers when a retrieved dependency is unmet.</summary>
    public const string Blocked = "blocked";

    /// <summary>One relationship as the answerer read it out of retrieved text. Public so a test can check what the answerer saw, which is the only way to tell a retrieval failure from an answering one.</summary>
    public sealed record Triple(string Subject, string Relation, string Object, DateTimeOffset? From, DateTimeOffset? Until, VerificationStatus Status)
    {
        public bool HoldsAt(DateTimeOffset at) => (From is null || at >= From) && (Until is null || at < Until);

        public bool Strong => Status >= VerificationStatus.Observed;

        public static readonly IEqualityComparer<Triple> Identity = new TripleIdentity();

        private sealed class TripleIdentity : IEqualityComparer<Triple>
        {
            public bool Equals(Triple? x, Triple? y) =>
                x is not null && y is not null &&
                string.Equals(x.Subject, y.Subject, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Relation, y.Relation, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Object, y.Object, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(Triple t) =>
                HashCode.Combine(t.Subject.ToLowerInvariant(), t.Relation.ToLowerInvariant(), t.Object.ToLowerInvariant());
        }
    }

    /// <summary>Every distinct triple in the retrieved text, in the order it was handed back.</summary>
    public static IReadOnlyList<Triple> Parse(IReadOnlyList<RetrievedItem> items)
    {
        var found = new List<Triple>();
        foreach (var item in items)
        {
            if (Line().Match(item.Text) is not { Success: true } m) continue;
            var from = Window(m.Groups["valid"].Value, out var until);
            found.Add(new Triple(
                m.Groups["subject"].Value.Trim(),
                m.Groups["relation"].Value.Trim(),
                m.Groups["object"].Value.Trim(),
                from,
                until,
                LineExtractor.ParseStatus(m.Groups["status"].Success ? m.Groups["status"].Value : null)));
        }

        return found.Distinct(Triple.Identity).ToList();
    }

    private static DateTimeOffset? Window(string valid, out DateTimeOffset? until)
    {
        until = null;
        if (string.IsNullOrWhiteSpace(valid)) return null;
        var parts = valid.Split("..", 2);
        if (parts.Length == 2)
        {
            var from = Stamp(parts[0]);
            until = Stamp(parts[1]);
            if (from is not null || until is not null) return from;
        }

        return Stamp(valid);
    }

    private static DateTimeOffset? Stamp(string text) =>
        string.IsNullOrWhiteSpace(text) || !TryStamp(text, out var at) ? null : at;

    private static bool TryStamp(string text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at);

    /// <summary>The answer to a question, from retrieved text alone.</summary>
    public static string? Solve(Question question, IReadOnlyList<RetrievedItem> items)
    {
        var triples = Parse(items);
        return question.Kind switch
        {
            QuestionKind.Chain => Chain(triples, question),
            QuestionKind.Resolve => Resolve(triples, question.Start, question.Relations.FirstOrDefault() ?? "", question.At),
            QuestionKind.AtTime => Resolve(triples, question.Start, question.Relations.FirstOrDefault() ?? "", question.At),
            QuestionKind.Choose => Choose(triples, question),
            QuestionKind.MayProceed => MayProceed(triples, question),
            QuestionKind.Cause => Cause(triples, question),
            QuestionKind.Synthesize => Synthesize(triples, question),
            _ => Unknown,
        };
    }

    /// <summary>Follows the relationships in order. Any hop with no answer ends the chain; any hop with two answers does not guess between them.</summary>
    private static string Chain(IReadOnlyList<Triple> triples, Question question)
    {
        var cursor = question.Start;
        foreach (var relation in question.Relations)
        {
            switch (Objects(triples, cursor, relation, question.At))
            {
                case { Count: 0 }: return Unknown;
                case { Count: 1 } one: cursor = one[0]; break;
                default: return Unresolved;
            }
        }

        return cursor;
    }

    private static string Resolve(IReadOnlyList<Triple> triples, string subject, string relation, DateTimeOffset? at) =>
        Objects(triples, subject, relation, at) switch
        {
            { Count: 0 } => Unknown,
            { Count: 1 } one => one[0],
            _ => Unresolved,
        };

    private static IReadOnlyList<string> Objects(IReadOnlyList<Triple> triples, string subject, string relation, DateTimeOffset? at)
    {
        var found = triples
            .Where(t => string.Equals(t.Subject, subject, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.Relation, relation, StringComparison.OrdinalIgnoreCase)
                        && (at is null || t.HoldsAt(at.Value)))
            .Select(t => t.Object)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A strongly supported answer wins over a mere claim, but only when there is exactly one of them: two
        // observations that disagree are a conflict to report, not a choice to make quietly.
        if (found.Count > 1)
        {
            var strong = triples
                .Where(t => t.Strong
                            && string.Equals(t.Subject, subject, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(t.Relation, relation, StringComparison.OrdinalIgnoreCase)
                            && (at is null || t.HoldsAt(at.Value)))
                .Select(t => t.Object)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (strong.Count >= 1) return strong;
        }

        return found.OrderBy(o => o, StringComparer.Ordinal).ToList();
    }

    /// <summary>The first candidate the retrieved knowledge links to what was asked for, or <see cref="Unknown"/> when none is.</summary>
    private static string Choose(IReadOnlyList<Triple> triples, Question question)
    {
        foreach (var option in question.Options)
        {
            var provides = Objects(triples, option, question.Requires, question.At);
            var providedBy = Objects(triples, question.Start, question.Requires, question.At);
            if (provides.Contains(question.Start, StringComparer.OrdinalIgnoreCase)
                || providedBy.Contains(option, StringComparer.OrdinalIgnoreCase))
                return option;
        }

        return Unknown;
    }

    /// <summary>
    /// Whether an action may run. "Proceed" only means nothing retrieved holds it back, which is why the arm with no
    /// memory answers it wrongly: it deploys early because it never heard about the migration. That failure is the
    /// measurement, not a flaw in the question.
    /// </summary>
    private static string MayProceed(IReadOnlyList<Triple> triples, Question question)
    {
        foreach (var blocker in Objects(triples, question.Start, "blockedBy", question.At))
        {
            var satisfied = Objects(triples, blocker, "status", question.At);
            if (!satisfied.Contains("done", StringComparer.OrdinalIgnoreCase)) return Blocked;
        }

        return Proceed;
    }

    /// <summary>The verified cause, or none. A relationship in time is not a candidate, because it is not a cause.</summary>
    private static string Cause(IReadOnlyList<Triple> triples, Question question)
    {
        var causes = triples
            .Where(t => string.Equals(t.Relation, question.Relations.FirstOrDefault() ?? "caused", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.Object, question.Start, StringComparison.OrdinalIgnoreCase)
                        && t.Strong)
            .Select(t => t.Subject)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        return causes.Count == 1 ? causes[0] : NoCause;
    }

    private static string Synthesize(IReadOnlyList<Triple> triples, Question question)
    {
        var supported = question.Themes.Where(theme => triples.Any(t =>
            Contains(t.Subject, theme) || Contains(t.Relation, theme) || Contains(t.Object, theme)));
        return supported.Count() == 0 ? Unknown : string.Join(",", supported.OrderBy(s => s, StringComparer.Ordinal));

        static bool Contains(string text, string theme) => text.Contains(theme, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^(?<subject>.+?)\s*->\s*(?<relation>.+?)\s*->\s*(?<object>[^@\[!]+?)\s*(?:@(?<valid>[^\s\[]+))?\s*(?:\[(?<corpus>[^\]]+)\])?\s*(?:!(?<status>[a-z]+))?\s*$")]
    private static partial Regex Line();
}
