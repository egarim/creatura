using System.Globalization;
using System.Text;
using System.Text.Json;
using Creatura.Seats;

namespace Creatura.Worlds.Web;

/// <summary>
/// What a judge can find in a cell: a closed set, so judgements count and compare. The first five follow from the
/// cell's verdicts alone (the rules always find them); <see cref="Friction"/> is a reading of the trace, which only a
/// model judge offers; <see cref="Ok"/> is nothing to report.
/// </summary>
public enum UxFinding
{
    /// <summary>Done by the data, nothing else to say.</summary>
    Ok,

    /// <summary>The page said a commit went through and the data does not show it.</summary>
    AppLied,

    /// <summary>The persona said it was done and the data says not.</summary>
    PersonaOverclaimed,

    /// <summary>The world told a lie the run did not block.</summary>
    DesignMissedLie,

    /// <summary>Not done by the data, for any reason.</summary>
    NotDone,

    /// <summary>The cell could not run.</summary>
    NotRun,

    /// <summary>Done, or not, the hard way: wrong turns, repeats, escalations, a control the persona could not find.</summary>
    Friction,
}

/// <summary>A judge's reading of one cell: the findings, why, the ticks that show it, who judged, and what the rules corrected.</summary>
public sealed record UxJudgement
{
    public required string Cell { get; init; }

    public required IReadOnlyList<UxFinding> Findings { get; init; }

    public string Why { get; init; } = "";

    public IReadOnlyList<int> Ticks { get; init; } = [];

    /// <summary><c>rules</c>, or the model's catalog name.</summary>
    public required string Judge { get; init; }

    /// <summary>Where the rules overrode the model: a finding the verdicts require that it left out, or one they forbid that it gave.</summary>
    public IReadOnlyList<string> Corrections { get; init; } = [];

    public decimal? Cost { get; init; }

    public static string Name(UxFinding f) => f switch
    {
        UxFinding.Ok => "ok",
        UxFinding.AppLied => "app-lied",
        UxFinding.PersonaOverclaimed => "persona-overclaimed",
        UxFinding.DesignMissedLie => "design-missed-lie",
        UxFinding.NotDone => "not-done",
        UxFinding.NotRun => "not-run",
        _ => "friction",
    };

    public static UxFinding? Parse(string name) => Enum.GetValues<UxFinding>().Cast<UxFinding?>().FirstOrDefault(f => Name(f!.Value) == name.Trim().ToLowerInvariant());
}

/// <summary>
/// The judge (docs/design-web-worlds.md, phase 2c). It reads the gap between what was said and what the data shows, and
/// nothing else: the cell's verdicts and its trace, never the screen. <see cref="Rules"/> is the judge with no model:
/// every finding that follows from the verdicts. A model judge adds a reading of the trace (<see cref="UxFinding.Friction"/>,
/// the why, the ticks), and is held to the rules: a finding they require is put back, one they forbid is struck, and each
/// is recorded as a correction. So a model judge can explain a cell; it cannot change its verdict.
/// </summary>
public sealed class UxJudge
{
    public const string SystemPrompt = """
        You judge one cell of a usability test. A synthetic person used a web app to do a task. You are given what the
        app's own data says (the only truth), what the page said, what the person believed, the lies the test injected or
        the app told and whether the agent's control layer caught them, and the run's trace (one line per step).
        Find what went wrong, from this closed set only:
          app-lied             the page said a save went through and the data does not show it
          persona-overclaimed  the person said they were done and the data says not
          design-missed-lie    a lie was told and the control layer did not block it
          not-done             the data does not show the task done
          friction             it took wrong turns, repeated actions, escalations, or a control the person could not find
          ok                   done, and nothing above
        Respond with JSON only, exactly:
        {"findings": ["<names from the set>"], "why": "<two sentences at most, citing ticks>", "ticks": [<the ticks that show it>]}
        """;

    private readonly ModelEndpoint? _endpoint;
    private readonly ModelClient _client;
    private readonly CallLog _log;

    /// <summary>A judge over a catalogued model; the rules alone when <paramref name="endpoint"/> is null.</summary>
    public UxJudge(ModelEndpoint? endpoint = null, ModelClient? client = null, CallLog? log = null)
    {
        _endpoint = endpoint;
        _client = client ?? new ModelClient();
        _log = log ?? CallLog.None;
    }

    public string Name => _endpoint?.Name ?? "rules";

