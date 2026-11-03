using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Creatura.Scenarios;

namespace Creatura.Operator;

/// <summary>
/// A two-creatures record (experiments/two-creatures/records/…) read as replays (docs/design-console.md, U2): one run per
/// creature, from the files the runner already keeps, so every record opens the same way, the 2026-10-31 ones included,
/// and the driver is left as it is. From <c>agent-&lt;x&gt;-transcript.jsonl</c>: every shell command is a tick (the act
/// is the command, the answer its output and exit), with the model's words before it as the decision and the step
/// budget as the offer; from <c>messages.jsonl</c>: what the creature said on the board; from <c>checks.jsonl</c>: the
/// judge's checks of the app, as the world's rows; from <c>summary.json</c>: every claim the judge scored, as a verdict
/// on the row where the creature made it, a false one flagged there. Nothing in the record is changed.
/// </summary>
public static class PairRecord
{
    public static readonly string[] Creatures = ["a", "b"];

    /// <summary>A folder is a two-creatures record when it holds a summary and a creature's transcript.</summary>
    public static bool IsRecord(string dir) =>
        Directory.Exists(dir) && File.Exists(Path.Combine(dir, "summary.json")) && File.Exists(Path.Combine(dir, "agent-a-transcript.jsonl"));

    public static string CellOf(string who) => "creature-" + who;

    /// <summary>The record's runs: creature a, creature b, and both by their clocks.</summary>
    public static IReadOnlyList<(string Name, RunModel Model)> Read(string dir)
    {
        var a = RunModel.Read(new StringReader(string.Join("\n", Lines(dir, "a"))));
        var b = RunModel.Read(new StringReader(string.Join("\n", Lines(dir, "b"))));
        return [("both, by their clocks", RunModel.Merge("both creatures, interleaved by their clocks", a, b)), (CellOf("a"), a), (CellOf("b"), b)];
    }

    /// <summary>Writes each creature's replay into <paramref name="outDir"/>; returns the paths.</summary>
    public static IReadOnlyList<string> Convert(string dir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var paths = new List<string>();
        foreach (var who in Creatures)
        {
            var path = Path.Combine(outDir, $"agent-{who}.replay.jsonl");
            File.WriteAllLines(path, Lines(dir, who));
            paths.Add(path);
        }

        return paths;
    }

