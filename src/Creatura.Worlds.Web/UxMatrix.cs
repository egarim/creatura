using System.Globalization;
using System.Text;
using System.Text.Json;
using Creatura.Bench;

namespace Creatura.Worlds.Web;

/// <summary>Which cells a matrix runs: ids from the adapter (empty: every one), the testers, the barriers on in every cell.</summary>
public sealed record UxMatrixSpec
{
    public IReadOnlyList<string> Tasks { get; init; } = [];

    public IReadOnlyList<string> Personas { get; init; } = [];

    public IReadOnlyList<string> Environments { get; init; } = [];

    /// <summary>The testers by name (<see cref="UxSeats"/>); empty: the adapter's <c>limits.testers</c>.</summary>
    public IReadOnlyList<string> Testers { get; init; } = [];

    public IReadOnlyList<string> Barriers { get; init; } = [];

    /// <summary>The designs in the loop, by name (<see cref="UxDesigns"/>); empty: <c>plain</c> alone.</summary>
    public IReadOnlyList<string> Designs { get; init; } = [];

    /// <summary>What the personas keep between cells; nothing when null.</summary>
    public UxMemory? Memory { get; init; }

    /// <summary>Ticks per cell; the adapter's <c>limits.steps</c> when null.</summary>
    public int? MaxTicks { get; init; }

    /// <summary>What the whole matrix may spend; a cell is not started once the cells before it spent this much. The adapter's gate <c>maxCost</c> when null.</summary>
    public decimal? CostCap { get; init; }
}

/// <summary>
/// The full matrix: every task × persona × environment × tester × design, one cell at a time, each in a fresh browser context.
/// A cell that cannot run is recorded with its error and the matrix goes on; once the spend reaches the cap, the cells
/// left are recorded as not run.
/// </summary>
public static class UxMatrix
{
    /// <summary>The cells the spec names, in order: task, then persona, then environment, then tester, then design.</summary>
    public static IReadOnlyList<(UxTask Task, UxPersona Persona, UxEnvironment Environment, string Tester, string Design)> Cells(UxAdapter adapter, UxMatrixSpec spec)
    {
        var tasks = spec.Tasks.Count == 0 ? adapter.Tasks : spec.Tasks.Select(adapter.Task).ToList();
        var personas = spec.Personas.Count == 0 ? adapter.Personas : spec.Personas.Select(adapter.Persona).ToList();
        var environments = spec.Environments.Count == 0 ? adapter.Environments : spec.Environments.Select(adapter.Environment).ToList();
        var testers = spec.Testers.Count > 0 ? spec.Testers : adapter.Limits.Testers;
        if (testers.Count == 0) throw new ArgumentException("no testers: name them (--testers) or list them in the adapter's limits.testers");
        foreach (var b in spec.Barriers) adapter.Barrier(b);
        var designs = spec.Designs.Count == 0 ? [UxDesigns.Plain] : spec.Designs;
        if (designs.FirstOrDefault(d => !UxDesigns.Known(d)) is { } unknown) throw new ArgumentException($"unknown design '{unknown}'; known: {string.Join(", ", UxDesigns.Names)}");
        return [.. from t in tasks from p in personas from e in environments from s in testers from d in designs select (t, p, e, s, d)];
    }