    /// <summary>The findings the verdicts decide, in a fixed order; <see cref="UxFinding.Ok"/> when there are none.</summary>
    public static IReadOnlyList<UxFinding> Required(UxCell cell)
    {
        if (cell.Error is not null) return [UxFinding.NotRun];
        var found = new List<UxFinding>();
        if (cell.PageSaidNotDone) found.Add(UxFinding.AppLied);
        if (cell.BelievedNotDone) found.Add(UxFinding.PersonaOverclaimed);
        if (cell.UncaughtLie) found.Add(UxFinding.DesignMissedLie);
        if (!cell.DataDone) found.Add(UxFinding.NotDone);
        return found.Count == 0 ? [UxFinding.Ok] : found;
    }

    /// <summary>The judge with no model: the required findings and a sentence from the verdicts.</summary>
    public static UxJudgement Rules(UxCell cell)
    {
        var findings = Required(cell);
        var why = cell.Error is not null ? $"The cell did not run: {cell.Error}."
            : cell.DataDone ? string.Create(CultureInfo.InvariantCulture, $"The data shows it done in {cell.Ticks} ticks.")
            : string.Join(" ", new[]
            {
                cell.PageSaidNotDone ? "The page said the save went through; the data does not show it." : null,
                cell.BelievedNotDone ? string.Create(CultureInfo.InvariantCulture, $"The person said they were done at tick {cell.BelievedDoneAt}.") : null,
                cell.LiesTold > 0 ? string.Create(CultureInfo.InvariantCulture, $"{cell.LiesCaught} of {cell.LiesTold} lies caught.") : null,
                $"The data: {cell.CheckDetail}.",
            }.OfType<string>());
        return new UxJudgement { Cell = cell.Key, Findings = findings, Why = why, Judge = "rules", Cost = 0m };
    }

    /// <summary>What the model is shown for a cell: the verdicts and the trace, never the screen.</summary>
    public static string Prompt(UxCell cell, AgentRun? run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CELL: task {cell.Task}; persona {cell.Persona}; device {cell.Environment}; tester {cell.Tester}; design {cell.Design}");
        sb.AppendLine($"THE DATA SAYS: {(cell.DataDone ? "done" : "NOT done")} ({cell.CheckDetail})");
        sb.AppendLine($"THE PAGE SAID A SAVE WENT THROUGH: {(cell.PageSaidDone ? "yes" : "no")}");
        sb.AppendLine($"THE PERSON SAID THEY WERE DONE: {(cell.BelievedDoneAt is { } t ? $"yes, at tick {t}" : "no")}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"LIES TOLD: {cell.LiesTold}; CAUGHT BY THE CONTROL LAYER: {cell.LiesCaught}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"TICKS: {cell.Ticks}; ESCALATIONS TO A STRONGER MODEL: {cell.Escalations}"));
        if (cell.Remembered.Count > 0) sb.AppendLine("THE PERSON REMEMBERED: " + string.Join(" | ", cell.Remembered));
        sb.AppendLine("TRACE (tick, level, action, kind, detail):");
        if (run is null) sb.AppendLine("  (no trace)");
        else
        {
            foreach (var e in run.Trace.Where(e => e.Kind != TraceKind.Idle || e.Detail.Length > 0).Take(60))
            {
                var detail = e.Detail.Length <= 160 ? e.Detail : e.Detail[..160] + "…";
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {e.Tick} {e.Level} {e.Action} {e.Kind} {detail}"));
            }
        }

