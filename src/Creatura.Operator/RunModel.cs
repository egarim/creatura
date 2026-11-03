using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Creatura.Operator;

/// <summary>How a row of the Run view is marked: a blocked row, a refused action, or a flag (a lie told or caught).</summary>
public enum RowMark
{
    None,
    Blocked,
    Refused,
    Flagged,
}

/// <summary>
/// One row of the Run view (docs/design-console.md): one thing a cell did or the world did in a tick. The columns are the
/// draft's: what the world showed the cell (observation), what the control layer allowed (offer), what the seat chose
/// and why (decision), the act, what the world said back (answer, honest or not) and the verdict. The convoy writes
/// no offer or decision; the web worlds, the two-creatures records and the bench do (slice U2).
/// </summary>
public sealed record RunRow(int Tick, string Cell, string Kind, string Observation, string Act, string Answer, string Verdict, RowMark Mark)
{
    public string Offer { get; init; } = "";

    public string Decision { get; init; } = "";

    /// <summary>When it happened, by the writer's clock, when the event says; what a pair's two runs interleave by.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>The judge held this row's claim false (a verdict event pointed at it).</summary>
    public bool FalseClaim { get; init; }
}

/// <summary>What the run is, from its first line.</summary>
public sealed record RunHeader(string Format, string Scenario, string Arm, int Seed, string ArmText, int MaxTicks, string Goal, IReadOnlyList<string> Cells);

/// <summary>
/// The Run view's model: a replay (<c>creatura.replay/1</c>, docs/replay-format.md) read one JSON line at a time. Read
/// whole, followed live from a growing file (<see cref="ReplayFollower"/>) or fed by a run in this process
/// (<see cref="LiveRun"/>), the same lines go through <see cref="Apply"/>, so the three show the same rows in the same
/// order. Unknown properties, line types and event kinds are ignored or shown as they are, never an error (the format's
/// rule). It writes nothing anywhere.
/// </summary>
public sealed class RunModel
{
    /// <summary>The event kinds docs/replay-format.md defines today; any other kind still renders, by its name.</summary>
    public static readonly IReadOnlyList<string> KnownKinds =
    [
        "drive", "blocked", "lie", "caught", "refused", "distrust", "warned", "load", "refuel", "unload", "wasted",
        "claim", "takeover", "release", "message", "lost", "delayed", "closure", "breakdown", "out-of-fuel", "done", "stopped",
    ];

    /// <summary>The cell name of a row the world wrote (a closure, a breakdown, done): no truck did it.</summary>
    public const string World = "world";

    /// <summary>The cell name of a judge's verdict row; the row it judges is marked where it stands.</summary>
    public const string Judge = "judge";

    private readonly List<RunRow> _rows = [];
    private readonly List<string> _cells = [];
    private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _outcome = [];
    private readonly List<KeyValuePair<string, string>> _measures = [];

    public RunHeader? Header { get; private set; }

    public IReadOnlyList<RunRow> Rows => _rows;

    /// <summary>Every cell that has a row or is in the header, in the header's order and then as they appear; the world last.</summary>
    public IReadOnlyList<string> Cells => [.. _cells.Where(c => c is not (World or Judge)), .. _cells.Where(c => c == World), .. _cells.Where(c => c == Judge)];

    /// <summary>The last tick read.</summary>
    public int Tick { get; private set; }

    public bool Ended { get; private set; }

    public IReadOnlyList<string> Outcome => _outcome;

    public IReadOnlyList<KeyValuePair<string, string>> Measures => _measures;

    /// <summary>Lines read: every one, and those skipped as unreadable or of a type the reader does not know.</summary>
    public int Lines { get; private set; }

    public int Skipped { get; private set; }

    /// <summary>Raised after a line changed what the view shows.</summary>
    public event Action? Changed;

    public static RunModel Read(TextReader reader)
    {
        var model = new RunModel();
        while (reader.ReadLine() is { } line) model.Apply(line);
        return model;
    }

