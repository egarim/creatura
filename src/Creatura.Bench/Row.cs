using System.Globalization;
using System.Text;

namespace Creatura.Bench;

/// <summary>What one tick of a row's <see cref="Row.Ticks"/> is: a loop tick, or one model call on a host that ticks per call.</summary>
public enum TickUnit
{
    LoopTick,
    ModelCall,
}

/// <summary>
/// One run of one agent under one arm: the row every battery appends. Its columns are the union of the two CSV shapes the
/// research runners have written since the first battery (the batteries' <c>results.csv</c> and the 26-column scoreboard),
/// plus the tick unit, the series counts, and the call log's path (never a prompt's contents). <see cref="ToLegacyCsv"/>
/// writes either legacy shape byte for byte; <see cref="ParseLegacyCsv"/> reads one back. Aggregate over rows, never quote one.
/// </summary>
public sealed record Row
{
    public DateTimeOffset At { get; init; }

    public string Battery { get; init; } = "";

    public string Test { get; init; } = "";

    /// <summary>The seat: <c>bfs</c> or the model's name (the scoreboard's <c>model</c> column).</summary>
    public string Seat { get; init; } = "bfs";

    public string Provider { get; init; } = "";

    /// <summary>How the seat was driven: <c>tools</c> or <c>json</c> for a model; empty for the planner.</summary>
    public string Protocol { get; init; } = "";

    public string World { get; init; } = "";

    public string Agent { get; init; } = "";

    /// <summary>The extensions loaded, as the rows write them: <c>none</c>, or names joined with <c>+</c>.</summary>
    public string Extensions { get; init; } = "none";

    public string Arm { get; init; } = "";

    public int Case { get; init; } = 1;

    public int Repeat { get; init; } = 1;

    public OutcomeClass Outcome { get; init; }

    public int Ticks { get; init; }

    public TickUnit TickUnit { get; init; } = TickUnit.LoopTick;

    public int Calls { get; init; }

    public int Failed { get; init; }

    public int Invalid { get; init; }

    /// <summary>The <c>hand_off</c> column: the outcome class, nothing else.</summary>
    public bool HandOff => Outcome == OutcomeClass.HandedOff;

    public int DeployBlocked { get; init; }

    public int LiesCaught { get; init; }

    public bool ShortcutTaken { get; init; }

    public double FreedomBefore { get; init; }

    public double FreedomAfter { get; init; }

    public int Releases { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public long ReasoningTokens { get; init; }

    public long CachedTokens { get; init; }

    public bool TokensKnown { get; init; }

    public double Seconds { get; init; }

    public double MedianCallSeconds { get; init; }

    public double? TokensPerSecond { get; init; }

    public decimal? CostUsd { get; init; }

    /// <summary>The free-text column (<c>note</c> / <c>notes</c>).</summary>
    public string Note { get; init; } = "";

    /// <summary>Where the seat's calls were logged, when they were. The path only: a row never holds a prompt.</summary>
    public string? CallLogPath { get; init; }

    /// <summary>The series counts, for a <see cref="Protocol.Series"/> row; null otherwise.</summary>
    public int? RoutesFound { get; init; }

    public int? RoutesExist { get; init; }

    public int? ExploratoryTries { get; init; }

    public string ToLegacyCsv(LegacyShape shape) => LegacyCsv.Line(this, shape);

    public static Row ParseLegacyCsv(string line, LegacyShape shape) => LegacyCsv.Parse(line, shape);
}

/// <summary>The two CSV shapes the research runs are archived in.</summary>
public enum LegacyShape
{
    /// <summary>The batteries' <c>results.csv</c> (batteries 2 and 3): 21 columns.</summary>
    BatteryResults,

    /// <summary>The seat bench's and the batteries' <c>scoreboard.csv</c>: 26 columns.</summary>
    Scoreboard,
}

/// <summary>
/// The legacy CSV lines, byte for byte as the research runners wrote them, so a re-run lands in the same files. The
/// quoting differs between the two shapes and is kept: the results note is always quoted with inner quotes turned to
/// apostrophes; the scoreboard quotes a field only when it holds a comma or a quote, doubling quotes.
/// </summary>
public static class LegacyCsv
{
    public const string Timestamp = "yyyy-MM-ddTHH:mm:ssZ";