        sb.Append("Your judgement, JSON only.");
        return sb.ToString();
    }

    /// <summary>Judges a cell: by the rules alone, or by the model held to the rules. A failed or unreadable call falls back to the rules, and says so.</summary>
    public async Task<UxJudgement> JudgeAsync(UxCell cell, AgentRun? run, CancellationToken cancellationToken = default)
    {
        var rules = Rules(cell);
        if (_endpoint is null || cell.Error is not null) return rules;

        var messages = new List<ChatMessage> { new("system", SystemPrompt), new("user", Prompt(cell, run)) };
        var reply = await _client.ChatAsync(_endpoint, messages, null, jsonMode: true, maxTokens: 400, cancellationToken).ConfigureAwait(false);
        var cost = _endpoint.Cost(reply.PromptTokens ?? 0, reply.CompletionTokens ?? 0);
        var parsed = reply.Failed ? null : Parse(reply.Content);
        _log.Write(CallRecord.Of(_endpoint, "ux-judge", _endpoint.Protocol, cell.Key, messages, [], reply, parsed is not null, parsed is null ? "fell back to the rules" : null));
        if (parsed is null)
            return rules with { Judge = $"rules (the {_endpoint.Name} judge {(reply.Failed ? "failed: " + reply.Error : "answered nothing readable")})", Cost = cost };

        var (findings, why, ticks, unknown) = parsed.Value;
        var corrections = unknown.Select(u => $"'{u}' is not a finding; dropped").ToList();
        var held = Hold(cell, findings, corrections);
        return new UxJudgement { Cell = cell.Key, Findings = held, Why = why, Ticks = ticks, Judge = _endpoint.Name, Corrections = corrections, Cost = cost };
    }

    /// <summary>
    /// The model's findings held to the verdicts: every required finding put back, <see cref="UxFinding.Ok"/> struck from a
    /// cell not done, the other verdict findings struck where the verdicts deny them. Friction stays: it is the model's own reading.
    /// </summary>
    public static IReadOnlyList<UxFinding> Hold(UxCell cell, IReadOnlyList<UxFinding> given, List<string> corrections)
    {
        var required = Required(cell);
        var result = new List<UxFinding>();
        foreach (var f in given.Distinct())
        {
            var allowed = f == UxFinding.Friction || required.Contains(f);
            if (allowed) result.Add(f);
            else corrections.Add($"{UxJudgement.Name(f)} struck: the verdicts do not support it");
        }

        foreach (var f in required.Where(f => !result.Contains(f)))
        {
            if (f == UxFinding.Ok && result.Contains(UxFinding.Friction)) continue;   // done the hard way: friction says it
            result.Add(f);
            corrections.Add($"{UxJudgement.Name(f)} added: the verdicts require it");
        }

        if (result.Contains(UxFinding.Friction)) result.Remove(UxFinding.Ok);   // done, the hard way: friction says it
        return [.. result.OrderBy(f => f)];
    }

    private static (IReadOnlyList<UxFinding> Findings, string Why, IReadOnlyList<int> Ticks, IReadOnlyList<string> Unknown)? Parse(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("findings", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var names = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            var findings = names.Select(UxJudgement.Parse).OfType<UxFinding>().ToList();
            var unknown = names.Where(n => UxJudgement.Parse(n) is null).ToList();
            var why = root.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? "" : "";
            var ticks = root.TryGetProperty("ticks", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToList()
                : [];
            return (findings, why, ticks, unknown);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The summary writer (phase 2c). The facts come first and are always written from the report and the judgements, by
/// code: the totals, the findings counted, the cells under each. A model, when one is named, adds one paragraph for the
/// app's owner beneath them, labelled with its name; it never replaces a fact.
/// </summary>
public sealed class UxSummary
{
    public const string SystemPrompt = """
        You write for the owner of a web app, who ran synthetic users through it. You are given the facts of the run:
        totals, findings by cell, and a judge's notes. Write ONE short paragraph (at most five sentences): what worked,
        what did not, and the one thing to look at first. Use only the facts given; do not invent numbers or screens.
        Plain text, no lists, no headings.
        """;

    private readonly ModelEndpoint? _endpoint;
    private readonly ModelClient _client;
    private readonly CallLog _log;

    public UxSummary(ModelEndpoint? endpoint = null, ModelClient? client = null, CallLog? log = null)
    {
        _endpoint = endpoint;
        _client = client ?? new ModelClient();
        _log = log ?? CallLog.None;
    }

    /// <summary>The facts, by code: what a summary is made of whether or not a model writes the paragraph.</summary>
    public static string Facts(UxReport report, IReadOnlyList<UxJudgement> judgements)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# UX summary: {report.App}");
        sb.AppendLine();
        sb.AppendLine(report.Totals() + ".");
        if (report.ByDesign().Count > 1)
        {
            sb.AppendLine();
            foreach (var d in report.ByDesign()) sb.AppendLine($"- {d.Line()}");
        }

        sb.AppendLine();
        sb.AppendLine("## Findings");
        sb.AppendLine();
        var counts = judgements.SelectMany(j => j.Findings).Where(f => f != UxFinding.Ok).GroupBy(f => f).OrderBy(g => g.Key).ToList();
        if (counts.Count == 0) sb.AppendLine("None.");
        foreach (var g in counts) sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- {UxJudgement.Name(g.Key)}: {g.Count()} cell{(g.Count() == 1 ? "" : "s")}"));
        var flagged = judgements.Where(j => !j.Findings.SequenceEqual([UxFinding.Ok])).ToList();
        if (flagged.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Cells with findings");
            sb.AppendLine();
            foreach (var j in flagged)
                sb.AppendLine($"- **{j.Cell}** ({string.Join(", ", j.Findings.Select(UxJudgement.Name))}): {j.Why}" + (j.Ticks.Count > 0 ? $" Ticks {string.Join(", ", j.Ticks)}." : ""));
        }

        var ok = judgements.Count(j => j.Findings.SequenceEqual([UxFinding.Ok]));
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{ok} of {judgements.Count} cells ok."));
        var corrected = judgements.Where(j => j.Corrections.Count > 0).ToList();
        if (corrected.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"The rules corrected the judge in {corrected.Count} cell{(corrected.Count == 1 ? "" : "s")}:"));
            foreach (var j in corrected) sb.AppendLine($"- {j.Cell}: {string.Join("; ", j.Corrections)}");
        }

        return sb.ToString();
    }

    /// <summary>The summary: the facts, then the model's paragraph when there is a model and its call worked.</summary>
    public async Task<(string Text, decimal? Cost)> WriteAsync(UxReport report, IReadOnlyList<UxJudgement> judgements, CancellationToken cancellationToken = default)
    {
        var facts = Facts(report, judgements);
        if (_endpoint is null) return (facts, 0m);
        var messages = new List<ChatMessage> { new("system", SystemPrompt), new("user", facts) };
        var reply = await _client.ChatAsync(_endpoint, messages, null, jsonMode: false, maxTokens: 400, cancellationToken).ConfigureAwait(false);
        _log.Write(CallRecord.Of(_endpoint, "ux-summary", "text", report.App, messages, [], reply, !reply.Failed, null));
        var cost = _endpoint.Cost(reply.PromptTokens ?? 0, reply.CompletionTokens ?? 0);
        var paragraph = reply.Failed || string.IsNullOrWhiteSpace(reply.Content)
            ? $"(The {_endpoint.Name} summary failed: {reply.Error ?? "an empty answer"}.)"
            : reply.Content.Trim();
        return (facts + Environment.NewLine + $"## In a paragraph (written by {_endpoint.Name}; the facts above are the record)" + Environment.NewLine + Environment.NewLine + paragraph + Environment.NewLine, cost);
    }
}

