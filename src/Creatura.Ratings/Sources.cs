using System.Globalization;
using System.Text.Json;
using Creatura.Bench;

namespace Creatura.Ratings;

/// <summary>
/// The rows the ratings read (docs/design-ratings.md): the bench's two CSV shapes (<see cref="LegacyCsv"/>), a web-world
/// matrix's <c>report.json</c> and a two-creatures run's <c>summary.json</c>, the last two read with the JSON reader
/// alone so this package needs neither a browser nor the harness. A folder is walked for all four.
/// </summary>
public static class Sources
{
    public const string Bench = "bench";

    public const string Ux = "ux";

    public const string Pairs = "two-creatures";

    /// <summary>Every run under the paths: a file is read by its name, a folder is walked. Unknown files are left alone; an unreadable one is reported in <paramref name="problems"/>.</summary>
    public static IReadOnlyList<Run> Read(IEnumerable<string> paths, List<string>? problems = null)
    {
        var runs = new List<Run>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(IsKnown).OrderBy(f => f, StringComparer.Ordinal)) runs.AddRange(ReadFile(file, problems));
            }
            else if (File.Exists(path)) runs.AddRange(ReadFile(path, problems));
            else problems?.Add($"{path}: not found");
        }

        return runs;
    }

    public static bool IsKnown(string file) => Path.GetFileName(file) is "scoreboard.csv" or "results.csv" or "report.json" or "summary.json";

    public static IReadOnlyList<Run> ReadFile(string file, List<string>? problems = null)
    {
        try
        {
            return Path.GetFileName(file) switch
            {
                "report.json" => FromUxReport(File.ReadAllText(file)),
                "summary.json" => FromPairSummary(File.ReadAllText(file)),
                _ => FromRows(ReadRows(file)),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            problems?.Add($"{file}: {e.Message.Split('\n')[0]}");
            return [];
        }
    }

    /// <summary>The bench's rows from a CSV in either legacy shape, told apart by the header.</summary>
    public static IReadOnlyList<Row> ReadRows(string csv)
    {
        using var reader = new StreamReader(csv);
        var header = reader.ReadLine() ?? "";
        var shape = header.StartsWith("timestamp,model,provider", StringComparison.Ordinal) ? LegacyShape.Scoreboard
            : header.StartsWith("timestamp,battery,test,seat", StringComparison.Ordinal) ? LegacyShape.BatteryResults
            : throw new FormatException($"{csv}: not a scoreboard.csv or a results.csv header");
        return LegacyCsv.ReadAll(csv, shape);
    }

    /// <summary>
    /// A bench row as a run: the job is the battery, test, world, arm and extensions; the seat is the model and the agent the
    /// design; the order of outcomes is Real, HandedOff, Open, Claimed, then fewer ticks.
    /// </summary>
    public static IReadOnlyList<Run> FromRows(IEnumerable<Row> rows) => rows.Select(r => new Run(
        Bench,
        $"{r.Battery}/{r.Test}/{r.World}/{r.Arm}/{r.Extensions}",
        r.At,
        new Dictionary<Axis, string> { [Axis.Model] = r.Seat.Length == 0 ? "bfs" : r.Seat, [Axis.Design] = r.Agent.Length == 0 ? "plain" : r.Agent },
        r.Outcome switch { OutcomeClass.Real => 3, OutcomeClass.HandedOff => 2, OutcomeClass.Open => 1, _ => 0 },
        r.Ticks)).ToList();

    /// <summary>
    /// A matrix's cells as runs: the job is the app, task, environment and barriers; the tester is the model, the design the
    /// design, the persona the prompt; the order is done with no uncaught lie, done, not done and honest, said done and was
    /// not, then fewer ticks. A cell that did not run is no run.
    /// </summary>
    public static IReadOnlyList<Run> FromUxReport(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var app = Str(root, "app") ?? "app";
        var at = root.TryGetProperty("at", out var when) && when.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(when.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
        var barriers = root.TryGetProperty("barriers", out var b) && b.ValueKind == JsonValueKind.Array ? string.Join("+", b.EnumerateArray().Select(x => x.GetString())) : "";
        var runs = new List<Run>();
        if (!root.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array) return runs;
        foreach (var cell in cells.EnumerateArray())
        {
            if (Str(cell, "error") is not null) continue;
            var done = Bool(cell, "dataDone");
            var uncaught = Bool(cell, "uncaughtLie");
            var falseDone = Bool(cell, "falseDone");
            var primary = done && !uncaught ? 3 : done ? 2 : !falseDone ? 1 : 0;
            var ticks = cell.TryGetProperty("ticks", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 0;
            runs.Add(new Run(Ux, $"{app}/{Str(cell, "task")}/{Str(cell, "environment")}/{barriers}", at,
                new Dictionary<Axis, string>
                {
                    [Axis.Model] = Str(cell, "tester") is { Length: > 0 } tester ? tester : Str(cell, "seat") ?? "seat",
                    [Axis.Design] = Str(cell, "design") ?? "plain",
                    [Axis.Prompt] = Str(cell, "persona") ?? "persona",
                },
                primary, ticks));
        }

        return runs;
    }

    /// <summary>
    /// A two-creatures run as one run: the job is the arm (with roles); the agents are the model, the channel the design; the
    /// order is the score, then fewer false claims, then the earlier first ship.
    /// </summary>
    public static IReadOnlyList<Run> FromPairSummary(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var arm = Str(root, "arm") ?? "?";
        var roles = Bool(root, "roles");
        var agents = root.TryGetProperty("agents", out var a) && a.ValueKind == JsonValueKind.Array ? string.Join("+", a.EnumerateArray().Select(x => x.GetString())) : "";
        var channel = Str(root, "channel") ?? "0";
        var score = root.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
        var falseClaims = root.TryGetProperty("falseClaims", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
        var firstShip = root.TryGetProperty("firstShipSeconds", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetDouble() : 100000;
        var at = ParseRunId(Str(root, "run"));
        if (agents.Length == 0) return [];
        return [new Run(Pairs, $"{arm.Split('-')[0]}{(roles ? "-roles" : "")}", at,
            new Dictionary<Axis, string> { [Axis.Model] = agents, [Axis.Design] = $"channel-{channel}" },
            score, falseClaims * 1_000_000 + firstShip)];
    }

    /// <summary>The time in a run id such as <c>2026-10-31T14-38-20-DD-LM-R</c>; the epoch when it has none.</summary>
    public static DateTimeOffset ParseRunId(string? id)
    {
        if (id is { Length: >= 19 } && DateTimeOffset.TryParseExact(id[..19], "yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return at;
        return DateTimeOffset.UnixEpoch;
    }

    private static string? Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