    /// <summary>Reads a replay file whole, opened for reading only and shared, so a run still writing it is not disturbed.</summary>
    public static RunModel ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return Read(reader);
    }

    /// <summary>
    /// Several runs as one, their rows interleaved by the writers' clocks (a row with no time keeps its place after the
    /// timed ones): a pair's two creatures read as one conversation. The world's rows are taken from the first run only,
    /// since every run of a pair carries the same ones. The header is the first run's, with <paramref name="armText"/>.
    /// </summary>
    public static RunModel Merge(string armText, RunModel first, params RunModel[] more)
    {
        var all = new List<RunModel> { first };
        all.AddRange(more);
        var merged = new RunModel();
        merged.Header = first.Header is { } h ? h with { ArmText = armText, Cells = [.. all.SelectMany(m => m.Header?.Cells ?? []).Distinct()] } : null;
        var rows = all.SelectMany((m, i) => m._rows.Where(r => i == 0 || r.Cell != World).Select(r => (Row: r, Run: i)))
            .Select((x, order) => (x.Row, Order: order))
            .OrderBy(x => x.Row.At ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Order)
            .Select(x => x.Row);
        foreach (var cell in merged.Header?.Cells ?? []) merged.AddCell(cell);
        foreach (var row in rows)
        {
            merged._rows.Add(row);
            merged.AddCell(row.Cell);
        }

        foreach (var cell in all.SelectMany(m => m._cells)) merged.AddCell(cell);
        merged.Tick = all.Max(m => m.Tick);
        merged.Ended = all.All(m => m.Ended);
        merged.Lines = all.Sum(m => m.Lines);
        merged.Skipped = all.Sum(m => m.Skipped);
        merged._outcome.AddRange(all.SelectMany(m => m._outcome).Distinct());
        merged._measures.AddRange(first._measures);
        return merged;
    }

    /// <summary>The rows of one cell, or every row when <paramref name="cell"/> is null.</summary>
    public IEnumerable<RunRow> RowsOf(string? cell) => cell is null ? _rows : _rows.Where(r => r.Cell == cell);

    /// <summary>How many rows carry each mark.</summary>
    public int Count(RowMark mark, string? cell = null) => RowsOf(cell).Count(r => r.Mark == mark);

    /// <summary>Reads one line of a replay. A blank line is nothing; a line that is not a JSON object, or of an unknown type, is skipped.</summary>
    public void Apply(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Lines++;
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            obj = null;
        }

        switch (obj is null ? null : Str(obj, "type"))
        {
            case "header":
                ReadHeader(obj!);
                break;
            case "tick":
                ReadTick(obj!);
                break;
            case "end":
                ReadEnd(obj!);
                break;
            default:
                Skipped++;
                return;
        }

        Changed?.Invoke();
    }

    private void ReadHeader(JsonObject h)
    {
        var cells = new List<string>();
        foreach (var truck in Array(h, "trucks"))
        {
            if (truck is not JsonObject t || Str(t, "name") is not { Length: > 0 } name) continue;
            cells.Add(name);
            AddCell(name);
            _seen[name] = Observe(Str(t, "start") ?? "", Int(t, "fuelCapacity"), 0, null);
        }

        // Any other run names its cells: a web persona, a creature, a bench agent.
        foreach (var c in Array(h, "cells"))
        {
            var name = c is JsonObject o ? Str(o, "name") : c is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (name is not { Length: > 0 } || cells.Contains(name)) continue;
            cells.Add(name);
            AddCell(name);
        }

        Header = new RunHeader(Str(h, "format") ?? "", Str(h, "scenario") ?? "", Str(h, "arm") ?? "", Int(h, "seed"), Str(h, "armText") ?? "",
            Int(h, "maxTicks"), Str(h, "goal") ?? "", cells);
    }

    private void ReadTick(JsonObject t)
    {
        var tick = Int(t, "tick");
        Tick = tick;
        foreach (var e in Array(t, "events"))
        {
            if (e is not JsonObject ev) continue;
            var kind = Str(ev, "kind") ?? "";
            var cell = Str(ev, "cell") is { Length: > 0 } named ? named : Str(ev, "truck") is { Length: > 0 } truck ? truck : World;
            if (kind == "verdict" && ev["on"] is JsonObject on) Judged(cell, on, ev);
            var rowCell = kind == "verdict" ? Judge : cell;
            AddCell(rowCell);
            var lie = ev["lie"] is JsonValue l && l.TryGetValue<bool>(out var lied) && lied;
            _rows.Add(new RunRow(tick, rowCell, kind, Str(ev, "observation") ?? (rowCell is World or Judge ? "" : _seen.GetValueOrDefault(cell, "")),
                Str(ev, "act") is { Length: > 0 } act ? act : Act(kind, ev), Str(ev, "text") ?? "",
                Str(ev, "verdict") ?? "", lie && kind is not ("blocked" or "refused") ? RowMark.Flagged : MarkOf(kind, Str(ev, "verdict")))
            {
                Offer = Str(ev, "offer") ?? "",
                Decision = Str(ev, "decision") ?? "",
                At = Str(ev, "at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null,
            });
        }

        // What each truck shows at the end of this tick is what it sees at the start of the next: its gauge, not its tank.
        foreach (var f in Array(t, "trucks"))
        {
            if (f is not JsonObject frame || Str(frame, "name") is not { Length: > 0 } name) continue;
            _seen[name] = Observe(Str(frame, "at") ?? "", frame.ContainsKey("fuelBelieved") ? Int(frame, "fuelBelieved") : Int(frame, "fuel"),
                Int(frame, "load"), Str(frame, "claim"), Str(frame, "state"));
        }
    }

    private void ReadEnd(JsonObject e)
    {
        Ended = true;
        _outcome.Clear();
        foreach (var line in Array(e, "outcome")) if (line is JsonValue v && v.TryGetValue<string>(out var s)) _outcome.Add(s);
        _measures.Clear();
        if (e["measures"] is JsonObject measures)
            foreach (var (key, value) in measures) _measures.Add(new(key, value?.ToJsonString() ?? ""));
    }

    /// <summary>
    /// A judge's verdict on an earlier row (<c>{"kind":"verdict","cell":…,"on":{"tick":…,"contains":…},"verdict":…,"falseClaim":true}</c>):
    /// the cell's row at that tick (the last one, or the last whose act or answer contains the text given) takes the
    /// verdict, and is flagged when the claim was false. A verdict on a row that is not there marks nothing; its own row
    /// still says what the judge found.
    /// </summary>
    private void Judged(string cell, JsonObject on, JsonObject ev)
    {
        var tick = Int(on, "tick");
        var contains = Str(on, "contains");
        var index = -1;
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            var r = _rows[i];
            if (r.Tick != tick || r.Cell != cell) continue;
            if (index < 0) index = i;
            if (contains is not { Length: > 0 } || r.Act.Contains(contains, StringComparison.Ordinal) || r.Answer.Contains(contains, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0) return;
        var falseClaim = ev["falseClaim"] is JsonValue f && f.TryGetValue<bool>(out var b) && b;
        var row = _rows[index];
        _rows[index] = row with
        {
            Verdict = Str(ev, "verdict") is { Length: > 0 } v ? v : row.Verdict,
            Mark = falseClaim ? RowMark.Flagged : row.Mark,
            FalseClaim = row.FalseClaim || falseClaim,
        };
    }

    private void AddCell(string cell)
    {
        if (!_cells.Contains(cell)) _cells.Add(cell);
    }

    /// <summary>What a truck sees: where it is, its gauge, its load and the village it holds.</summary>
    private static string Observe(string at, int gauge, int load, string? claim, string? state = null) =>
        string.Create(CultureInfo.InvariantCulture, $"at {at}, gauge {gauge}, load {load}")
        + (string.IsNullOrEmpty(claim) ? "" : $", holds {claim}")
        + (state is null or "acting" ? "" : $", {state}");

    /// <summary>The act in a few words: the kind and where it applies.</summary>
    public static string Act(string kind, JsonObject ev)
    {
        var road = Str(ev, "road");
        var from = Str(ev, "from");
        var to = Str(ev, "to");
        var village = Str(ev, "village");
        var parts = new List<string> { kind.Length == 0 ? "(no kind)" : kind };
        if (road is { Length: > 0 }) parts.Add(road);
        if (from is { Length: > 0 } && to is { Length: > 0 }) parts.Add($"{from} → {to}");
        else if (to is { Length: > 0 }) parts.Add("to " + to);
        else if (from is { Length: > 0 }) parts.Add("from " + from);
        else if (village is { Length: > 0 }) parts.Add(village);
        return string.Join(" ", parts);
    }

    /// <summary>A blocked road, a refused action, a lie told or caught (and any action whose verdict is a lie) are marked.</summary>
    public static RowMark MarkOf(string kind, string? verdict) => kind switch
    {
        "blocked" => RowMark.Blocked,
        "refused" => RowMark.Refused,
        "lie" or "caught" => RowMark.Flagged,
        _ => string.Equals(verdict, "Lied", StringComparison.OrdinalIgnoreCase) ? RowMark.Flagged : RowMark.None,
    };

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[key] is JsonValue n ? n.ToJsonString() : null;

    private static int Int(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static IEnumerable<JsonNode?> Array(JsonObject o, string key) => o[key] as JsonArray ?? [];
}
