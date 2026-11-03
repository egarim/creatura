using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Creatura.Cells;

namespace Creatura.Operator;

/// <summary>
/// How the console reaches the ledger: through the <c>creatura</c> command itself, so the Fleet is read by the same code
/// as <c>cell stream</c> (<c>creatura cell fleet --json</c>, <c>cell activity</c>) and an offer is written by
/// <c>cell offer</c> and nothing else. The console holds no key and opens no store.
/// </summary>
public interface ICellCommand
{
    /// <summary>Runs <c>creatura</c> with these arguments; the exit code and what it wrote.</summary>
    (int Code, string Output, string Error) Run(IReadOnlyList<string> args);
}

/// <summary>The <c>creatura</c> program as a process: the command named (default <c>creatura</c>, or <c>$CREATURA_CLI</c>), split on spaces, then the arguments.</summary>
public sealed class ProcessCellCommand(string? command = null) : ICellCommand
{
    public string Command { get; } = command ?? Environment.GetEnvironmentVariable("CREATURA_CLI") ?? "creatura";

    public (int Code, string Output, string Error) Run(IReadOnlyList<string> args)
    {
        var parts = Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = new ProcessStartInfo(parts[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var part in parts.Skip(1)) start.ArgumentList.Add(part);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, output.Result, error.Result);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (127, "", $"could not run '{Command}': {e.Message}; put creatura on the PATH or name it with --creatura <command>");
        }
    }
}

/// <summary>The Fleet as the console holds it: the rows <c>creatura cell fleet --json</c> gave, when, and what went wrong.</summary>
public sealed record FleetSnapshot(DateTimeOffset At, IReadOnlyList<FleetRow> Rows, string? Problem = null);

/// <summary>
/// The Fleet view's model (docs/design-console.md, U3): read through <see cref="ICellCommand"/>, never written, except an
/// offer, which is <c>cell offer</c>'s own entry. The home arguments (<c>--home</c>, <c>--store</c>) go to every command.
/// </summary>
public sealed class FleetModel(ICellCommand cli, IReadOnlyList<string>? homeArgs = null)
{
    public IReadOnlyList<string> HomeArgs { get; } = homeArgs ?? [];

    public ICellCommand Cli { get; } = cli;

    public FleetSnapshot Read()
    {
        var (code, output, error) = Cli.Run(["cell", "fleet", "--json", .. HomeArgs]);
        if (code != 0) return new FleetSnapshot(DateTimeOffset.UtcNow, [], Problem(error, output, code));
        try
        {
            return Parse(output);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return new FleetSnapshot(DateTimeOffset.UtcNow, [], "creatura cell fleet --json did not answer with the Fleet: " + e.Message);
        }
    }

    /// <summary>The rows of a <c>creatura.fleet/1</c> document, as <see cref="FleetView.Rows"/> made them.</summary>
    public static FleetSnapshot Parse(string json)
    {
        var doc = JsonNode.Parse(json)!.AsObject();
        if ((string?)doc["format"] != "creatura.fleet/1") throw new FormatException($"not a creatura.fleet/1 document but {(string?)doc["format"] ?? "nothing"}");
        var rows = new List<FleetRow>();
        foreach (var node in doc["cells"]!.AsArray())
        {
            var c = node!.AsObject();
            rows.Add(new FleetRow((string)c["cell"]!, (string)c["name"]!, (string)c["kind"]!, (string)c["creatura"]!, (string)c["vessel"]!,
                (string)c["vesselName"]!, (string)c["ring"]!, Time(c["lastSeen"])!.Value, (bool)c["quiet"]!)
            {
                Claim = (string?)c["claim"],
                ClaimId = (string?)c["claimId"],
                ClaimedAt = Time(c["claimedAt"]),
                Progress = (string?)c["progress"],
                Stress = (double?)c["stress"],
                Blocked = (bool?)c["blocked"] == true,
                Load = (double?)c["load"],
                PendingOffers = (int?)c["pendingOffers"] ?? 0,
            });
        }

        return new FleetSnapshot(Time(doc["at"])!.Value, rows);
    }

