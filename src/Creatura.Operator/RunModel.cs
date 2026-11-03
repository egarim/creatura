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
/// and why (decision), the act, what the world said back (answer, honest or not) and the verdict. A convoy replay has
/// no offer or decision yet (slice U2 adds them); they are read when a tick's event carries them.
/// </summary>
public sealed record RunRow(int Tick, string Cell, string Kind, string Observation, string Act, string Answer, string Verdict, RowMark Mark)
{
    public string Offer { get; init; } = "";

    public string Decision { get; init; } = "";
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

    private readonly List<RunRow> _rows = [];
    private readonly List<string> _cells = [];
    private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _outcome = [];
    private readonly List<KeyValuePair<string, string>> _measures = [];

    public RunHeader? Header { get; private set; }

    public IReadOnlyList<RunRow> Rows => _rows;

    /// <summary>Every cell that has a row or is in the header, in the header's order and then as they appear; the world last.</summary>
    public IReadOnlyList<string> Cells => _cells.Contains(World) ? [.. _cells.Where(c => c != World), World] : _cells;

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
            var cell = Str(ev, "truck") is { Length: > 0 } truck ? truck : World;
            AddCell(cell);
            _rows.Add(new RunRow(tick, cell, kind, cell == World ? "" : _seen.GetValueOrDefault(cell, ""), Act(kind, ev), Str(ev, "text") ?? "",
                Str(ev, "verdict") ?? "", MarkOf(kind, Str(ev, "verdict")))
            {
                Offer = Str(ev, "offer") ?? "",
                Decision = Str(ev, "decision") ?? "",
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
