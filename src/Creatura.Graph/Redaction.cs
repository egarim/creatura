using System.Text.RegularExpressions;
using Creatura.Cells;

namespace Creatura.Graph;

/// <summary>
/// What the graph keeps of free text. Redaction is applied on the way in, not on the way out, because knowledge that
/// held a secret has already leaked to every reader, export and snapshot by the time anybody asks. The filter is
/// <see cref="Scrubber.Scrub"/>, the ledger's: this repository already has three redactors and a fourth would be a
/// fourth place to get wrong, and the ledger's is the one whose policy is written down ("deliberately greedy: a goal
/// that loses a long identifier is a smaller loss than a ledger that carries a key to every vessel it syncs with").
/// </summary>
public static partial class GraphRedaction
{
    /// <summary>
    /// Free text as the graph keeps it: secrets replaced, then bounded. Applied to summaries, attribute values and
    /// evidence — never to an id, an entity type or a relation name, because those are what a traversal matches on and
    /// a redacted identifier is a broken edge. That asymmetry is a decision, not an oversight: a long hash used as a
    /// node id will be left alone, and a long hash in a summary will not be.
    /// </summary>
    public static string Text(string? text, int max = GraphBounds.MaxTextLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var scrubbed = Scrubber.Scrub(text);
        return scrubbed.Length <= max ? scrubbed : scrubbed[..max];
    }

    /// <summary>One line a seat reads: redacted, and cut shorter than an attribute because it goes into a prompt.</summary>
    public static string? Summary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var text = Text(summary, GraphBounds.MaxSummaryLength);
        return text.Length == 0 ? null : text;
    }

    public static string? Evidence(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return null;
        var text = Text(evidence);
        return text.Length == 0 ? null : text;
    }

    /// <summary>Every value of an attribute set, redacted; the keys are identifiers and are left alone.</summary>
    public static IReadOnlyDictionary<string, string> Attributes(IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null || attributes.Count == 0) return NoAttributes;
        var clean = new Dictionary<string, string>(attributes.Count, StringComparer.Ordinal);
        foreach (var (key, value) in attributes) clean[key] = Text(value);
        return clean;
    }

    public static IReadOnlyList<string> Lines(IReadOnlyList<string>? lines) =>
        lines is null || lines.Count == 0
            ? NoLines
            : [.. lines.Select(l => Text(l)).Where(l => l.Length > 0)];

    /// <summary>
    /// Whether an identifier is shaped like a credential. Identifiers are not redacted, because a redacted id is a
    /// broken edge and a traversal that cannot match its own nodes is worse than no graph at all; they are refused
    /// instead. Only the high-precision shapes are refused — the token prefixes, key blocks and <c>key=value</c> pairs
    /// <see cref="Scrubber"/> knows — and deliberately not its greedy long-run rule, which would refuse an ordinary
    /// long slug such as <c>service-orders-v2-production-shard-01</c>. The cost is a false refusal on an id that
    /// merely begins with a word like <c>token:</c>; a refusal that is reported is a cheaper mistake than a key that is
    /// stored, exported and synced.
    /// </summary>
    public static bool LooksLikeSecret(string? identifier) =>
        !string.IsNullOrEmpty(identifier) && SecretShaped().IsMatch(identifier);

    /// <summary>An identifier fit to put in a refusal and an audit line: the secret taken out of it first.</summary>
    public static string SafeId(string? identifier) =>
        string.IsNullOrEmpty(identifier) ? "" : Text(identifier, GraphBounds.MaxSummaryLength);

    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyList<string> NoLines = [];

    [GeneratedRegex("""
                    -----BEGIN [A-Z0-9 ]*PRIVATE KEY-----
                    |\b(?:sk|pk|rk)-[A-Za-z0-9_-]{8,}
                    |\bgh[pousr]_[A-Za-z0-9]{16,}
                    |\bgithub_pat_[A-Za-z0-9_]{16,}
                    |\bxox[abprs]-[A-Za-z0-9-]{10,}
                    |\bAKIA[0-9A-Z]{16}\b
                    |\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+
                    |[a-z][a-z0-9+.-]*://[^/\s:@]+:[^/\s@]*@
                    |\b(?:password|passwd|pwd|secret|api[_-]?key|apikey|access[_-]?key|client[_-]?secret)\s*[:=]\s*\S+
                    |\bbearer\s+[A-Za-z0-9._~+/=-]+
                    """, RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase)]
    private static partial Regex SecretShaped();
}

/// <summary>
/// The synthetic secrets the redaction is tested against (issue #109, "use synthetic secret canaries to verify
/// exclusion from graph storage, exports, logs and traces"). Every one of them is a shape
/// <see cref="Scrubber.Scrub"/> claims to catch, and none of them is a real credential: a canary that was a real key
/// would be a leak in the repository that was supposed to prove there are none. They live beside the redactor rather
/// than in the tests so that an export path, a fixture and a test all assert against the same list.
/// </summary>
public static class SecretCanary
{
    /// <summary>An OpenAI-shaped key.</summary>
    public const string ApiKey = "sk-canaryD0NOTSTORE000000000000";

    /// <summary>A GitHub personal access token.</summary>
    public const string GitHubToken = "github_pat_CANARY00000000000000000000";

    /// <summary>An AWS access key id: AKIA and sixteen uppercase alphanumerics, which is the shape the redactor matches.</summary>
    public const string AwsKey = "AKIACANARY0000000000";

    /// <summary>A key=value pair naming itself a password.</summary>
    public const string KeyValue = "password=canary-hunter2";

    /// <summary>A bearer header.</summary>
    public const string Bearer = "Bearer canary.aaaaaaaaaaaaaaaa";

    /// <summary>Credentials inside a URL.</summary>
    public const string Url = "https://canary-user:canary-pass@example.invalid/repo.git";

    /// <summary>A private key block.</summary>
    public const string PrivateKey = "-----BEGIN PRIVATE KEY-----\nCANARYDO_NOT_STORE\n-----END PRIVATE KEY-----";

    /// <summary>Every canary with the name it fails under, so the two cannot drift apart and a new one is covered by every test that walks this list.</summary>
    public static readonly IReadOnlyList<(string Name, string Value)> All =
    [
        ("api key", ApiKey),
        ("github token", GitHubToken),
        ("aws key", AwsKey),
        ("key=value", KeyValue),
        ("bearer", Bearer),
        ("url credentials", Url),
        ("private key", PrivateKey),
    ];

    /// <summary>Whether any canary survives in a piece of text. The assertion every export, log and trace should be able to pass.</summary>
    public static bool AppearsIn(string? text) => FoundIn(text).Count > 0;

    /// <summary>The canaries that survived, named — so a failure says which shape the redactor missed rather than only that it missed one.</summary>
    public static IReadOnlyList<string> FoundIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return [.. All.Where(c => text.Contains(c.Value, StringComparison.Ordinal)).Select(c => c.Name)];
    }
}