    public static async Task<UxReport> RunAsync(WebBrowser browser, UxAdapter adapter, UxMatrixSpec spec, UxSeats seats,
        Action<UxCell>? progress = null, UxSessions? sessions = null, CancellationToken cancellationToken = default)
    {
        var cells = Cells(adapter, spec);
        sessions ??= new UxSessions();
        var (signInsBefore, reusedBefore) = (sessions.SignIns, sessions.Reused);
        var cap = spec.CostCap ?? adapter.Gate?.MaxCost;
        var results = new List<UxCell>();
        decimal spent = 0;
        var memory = spec.Memory;
        foreach (var (task, persona, environment, tester, design) in cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UxCell cell;
            if (cap is { } c && spent >= c)
            {
                cell = UxCell.Failed(task.Id, persona.Id, environment.Id, tester, string.Create(TraceText.Culture, $"cost cap {c:0.####} reached"), design);
            }
            else
            {
                // The persona's memory: its record (the design's carry) and its lessons, as they stand before this cell.
                var key = memory is null ? null : UxMemory.Key(persona.Id, tester, design);
                var store = memory?.ForCell();
                var lessons = key is null ? [] : memory!.Lessons(key);
                var casesBefore = key is null ? 0 : memory!.Cases(key);
                try
                {
                    cell = await UxRunner.RunAsync(browser, adapter, task, persona, environment, w => seats.Build(tester, adapter, task, persona, environment, w, lessons),
                        (p, t) => UxDesigns.Make(design, p, t, store, key),
                        new WebWorldOptions { Barriers = spec.Barriers, Sessions = sessions }, spec.MaxTicks, cancellationToken).ConfigureAwait(false);
                    cell = cell with { Tester = tester, Design = design, Memory = key, CasesBefore = casesBefore, Remembered = lessons };
                    spent += cell.Cost ?? 0;
                    if (key is not null) memory!.Remember(key, cell, environment);
                }
                catch (Exception e) when (WebBrowser.IsBrowserFailure(e))
                {
                    cell = UxCell.Failed(task.Id, persona.Id, environment.Id, tester, e.Message.Split('\n')[0], design);
                }
            }

            results.Add(cell);
            progress?.Invoke(cell);
        }

        return new UxReport
        {
            App = adapter.App, At = DateTimeOffset.UtcNow, Design = string.Join(", ", spec.Designs.Count == 0 ? [UxDesigns.Plain] : spec.Designs),
            Memory = memory?.Mode ?? UxMemoryMode.Off, Barriers = spec.Barriers, Gate = adapter.Gate, Cells = results,
            SignIns = sessions.SignIns - signInsBefore, SessionsReused = sessions.Reused - reusedBefore,
        };
    }
}

/// <summary>
/// One matrix run: the cells and what they add up to. <see cref="Write"/> leaves a folder: <c>report.json</c> (what
/// compare and gate read), <c>cells.csv</c> (one row per cell), <c>report.md</c> (for a person) and <c>traces/</c> (each
/// cell's run as JSON lines, the format the goldens use).
/// </summary>
public sealed record UxReport
{
    public const string File = "report.json";

    public required string App { get; init; }

    public DateTimeOffset At { get; init; }

    /// <summary>The designs the matrix ran, joined with commas.</summary>
    public string Design { get; init; } = "plain";

    /// <summary>Whether the personas remembered between cells: a report with memory compares only with one that started from the same records.</summary>
    public UxMemoryMode Memory { get; init; }

    public IReadOnlyList<string> Barriers { get; init; } = [];

    /// <summary>The adapter's gate when the matrix ran, so a gate can be read from the report alone.</summary>
    public UxGate? Gate { get; init; }

    public IReadOnlyList<UxCell> Cells { get; init; } = [];

    /// <summary>How many times an account signed in during the matrix: once per account, unless a session expired.</summary>
    public int SignIns { get; init; }

    /// <summary>How many cells opened with a kept session instead of signing in.</summary>
    public int SessionsReused { get; init; }

    public int Ran => Cells.Count(c => c.Error is null);

    public int Errors => Cells.Count(c => c.Error is not null);

    public int Done => Cells.Count(c => c.DataDone);

    /// <summary>Done over the cells that ran; 0 when none ran.</summary>
    public double DoneRate => Ran == 0 ? 0 : (double)Done / Ran;

    public int BelievedNotDone => Cells.Count(c => c.BelievedNotDone);

    public int PageSaidNotDone => Cells.Count(c => c.PageSaidNotDone);

    public int UncaughtLies => Cells.Count(c => c.UncaughtLie);

    public int LiesCaught => Cells.Sum(c => c.LiesCaught);

    public int Escalations => Cells.Sum(c => c.Escalations);

    /// <summary>The spend; null when any cell's price was unknown.</summary>
    public decimal? Cost => Cells.Any(c => c.Error is null && c.Cost is null) ? null : Cells.Sum(c => c.Cost ?? 0);