    public static string Header(LegacyShape shape) => shape switch
    {
        LegacyShape.BatteryResults => "timestamp,battery,test,seat,world,agent,extensions,arm,case,repeat,outcome,ticks,calls,invalid,hand_off,deploy_blocked,lies_caught,freedom_before,freedom_after,releases,note",
        LegacyShape.Scoreboard => "timestamp,model,provider,protocol,battery,test,world,agent,repeat,outcome,ticks,calls,failed,invalid,lies_caught,shortcut,prompt_tokens,completion_tokens,reasoning_tokens,cached_tokens,tokens_known,seconds,median_call_seconds,tokens_per_second,cost_usd,notes",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    public static string Line(Row r, LegacyShape shape)
    {
        var inv = CultureInfo.InvariantCulture;
        return shape switch
        {
            LegacyShape.BatteryResults => string.Join(",",
                r.At.ToString(Timestamp, inv), r.Battery, r.Test, r.Seat, r.World, r.Agent, r.Extensions, r.Arm, r.Case, r.Repeat, r.Outcome.AsRow(),
                r.Ticks, r.Calls, r.Invalid, r.HandOff ? "yes" : "no", r.DeployBlocked, r.LiesCaught,
                r.FreedomBefore.ToString("0.00", inv), r.FreedomAfter.ToString("0.00", inv), r.Releases,
                "\"" + r.Note.Replace("\"", "'") + "\""),
            LegacyShape.Scoreboard => string.Join(",",
                r.At.ToString(Timestamp, inv), Q(r.Seat), r.Provider, r.Protocol, r.Battery, r.Test, r.World, r.Agent, r.Repeat, r.Outcome.AsRow(),
                r.Ticks, r.Calls, r.Failed, r.Invalid, r.LiesCaught, r.ShortcutTaken ? "yes" : "no",
                r.PromptTokens, r.CompletionTokens, r.ReasoningTokens, r.CachedTokens, r.TokensKnown ? "yes" : "no",
                r.Seconds.ToString("0.0", inv), r.MedianCallSeconds.ToString("0.00", inv),
                r.TokensPerSecond is null ? "" : r.TokensPerSecond.Value.ToString("0.0", inv),
                r.CostUsd is null ? "" : r.CostUsd.Value.ToString("0.0000", inv), Q(r.Note)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    private static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>One data line back into a row. A line that is not of the shape throws <see cref="FormatException"/>.</summary>
    public static Row Parse(string line, LegacyShape shape)
    {
        var inv = CultureInfo.InvariantCulture;
        var f = Split(line);
        try
        {
            switch (shape)
            {
                case LegacyShape.BatteryResults:
                    if (f.Count != 21) throw new FormatException($"{f.Count} fields, not 21");
                    return new Row
                    {
                        At = DateTimeOffset.ParseExact(f[0], Timestamp, inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                        Battery = f[1], Test = f[2], Seat = f[3], World = f[4], Agent = f[5], Extensions = f[6], Arm = f[7],
                        Case = int.Parse(f[8], inv), Repeat = int.Parse(f[9], inv), Outcome = OutcomeClasses.Parse(f[10]),
                        Ticks = int.Parse(f[11], inv), Calls = int.Parse(f[12], inv), Invalid = int.Parse(f[13], inv),
                        DeployBlocked = int.Parse(f[15], inv), LiesCaught = int.Parse(f[16], inv),
                        FreedomBefore = double.Parse(f[17], inv), FreedomAfter = double.Parse(f[18], inv), Releases = int.Parse(f[19], inv), Note = f[20],
                    };
                case LegacyShape.Scoreboard:
                    if (f.Count != 26) throw new FormatException($"{f.Count} fields, not 26");
                    return new Row
                    {
                        At = DateTimeOffset.ParseExact(f[0], Timestamp, inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                        Seat = f[1], Provider = f[2], Protocol = f[3], Battery = f[4], Test = f[5], World = f[6], Agent = f[7],
                        Repeat = int.Parse(f[8], inv), Outcome = OutcomeClasses.Parse(f[9]), Ticks = int.Parse(f[10], inv),
                        Calls = int.Parse(f[11], inv), Failed = int.Parse(f[12], inv), Invalid = int.Parse(f[13], inv), LiesCaught = int.Parse(f[14], inv),
                        ShortcutTaken = f[15] == "yes",
                        PromptTokens = long.Parse(f[16], inv), CompletionTokens = long.Parse(f[17], inv), ReasoningTokens = long.Parse(f[18], inv), CachedTokens = long.Parse(f[19], inv),
                        TokensKnown = f[20] == "yes", Seconds = double.Parse(f[21], inv), MedianCallSeconds = double.Parse(f[22], inv),
                        TokensPerSecond = f[23] == "" ? null : double.Parse(f[23], inv),
                        CostUsd = f[24] == "" ? null : decimal.Parse(f[24], inv), Note = f[25],
                    };
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new FormatException($"not a {shape} line: {e.Message}", e);
        }
    }

    /// <summary>Every row of a legacy file; a line that does not parse is skipped, as the scoreboard reader always did.</summary>
    public static IReadOnlyList<Row> ReadAll(string path, LegacyShape shape)
    {
        if (!File.Exists(path)) return [];
        var rows = new List<Row>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { rows.Add(Parse(line, shape)); }
            catch (FormatException) { }
        }

        return rows;
    }

    /// <summary>Appends the row to the file in the shape, through <see cref="AppendOnlyCsv"/>: the header is written once, and a header that no longer matches sends the row to a new file name.</summary>
    public static string Append(string path, LegacyShape shape, Row row) => AppendOnlyCsv.Append(path, Header(shape), Line(row, shape));

    /// <summary>A CSV line into fields, quotes honoured and doubled quotes unescaped.</summary>
    public static List<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }

        fields.Add(sb.ToString());
        return fields;
    }
}

/// <summary>
/// The rule every results file follows: a file is only ever appended to, its header is written once when the file is
/// new, and a change of header never rewrites a file. A line whose header does not match the file's first line goes to the
/// next free name beside it (<c>results-2.csv</c>, <c>results-3.csv</c>, …), so an old archive and a new shape never mix.
/// </summary>
public static class AppendOnlyCsv
{
    /// <summary>Appends the line under the header and returns the path it went to.</summary>
    public static string Append(string path, string header, string line)
    {
        var target = Resolve(path, header);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var fresh = !File.Exists(target);
        using var w = new StreamWriter(target, append: true);
        if (fresh) w.Write(header + "\n");
        w.Write(line + "\n");
        return target;
    }

    /// <summary>The file this header may be appended to: the path itself when absent or headed the same, else the first numbered sibling that is.</summary>
    public static string Resolve(string path, string header)
    {
        if (Matches(path, header)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}-{n}{ext}");
            if (Matches(candidate, header)) return candidate;
        }
    }

    private static bool Matches(string path, string header)
    {
        if (!File.Exists(path)) return true;
        using var reader = new StreamReader(path);
        return reader.ReadLine() == header;
    }
}
