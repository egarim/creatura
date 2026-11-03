using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Creatura.Operator;

/// <summary>A run the page can open: where it is on disk (never sent to the page), its id, its title and its kind.</summary>
public sealed record RunEntry(string Id, string Path, string Kind, string Title, string Subtitle, DateTimeOffset When);

/// <summary>
/// The runs under the folders the person named (<c>--runs</c>): convoy and bench replays (<c>*.jsonl</c> whose first
/// line is a <c>creatura.replay/1</c> or web recording header), web recordings (a folder with <c>replay.jsonl</c>) and
/// two-creatures records (a folder with <c>summary.json</c> and the transcripts). The page asks for a run by its id,
/// a hash of its path, so it can open only what is listed here; a path from the page is never read.
/// </summary>
public sealed class RunCatalog(IReadOnlyList<string> roots)
{
    private const int MaxDepth = 6;

    public IReadOnlyList<string> Roots { get; } = [.. roots.Select(System.IO.Path.GetFullPath)];

    public IReadOnlyList<RunEntry> List()
    {
        var found = new Dictionary<string, RunEntry>(StringComparer.Ordinal);
        foreach (var root in Roots)
        {
            if (File.Exists(root)) Add(found, Probe(root));
            else if (Directory.Exists(root)) Walk(root, 0, found);
        }

        return [.. found.Values.OrderByDescending(r => r.When).ThenBy(r => r.Title, StringComparer.Ordinal)];
    }

    public RunEntry? Find(string id) => List().FirstOrDefault(r => r.Id == id);

    /// <summary>A run's views: one for a replay, three for a two-creatures record (both, then each creature).</summary>
    public static IReadOnlyList<(string Name, RunModel Model)> Open(RunEntry entry) => entry.Kind switch
    {
        "two-creatures" => PairRecord.Read(entry.Path),
        _ => [("run", RunModel.ReadFile(Directory.Exists(entry.Path) ? System.IO.Path.Combine(entry.Path, "replay.jsonl") : entry.Path))],
    };

    private static void Walk(string dir, int depth, Dictionary<string, RunEntry> found)
    {
        if (PairRecord.IsRecord(dir))
        {
            Add(found, Pair(dir));
            return;
        }

        if (File.Exists(System.IO.Path.Combine(dir, "replay.jsonl")))
        {
            Add(found, Probe(System.IO.Path.Combine(dir, "replay.jsonl"), dir));
            return;
        }

        IEnumerable<string> files = [], dirs = [];
        try
        {
            files = Directory.EnumerateFiles(dir, "*.jsonl");
            dirs = depth < MaxDepth ? Directory.EnumerateDirectories(dir) : [];
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
        }

        foreach (var file in files) Add(found, Probe(file));
        foreach (var sub in dirs)
        {
            var name = System.IO.Path.GetFileName(sub);
            if (name is "bin" or "obj" or "node_modules" or ".git") continue;
            Walk(sub, depth + 1, found);
        }
    }

    private static void Add(Dictionary<string, RunEntry> found, RunEntry? entry)
    {
        if (entry is not null) found[entry.Id] = entry;
    }

    /// <summary>A replay file, by its first line; null when it is not one.</summary>
    private static RunEntry? Probe(string file, string? folder = null)
    {
        string? first;
        try
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            first = reader.ReadLine();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return null;
        }

        if (first is null) return null;
        JsonObject? h;
        try
        {
            h = JsonNode.Parse(first) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (h is null || (string?)h["type"] != "header" || (string?)h["format"] is not ("creatura.replay/1" or "creatura.web-replay/1")) return null;
        var scenario = (string?)h["scenario"] ?? "run";
        var arm = (string?)h["arm"] ?? "";
        var title = scenario switch
        {
            "convoy" => $"Convoy, arm {arm}, seed {(int?)h["seed"] ?? 0}",
            "web" => $"{(string?)h["task"]} · {(string?)h["persona"]}",
            "bench" => $"Bench {(string?)h["goal"]}, arm {arm}",
            _ => $"{scenario} {arm}".Trim(),
        };
        var path = folder ?? file;
        return new RunEntry(IdOf(path), path, scenario, title, (string?)h["armText"] ?? "", File.GetLastWriteTimeUtc(file));
    }

    private static RunEntry Pair(string dir)
    {
        var summary = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(dir, "summary.json"))) as JsonObject ?? [];
        var score = (int?)summary["score"] ?? 0;
        var falseClaims = (int?)summary["falseClaims"] ?? 0;
        var name = System.IO.Path.GetFileName(dir);
        var when = DateTimeOffset.TryParseExact(name.Length >= 19 ? name[..19] : "", "yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t : File.GetLastWriteTimeUtc(System.IO.Path.Combine(dir, "summary.json"));
        return new RunEntry(IdOf(dir), dir, "two-creatures", $"Two creatures, {(string?)summary["arm"] ?? name}",
            string.Create(CultureInfo.InvariantCulture, $"{name}: {score} of 5, {falseClaims} false claim{(falseClaims == 1 ? "" : "s")}"), when);
    }

    private static string IdOf(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path))))[..16].ToLowerInvariant();
}

/// <summary>A run as the page reads it: the header, every row, the cells, the outcome; for a pair, each view.</summary>
public static class RunJson
{
    public static JsonObject Of(IReadOnlyList<(string Name, RunModel Model)> views) => new()
    {
        ["views"] = new JsonArray([.. views.Select(v => (JsonNode)View(v.Name, v.Model))]),
    };

    public static JsonObject View(string name, RunModel model)
    {
        var h = model.Header;
        var o = new JsonObject
        {
            ["name"] = name,
            ["title"] = RunText.Title(model),
            ["scenario"] = h?.Scenario ?? "",
            ["arm"] = h?.Arm ?? "",
            ["armText"] = h?.ArmText ?? "",
            ["goal"] = h?.Goal ?? "",
            ["tick"] = model.Tick,
            ["maxTicks"] = h?.MaxTicks ?? 0,
            ["ended"] = model.Ended,
            ["cells"] = new JsonArray([.. model.Cells.Select(c => (JsonNode?)c)]),
            ["outcome"] = new JsonArray([.. model.Outcome.Select(l => (JsonNode?)l)]),
            ["counts"] = new JsonObject
            {
                ["rows"] = model.Rows.Count,
                ["blocked"] = model.Count(RowMark.Blocked),
                ["refused"] = model.Count(RowMark.Refused),
                ["flagged"] = model.Count(RowMark.Flagged),
            },
        };
        var rows = new JsonArray();
        foreach (var r in model.Rows)
        {
            var row = new JsonObject
            {
                ["tick"] = r.Tick,
                ["cell"] = r.Cell,
                ["kind"] = r.Kind,
                ["act"] = r.Act,
                ["answer"] = r.Answer,
            };
            if (r.Observation.Length > 0) row["saw"] = r.Observation;
            if (r.Offer.Length > 0) row["offer"] = r.Offer;
            if (r.Decision.Length > 0) row["decision"] = r.Decision;
            if (r.Verdict.Length > 0) row["verdict"] = r.Verdict;
            if (r.Mark != RowMark.None) row["mark"] = r.Mark.ToString().ToLowerInvariant();
            if (r.FalseClaim) row["falseClaim"] = true;
            if (r.At is { } at) row["at"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            rows.Add(row);
        }

        o["rows"] = rows;
        return o;
    }
}