    public string Totals() => string.Create(TraceText.Culture,
        $"{Done} of {Ran} cells done ({DoneRate:P0}); {BelievedNotDone} believed done but not; {PageSaidNotDone} the page said done but not; " +
        $"{UncaughtLies} lie{(UncaughtLies == 1 ? "" : "s")} not caught; {LiesCaught} caught; {Errors} not run; cost {(Cost is { } c ? c.ToString("0.####", TraceText.Culture) : "unknown")}")
        + (SignIns + SessionsReused == 0 ? "" : $"; {SignIns} sign-in{(SignIns == 1 ? "" : "s")}, {SessionsReused} session{(SessionsReused == 1 ? "" : "s")} reused");

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# UX matrix: {App}");
        sb.AppendLine();
        sb.AppendLine(string.Create(TraceText.Culture, $"{At:yyyy-MM-dd HH:mm} UTC; design {Design}; barriers {(Barriers.Count == 0 ? "none" : string.Join(", ", Barriers))}."));
        sb.AppendLine();
        sb.AppendLine(Totals() + ".");
        sb.AppendLine();
        if (Memory != UxMemoryMode.Off) { sb.AppendLine($"Memory: {Memory.ToString().ToLowerInvariant()} (personas remembered earlier cells; the order of the cells matters)."); sb.AppendLine(); }
        if (ByDesign().Count > 1)
        {
            sb.AppendLine("| design | done | believed done, not | page said done, not | lies not caught | lies caught | mean ticks (done) | cost |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var d in ByDesign())
                sb.AppendLine(string.Create(TraceText.Culture, $"| {d.Design} | {d.Done} of {d.Ran} | {d.BelievedNotDone} | {d.PageSaidNotDone} | {d.UncaughtLies} | {d.LiesCaught} | {(d.MeanTicksDone is { } m ? m.ToString("0.#", TraceText.Culture) : "")} | {(d.Cost is { } k ? k.ToString("0.####", TraceText.Culture) : "unknown")} |"));
            sb.AppendLine();
        }

        sb.AppendLine("| task | persona | env | tester | data | page said done | believed done | ticks | lies caught | escalations | cost | note |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var c in Cells)
        {
            var note = c.Error ?? (c.UncaughtLie ? "lie not caught" : c.BelievedNotDone ? "believed done, not done" : c.PageSaidNotDone ? "page said done, not done" : "");
            sb.AppendLine(string.Create(TraceText.Culture,
                $"| {c.Task} | {c.Persona} | {c.Environment} | {c.Tester} | {(c.Error is not null ? "not run" : c.DataDone ? "done" : "not done")} | {(c.PageSaidDone ? "yes" : "")} | {(c.BelievedDoneAt is { } t ? $"tick {t}" : "")} | {c.Ticks} | {c.LiesCaught} | {c.Escalations} | {(c.Cost is { } k && k > 0 ? k.ToString("0.####", TraceText.Culture) : "")} | {note} |"));
        }

        return sb.ToString();
    }

    /// <summary>The totals for each design, in the order the matrix ran them: how the control mechanisms compare on the same cells.</summary>
    public IReadOnlyList<UxDesignTotals> ByDesign() => Cells.GroupBy(c => c.Design).Select(g =>
    {
        var ran = g.Where(c => c.Error is null).ToList();
        var done = ran.Where(c => c.DataDone).ToList();
        return new UxDesignTotals(g.Key, ran.Count, done.Count, ran.Count(c => c.BelievedNotDone), ran.Count(c => c.PageSaidNotDone), ran.Count(c => c.UncaughtLie),
            ran.Sum(c => c.LiesCaught), done.Count == 0 ? null : done.Average(c => c.Ticks), ran.Any(c => c.Cost is null) ? null : ran.Sum(c => c.Cost ?? 0));
    }).ToList();

    public const string CsvHeader = "task,persona,environment,tester,design,seat,agent,barriers,data_done,page_said_done,believed_done_at,ticks,lies_told,lies_caught,escalations,cost,uncaught_lie,error,check";

    public IEnumerable<string> CsvLines() => Cells.Select(c => string.Join(',', new[]
    {
        c.Task, c.Persona, c.Environment, c.Tester, c.Design, c.Seat, c.Agent, string.Join('+', c.Barriers), c.DataDone ? "1" : "0", c.PageSaidDone ? "1" : "0",
        c.BelievedDoneAt?.ToString(TraceText.Culture) ?? "", c.Ticks.ToString(TraceText.Culture), c.LiesCaught.ToString(TraceText.Culture),
        c.Escalations.ToString(TraceText.Culture), c.Cost?.ToString(TraceText.Culture) ?? "", c.UncaughtLie ? "1" : "0", c.Error ?? "", c.CheckDetail,
    }.Select(Csv)));

    private static string Csv(string s) => s.IndexOfAny([',', '"', '\n', '\r']) < 0 ? s : "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>The cell's trace file under <c>traces/</c>: its key, slugged whole.</summary>
    public static string TraceName(UxCell cell) => Observation.Slug(cell.Key.Replace('|', '-'), max: 200) + ".jsonl";

    /// <summary>Writes the folder; returns the path of <c>report.json</c>.</summary>
    public string Write(string folder)
    {
        Directory.CreateDirectory(folder);
        var traces = Path.Combine(folder, "traces");
        Directory.CreateDirectory(traces);
        foreach (var c in Cells.Where(c => c.Run is not null))
        {
            c.Run!.WriteJsonl(Path.Combine(traces, TraceName(c)));
        }

        var csv = Path.Combine(folder, "cells.csv");
        if (System.IO.File.Exists(csv)) System.IO.File.Delete(csv);
        foreach (var line in CsvLines()) AppendOnlyCsv.Append(csv, CsvHeader, line);
        System.IO.File.WriteAllText(Path.Combine(folder, "report.md"), ToMarkdown());
        var json = Path.Combine(folder, File);
        System.IO.File.WriteAllText(json, JsonSerializer.Serialize(this, UxAdapter.Json));
        return json;
    }

    /// <summary>Reads a report from its folder or its <c>report.json</c>.</summary>
    public static UxReport Load(string path)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, File) : path;
        return JsonSerializer.Deserialize<UxReport>(System.IO.File.ReadAllText(file), UxAdapter.Json) ?? throw new FormatException($"{file} is empty");
    }
}

/// <summary>One design's totals in a report.</summary>
public sealed record UxDesignTotals(string Design, int Ran, int Done, int BelievedNotDone, int PageSaidNotDone, int UncaughtLies, int LiesCaught, double? MeanTicksDone, decimal? Cost)
{
    public string Line() => string.Create(TraceText.Culture,
        $"{Design}: {Done} of {Ran} done; {UncaughtLies} lie{(UncaughtLies == 1 ? "" : "s")} not caught, {LiesCaught} caught; {BelievedNotDone} believed done but not")
        + (MeanTicksDone is { } m ? string.Create(TraceText.Culture, $"; {m:0.#} ticks a done cell") : "");
}

/// <summary>Two runs of a matrix, cell by cell, joined on the cell's key (task, persona, environment, tester, design).</summary>
public sealed record UxComparison
{
    public required UxReport Before { get; init; }

    public required UxReport After { get; init; }

    /// <summary>Done before, not done after.</summary>
    public IReadOnlyList<string> Regressed { get; init; } = [];

    /// <summary>Not done before, done after.</summary>
    public IReadOnlyList<string> Fixed { get; init; } = [];

    /// <summary>A gap after (believed or the page said done, data not; or a lie not caught) that was not there before.</summary>
    public IReadOnlyList<string> NewGaps { get; init; } = [];

    /// <summary>Done both times, in more ticks after.</summary>
    public IReadOnlyList<string> Slower { get; init; } = [];

    public IReadOnlyList<string> OnlyBefore { get; init; } = [];

    public IReadOnlyList<string> OnlyAfter { get; init; } = [];

    public static UxComparison Of(UxReport before, UxReport after)
    {
        var a = before.Cells.Where(c => c.Error is null).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
        var b = after.Cells.Where(c => c.Error is null).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
        var both = a.Keys.Intersect(b.Keys).Order(StringComparer.Ordinal).ToList();
        static bool Gap(UxCell c) => c.BelievedNotDone || c.PageSaidNotDone || c.UncaughtLie;
        static string Gaps(UxCell c) => string.Join(", ", new[] { c.UncaughtLie ? "lie not caught" : null, c.BelievedNotDone ? "believed done" : null, c.PageSaidNotDone ? "page said done" : null }.OfType<string>());
        return new UxComparison
        {
            Before = before,
            After = after,
            Regressed = both.Where(k => a[k].DataDone && !b[k].DataDone).ToList(),
            Fixed = both.Where(k => !a[k].DataDone && b[k].DataDone).ToList(),
            NewGaps = both.Where(k => Gap(b[k]) && Gaps(b[k]) != Gaps(a[k])).Select(k => $"{k} ({Gaps(b[k])})").ToList(),
            Slower = both.Where(k => a[k].DataDone && b[k].DataDone && b[k].Ticks > a[k].Ticks).Select(k => string.Create(TraceText.Culture, $"{k} ({a[k].Ticks} → {b[k].Ticks} ticks)")).ToList(),
            OnlyBefore = a.Keys.Except(b.Keys).Order(StringComparer.Ordinal).ToList(),
            OnlyAfter = b.Keys.Except(a.Keys).Order(StringComparer.Ordinal).ToList(),
        };
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# UX compare: {After.App}");
        sb.AppendLine();
        sb.AppendLine(string.Create(TraceText.Culture, $"- before ({Before.At:yyyy-MM-dd HH:mm}): {Before.Totals()}"));
        sb.AppendLine(string.Create(TraceText.Culture, $"- after ({After.At:yyyy-MM-dd HH:mm}): {After.Totals()}"));
        Section(sb, "Regressed (done before, not after)", Regressed);
        Section(sb, "Fixed (not done before, done after)", Fixed);
        Section(sb, "New gaps", NewGaps);
        Section(sb, "Slower", Slower);
        Section(sb, "Only in before", OnlyBefore);
        Section(sb, "Only in after", OnlyAfter);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        sb.AppendLine();
        sb.AppendLine(string.Create(TraceText.Culture, $"## {title}: {items.Count}"));
        foreach (var item in items) sb.AppendLine($"- {item}");
    }
}

/// <summary>
/// The CI gate: thresholds on a report, and on its comparison with a baseline when one is given. Every threshold is
/// optional; one left out is not checked. In the adapter as <c>gate</c>.
/// </summary>
public sealed record UxGate
{
    /// <summary>The least share of the cells that ran whose data says done (0 to 1).</summary>
    public double? MinDone { get; init; }

    /// <summary>The most cells where the model believed it was done and the data says not.</summary>
    public int? MaxBelievedNotDone { get; init; }

    /// <summary>The most cells where the page said done and the data says not (with a lie barrier on, every cell the persona saved in).</summary>
    public int? MaxPageSaidNotDone { get; init; }

    /// <summary>The most cells where the page said done, the data says not, and the run caught no lie.</summary>
    public int? MaxUncaughtLies { get; init; }

    /// <summary>The most the matrix may cost; also the matrix's cost cap.</summary>
    public decimal? MaxCost { get; init; }

    /// <summary>The most cells that could not run. Zero when left out: a matrix that did not run is not a pass.</summary>
    public int? MaxErrors { get; init; } = 0;

    /// <summary>The most cells done in the baseline and not now (needs a baseline).</summary>
    public int? MaxRegressions { get; init; } = 0;

    /// <summary>The thresholds broken, each as one line; empty is a pass.</summary>
    public IReadOnlyList<string> Breaches(UxReport report, UxReport? baseline = null)
    {
        var breaches = new List<string>();
        void Check(bool broken, string line)
        {
            if (broken) breaches.Add(line);
        }

        var ic = TraceText.Culture;
        if (MinDone is { } minDone) Check(report.DoneRate < minDone, string.Create(ic, $"done {report.DoneRate:P0} < {minDone:P0}"));
        if (MaxBelievedNotDone is { } b) Check(report.BelievedNotDone > b, string.Create(ic, $"believed done but not: {report.BelievedNotDone} > {b}"));
        if (MaxPageSaidNotDone is { } p) Check(report.PageSaidNotDone > p, string.Create(ic, $"the page said done but not: {report.PageSaidNotDone} > {p}"));
        if (MaxUncaughtLies is { } u) Check(report.UncaughtLies > u, string.Create(ic, $"lies not caught: {report.UncaughtLies} > {u}"));
        if (MaxCost is { } cost) Check(report.Cost is not { } spent || spent > cost, report.Cost is { } s ? string.Create(ic, $"cost {s:0.####} > {cost:0.####}") : "cost unknown, with a maxCost set");
        if (MaxErrors is { } e) Check(report.Errors > e, string.Create(ic, $"cells not run: {report.Errors} > {e}"));
        if (MaxRegressions is { } r && baseline is not null)
        {
            var regressed = UxComparison.Of(baseline, report).Regressed;
            Check(regressed.Count > r, string.Create(ic, $"regressions: {regressed.Count} > {r} ({string.Join("; ", regressed)})"));
        }

        if (report.Ran == 0) breaches.Add("no cell ran");
        return breaches;
    }
}
