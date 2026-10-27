using System.Text.RegularExpressions;

namespace Creatura.Cells;

/// <summary>
/// Turns a prompt into a goal line fit for the ledger: pasted blocks removed, the first non-empty line kept, anything
/// shaped like a secret replaced by <c>[redacted]</c>, whitespace collapsed, at most <see cref="MaxGoal"/> characters.
/// The filter is deliberately greedy: a goal that loses a long identifier is a smaller loss than a ledger that carries
/// a key to every vessel it syncs with.
/// </summary>
public static partial class Scrubber
{
    public const int MaxGoal = 100;

    public const string Redacted = "[redacted]";

    /// <summary>The goal line for a prompt, or null when nothing readable is left.</summary>
    public static string? GoalFromPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var withoutPasted = Pasted().Replace(prompt, " ");
        var line = withoutPasted.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;
        var scrubbed = Whitespace().Replace(Scrub(line), " ").Trim();
        if (scrubbed.Length == 0) return null;
        return scrubbed.Length <= MaxGoal ? scrubbed : scrubbed[..(MaxGoal - 1)].TrimEnd() + "…";
    }

    /// <summary>The text with every secret-shaped run replaced.</summary>
    public static string Scrub(string text)
    {
        var s = PrivateKeyBlock().Replace(text, Redacted);
        s = UrlCredentials().Replace(s, m => m.Groups[1].Value + Redacted + "@");
        s = Bearer().Replace(s, "Bearer " + Redacted);
        s = KnownTokens().Replace(s, Redacted);
        s = KeyValue().Replace(s, m => m.Groups[1].Value + "=" + Redacted);
        s = LongRun().Replace(s, Redacted);
        return s;
    }

    /// <summary>A git remote as "owner/name" for the common hosts, otherwise the URL with any credentials removed.</summary>
    public static string? RepoFromRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        var r = remote.Trim();
        var scp = ScpRemote().Match(r);
        if (scp.Success) return scp.Groups[2].Value;
        var url = HttpRemote().Match(r);
        if (url.Success) return url.Groups[2].Value;
        return UrlCredentials().Replace(r, m => m.Groups[1].Value);
    }

    [GeneratedRegex(@"<pasted_content\b[^>]*>.*?</pasted_content\b[^>]*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Pasted();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?(-----END [A-Z0-9 ]*PRIVATE KEY-----|$)", RegexOptions.Singleline)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]*@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"\bbearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(?:sk|pk|rk)-[A-Za-z0-9_-]{8,}|\bgh[pousr]_[A-Za-z0-9]{16,}|\bgithub_pat_[A-Za-z0-9_]{16,}|\bxox[abprs]-[A-Za-z0-9-]{10,}|\bAKIA[0-9A-Z]{16}\b|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex KnownTokens();

    [GeneratedRegex(@"\b(password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|client[_-]?secret)\s*[:=]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();

    // A run of 25+ base64/hex-ish characters holding both a letter and a digit. '/' is left out so paths survive.
    [GeneratedRegex(@"(?=[A-Za-z0-9+=_-]{25,})(?=[A-Za-z0-9+=_-]*[0-9])(?=[A-Za-z0-9+=_-]*[A-Za-z])[A-Za-z0-9+=_-]{25,}")]
    private static partial Regex LongRun();

    [GeneratedRegex(@"^(?:[^@\s]+@)?([^:\s/]+):(?!//)([^\s]+?)(?:\.git)?/?$")]
    private static partial Regex ScpRemote();

    [GeneratedRegex(@"^https?://(?:[^@/\s]+@)?([^/\s]+)/([^\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex HttpRemote();
}