    /// <summary>Offers <paramref name="goal"/> to the cell: <c>creatura cell offer --to &lt;cell&gt; --goal &lt;goal&gt;</c>, on this home. Returns whether it was written and what the command said.</summary>
    public (bool Ok, string Said) Offer(string cell, string goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return (false, "an offer needs a goal");
        var (code, output, error) = Cli.Run(["cell", "offer", "--to", cell, "--goal", goal.Trim(), .. HomeArgs]);
        return code == 0 ? (true, output.Trim()) : (false, Problem(error, output, code));
    }

    /// <summary>One cell's activity, from <c>creatura cell activity</c>, as a run: every entry it wrote a row, oldest first.</summary>
    public RunModel CellRun(FleetRow row)
    {
        var (code, output, error) = Cli.Run(["cell", "activity", .. HomeArgs]);
        var model = new RunModel();
        if (code != 0) return model;
        foreach (var line in ActivityRun.Lines(output, row)) model.Apply(line);
        return model;
    }

    private static string Problem(string error, string output, int code) =>
        (error.Trim().Length > 0 ? error.Trim() : output.Trim().Length > 0 ? output.Trim() : $"creatura exited {code}").Split('\n')[0];

    private static DateTimeOffset? Time(JsonNode? node) =>
        (string?)node is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

/// <summary>
/// A cell's activities (the stream's document, <c>creatura cell activity</c>) as a replay for the Run view: a header
/// naming the cell, then a tick per activity of that cell, oldest first: the activity's type as the act, its summary as
/// the answer, its stress as what it showed, a blocked step marked.
/// </summary>
public static class ActivityRun
{
    public static IReadOnlyList<string> Lines(string activityDocument, FleetRow row)
    {
        var doc = JsonNode.Parse(activityDocument)!.AsObject();
        var actor = $"urn:creatura:cell:{row.Cell}";
        var items = (doc["orderedItems"] as JsonArray ?? []).OfType<JsonObject>().Where(a => (string?)a["actor"] == actor).Reverse().ToList();
        var lines = new List<string>
        {
            new JsonObject
            {
                ["type"] = "header",
                ["format"] = "creatura.replay/1",
                ["scenario"] = "cell",
                ["arm"] = row.Name,
                ["armText"] = $"{row.Creatura}, {row.Cell}",
                ["goal"] = row.Claim ?? "",
                ["cells"] = new JsonArray(row.Name),
            }.ToJsonString(),
        };
        var tick = 0;
        foreach (var a in items)
        {
            var type = (string?)a["type"] ?? "";
            var blocked = (bool?)a["creatura:blocked"] == true;
            var stress = (double?)a["creatura:stress"];
            lines.Add(new JsonObject
            {
                ["type"] = "tick",
                ["tick"] = ++tick,
                ["events"] = new JsonArray(new JsonObject
                {
                    ["kind"] = blocked ? "blocked" : type.Replace("creatura:", "", StringComparison.Ordinal).ToLowerInvariant(),
                    ["cell"] = row.Name,
                    ["at"] = (string?)a["published"],
                    ["act"] = Label(type),
                    ["text"] = Sentence((string?)a["summary"] ?? "", row.Name),
                    ["observation"] = stress is { } s ? string.Create(CultureInfo.InvariantCulture, $"stress {s:0.00}") : null,
                    ["verdict"] = blocked ? "blocked" : null,
                }),
            }.ToJsonString());
        }

        return lines;
    }

    /// <summary>An activity's type as a person says it: claim, progress, hand-off, released.</summary>
    public static string Label(string type) => type switch
    {
        "Join" => "joined",
        "Create" => "claim",
        "Update" => "progress",
        "Leave" => "hand-off",
        "Reject" => "declined",
        "creatura:Release" => "released",
        "Offer" => "offer",
        "Undo" => "withdrawn",
        _ => type.Replace("creatura:", "", StringComparison.Ordinal).ToLowerInvariant(),
    };

    /// <summary>The summary without the actor's name in front, which the view already shows: "started 'x'", not "claude-code on SURFACE started 'x'".</summary>
    public static string Sentence(string summary, string name)
    {
        var s = summary.StartsWith(name, StringComparison.Ordinal) ? summary[name.Length..].TrimStart(' ', ':') : summary;
        return s.Length == 0 ? summary : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