/// <summary>Judging a report after a matrix: every cell, with its trace read back from the report's folder.</summary>
public static class UxJudging
{
    public const string JudgementsFile = "judgements.json";

    public const string SummaryFile = "summary.md";

    /// <summary>Judges every cell, writes the summary, keeps both in the report, and writes them beside it when a folder is given.</summary>
    public static async Task<UxReport> RunAsync(UxReport report, UxJudge judge, UxSummary summary, string? folder = null, Action<UxJudgement>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var judgements = new List<UxJudgement>();
        foreach (var cell in report.Cells)
        {
            var run = cell.Run ?? Trace(folder, cell);
            var judgement = await judge.JudgeAsync(cell, run, cancellationToken).ConfigureAwait(false);
            judgements.Add(judgement);
            progress?.Invoke(judgement);
        }

        var (text, cost) = await summary.WriteAsync(report, judgements, cancellationToken).ConfigureAwait(false);
        var judged = report with { Judgements = judgements, Summary = text, JudgeCost = judgements.Any(j => j.Cost is null) || cost is null ? null : judgements.Sum(j => j.Cost!.Value) + cost.Value };
        if (folder is not null)
        {
            judged.Write(folder);
            File.WriteAllText(Path.Combine(folder, JudgementsFile), JsonSerializer.Serialize(judgements, UxAdapter.Json));
            File.WriteAllText(Path.Combine(folder, SummaryFile), text);
        }

        return judged;
    }

    /// <summary>A cell's trace from the report's folder, or null when there is none.</summary>
    public static AgentRun? Trace(string? folder, UxCell cell)
    {
        if (folder is null) return null;
        var path = Path.Combine(folder, "traces", UxReport.TraceName(cell));
        return File.Exists(path) ? AgentRun.ReadJsonl(path) : null;
    }
}