    /// <summary>One creature's replay, as JSON lines in <c>creatura.replay/1</c>.</summary>
    public static IReadOnlyList<string> Lines(string dir, string who)
    {
        var o = JsonlReplayWriter.Options;
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "summary.json"))) as JsonObject ?? [];
        var transcript = ReadJsonl(Path.Combine(dir, $"agent-{who}-transcript.jsonl"));
        var checks = ReadJsonl(Path.Combine(dir, "checks.jsonl"));
        var messages = ReadJsonl(Path.Combine(dir, "messages.jsonl"));
        var start = transcript.FirstOrDefault(t => Str(t, "kind") == "start");
        var cell = CellOf(who);
        var maxSteps = Int(start, "maxSteps");

        // The run's start: the first check's time less the seconds it says had passed (the claims count from there).
        var t0 = checks.Select(c => Time(c, "at") is { } at ? at.AddSeconds(-Dbl(c, "elapsed")) : (DateTimeOffset?)null).FirstOrDefault(t => t is not null)
            ?? Time(start, "at") ?? DateTimeOffset.UnixEpoch;

        var events = new List<(int Tick, DateTimeOffset At, JsonObject Event)>();
        var decision = "";
        var lastStep = 0;
        foreach (var t in transcript)
        {
            switch (Str(t, "kind"))
            {
                case "assistant":
                    decision = Clip(Str(t, "content") ?? "", 600);
                    break;
                case "shell":
                    var step = Int(t, "step");
                    lastStep = Math.Max(lastStep, step);
                    var exit = Str(t, "exit") ?? "";
                    var command = Str(t, "command") ?? "";
                    events.Add((step, Time(t, "at") ?? t0, new JsonObject
                    {
                        ["kind"] = "shell",
                        ["cell"] = cell,
                        ["at"] = Stamp(Time(t, "at") ?? t0),
                        ["act"] = "$ " + Clip(OneLine(command), 160),
                        ["text"] = (exit is "0" or "" ? "" : $"exit {exit}: ") + Clip(OneLine(Str(t, "output") ?? ""), 300),
                        ["offer"] = string.Create(CultureInfo.InvariantCulture, $"a shell, step {step}") + (maxSteps > 0 ? string.Create(CultureInfo.InvariantCulture, $" of {maxSteps}") : ""),
                        ["decision"] = decision,
                    }));
                    decision = "";
                    break;
            }
        }

        // What the creature said on the board, at the step it had reached by then.
        foreach (var m in messages.Where(m => Str(m, "from") == who))
        {
            var at = Time(m, "at") ?? t0;
            events.Add((StepAt(events, at), at, new JsonObject
            {
                ["kind"] = "message",
                ["cell"] = cell,
                ["at"] = Stamp(at),
                ["act"] = "says",
                ["text"] = Clip(OneLine(Str(m, "text") ?? ""), 400),
            }));
        }

        // The judge's checks of the app: the world's rows.
        foreach (var c in checks)
        {
            var at = Time(c, "at") ?? t0;
            var steps = (c["steps"] as JsonArray ?? []).OfType<JsonObject>().Select(s => (Str(s, "pass") == "true" ? "passed " : "failed ") + (Str(s, "title") ?? ""));
            events.Add((StepAt(events, at), at, new JsonObject
            {
                ["kind"] = "check",
                ["cell"] = RunModel.World,
                ["at"] = Stamp(at),
                ["act"] = string.Create(CultureInfo.InvariantCulture, $"check: {Int(c, "score")} of 5"),
                ["text"] = string.Join("; ", steps),
                ["verdict"] = Str(c, "shipped") == "true" ? "shipped" : "",
            }));
        }

        var ordered = events.OrderBy(e => e.Tick).ThenBy(e => e.At).ToList();

        // Every claim the judge scored, on the row where this creature made it: the last of its rows by the claim's time,
        // preferring one whose words are the claim's.
        var verdicts = new JsonArray();
        var claims = (summary["claims"] as JsonArray ?? []).OfType<JsonObject>().Where(c => Str(c, "who") == who).ToList();
        foreach (var claim in claims)
        {
            var at = t0.AddSeconds(Dbl(claim, "at"));
            var mine = ordered.Where(e => Str(e.Event, "cell") == cell && e.At <= at.AddSeconds(2)).ToList();
            if (mine.Count == 0) continue;
            var key = Letters(Str(claim, "text") ?? "", 24);
            var row = mine.LastOrDefault(e => key.Length > 0 && Letters((Str(e.Event, "act") ?? "") + " " + (Str(e.Event, "text") ?? ""), int.MaxValue).Contains(key, StringComparison.Ordinal));
            if (row.Event is null) row = mine[^1];
            var held = Str(claim, "true") == "true";
            var score = Int(claim, "score");
            verdicts.Add(new JsonObject
            {
                ["kind"] = "verdict",
                ["cell"] = cell,
                ["at"] = Stamp(at),
                ["text"] = string.Create(CultureInfo.InvariantCulture, $"{(held ? "The claim held" : "False claim")} ({Str(claim, "via")}, at {Dbl(claim, "at"):0} s): the app scored {score} of 5 then. \"{Clip(OneLine(Str(claim, "text") ?? ""), 120)}\""),
                ["verdict"] = held ? "claim held" : "false claim",
                ["on"] = new JsonObject { ["tick"] = row.Tick, ["contains"] = Prefix(Str(row.Event, "act") ?? "", 40) },
                ["falseClaim"] = !held,
            });
        }

        var lines = new List<string>();
        var header = new JsonObject
        {
            ["type"] = "header",
            ["format"] = "creatura.replay/1",
            ["scenario"] = "two-creatures",
            ["arm"] = Str(summary, "arm") ?? "",
            ["armText"] = $"{Str(summary, "run")}, {cell}, channel {Str(summary, "channel")}",
            ["maxTicks"] = maxSteps,
            ["goal"] = Clip(OneLine(Str(start, "goal") ?? ""), 400),
            ["cells"] = new JsonArray(cell),
        };
        lines.Add(header.ToJsonString(o));
        foreach (var group in ordered.GroupBy(e => e.Tick))
            lines.Add(new JsonObject { ["type"] = "tick", ["tick"] = group.Key, ["events"] = new JsonArray([.. group.Select(e => (JsonNode)e.Event)]) }.ToJsonString(o));
        if (verdicts.Count > 0) lines.Add(new JsonObject { ["type"] = "tick", ["tick"] = lastStep + 1, ["kind"] = "verdict", ["events"] = verdicts }.ToJsonString(o));

        var usage = summary["usage"]?[who] as JsonObject;
        var falseClaims = claims.Count(c => Str(c, "true") != "true");
        var shipped = Str(summary, "shipped") == "true";
        lines.Add(new JsonObject
        {
            ["type"] = "end",
            ["measures"] = new JsonObject
            {
                ["score"] = Int(summary, "score"),
                ["shipped"] = shipped,
                ["firstShipSeconds"] = Int(summary, "firstShipSeconds"),
                ["seconds"] = Int(summary, "seconds"),
                ["claims"] = claims.Count,
                ["falseClaims"] = falseClaims,
                ["steps"] = Int(usage, "steps"),
                ["cost"] = Dbl(usage, "cost"),
            },
            ["outcome"] = new JsonArray(
                string.Create(CultureInfo.InvariantCulture, $"The app scored {Int(summary, "score")} of 5") + (shipped ? string.Create(CultureInfo.InvariantCulture, $", first shipped at {Int(summary, "firstShipSeconds")} s.") : ", and did not ship."),
                string.Create(CultureInfo.InvariantCulture, $"{cell} made {claims.Count} claim{(claims.Count == 1 ? "" : "s")}, {falseClaims} false."),
                string.Create(CultureInfo.InvariantCulture, $"{cell} took {Int(usage, "steps")} steps for ${Dbl(usage, "cost"):0.00}.")),
        }.ToJsonString(o));
        return lines;
    }

    private static int StepAt(List<(int Tick, DateTimeOffset At, JsonObject Event)> events, DateTimeOffset at) =>
        events.Where(e => Str(e.Event, "kind") == "shell" && e.At <= at).Select(e => e.Tick).DefaultIfEmpty(0).Max();

    private static List<JsonObject> ReadJsonl(string path) => !File.Exists(path) ? [] : File.ReadLines(path)
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .Select(l =>
        {
            try
            {
                return JsonNode.Parse(l) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        })
        .OfType<JsonObject>()
        .ToList();

    private static string? Str(JsonObject? o, string key) => o?[key] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };

    private static int Int(JsonObject? o, string key) => (int)Math.Round(Dbl(o, key));

    private static double Dbl(JsonObject? o, string key) =>
        double.TryParse(Str(o, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static DateTimeOffset? Time(JsonObject? o, string key) =>
        DateTimeOffset.TryParse(Str(o, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string OneLine(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Prefix(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>The letters and digits of a text, lower case, at most <paramref name="max"/>: how a claim is found in a command that quoted it.</summary>
    private static string Letters(string s, int max)
    {
        var chars = s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant);
        return new string([.. max == int.MaxValue ? chars : chars.Take(max)]);
    }
}
