using System.Globalization;
using Creatura.Seats;
using Creatura.Worlds;
using Creatura.Worlds.Web;

namespace Creatura.Cli;

/// <summary>
/// <c>creatura ux</c>: web worlds (docs/design-web-worlds.md). A persona uses a real web app through Playwright, and the
/// app's own data says whether the task was done. <c>run</c> is one cell; <c>matrix</c> is every task × persona ×
/// environment × tester, written as a report; <c>compare</c> reads two reports cell by cell; <c>gate</c> holds a report to
/// thresholds, for CI. <c>--demo</c> serves the bundled notes app on 127.0.0.1 and drives it with the bundled script, so
/// it runs with no backend and no model; an adapter file runs any app, with models as the testers (they cost money, so
/// they are never chosen for you). Exit codes: 0 ran (and the gate held, when asked), 1 the gate did not hold, 2 a usage
/// error, 3 no browser could be started.
/// </summary>
public static class UxCommand
{
    public const string Usage = """
        usage: creatura ux run (--demo | --adapter <file.json> --seat model:<name>) [options]
               creatura ux matrix (--demo | --adapter <file.json>) [options] [--out <dir>] [--baseline <report>] [--gate]
               creatura ux compare <before report> <after report>
               creatura ux gate <report> [--baseline <report>] [thresholds]
               creatura ux list (--demo | --adapter <file.json>)
               creatura ux adapter          print the demo's adapter, to start a file of your own from

        the app:
          --demo                       the bundled notes app, served on 127.0.0.1 by this command; offline
          --adapter <file.json>        an app's adapter: personas, environments, tasks with their data checks, barriers, gate
          --base-url <url>             where the app is served (overrides the adapter's baseUrl)

        run (one cell; default: the first of each):
          --task <id> --persona <id> --env <id>
          --seat script:<path>|script:demo|model:<name>
                                       the tester: a script of actions by control name, or a catalogued model
                                       (default for --demo: script:demo; an adapter needs one named)
          --trace                      every row of the trace

        matrix (every cell named; default: all of each):
          --tasks <ids> --personas <ids> --envs <ids>
                                       ids joined with +, or all
          --testers <a,b>              testers, comma-separated (default: the adapter's limits.testers)
          --max-cost <n>               stop starting cells once the matrix has spent this (default: the gate's maxCost)
          --out <dir>                  where the report goes (default: runs/ux/<app>-<time>)
          --baseline <report>          compare with an earlier report, and gate its regressions
          --gate                       hold the report to the adapter's gate; exit 1 if it breaks

        both:
          --barriers none|<ids>        barriers between the browser and the app, joined with +, e.g. save-lies
          --escalate <name>            the stronger model asked when a persona is stuck (default: limits.models.escalate)
          --design plain|<name>        the design in the loop (default plain: the control layer alone); creatura run --list
          --ticks <n>                  at most n steps per cell (default: the adapter's limits.steps)
          --log <path>                 the model calls, one JSON line each
          --sessions <dir>             keep each account's signed-in session here and reuse it next time (owner-only files
                                       that hold tokens: keep the folder out of the repository); default: this run only
          --browser <path>             a Chromium to launch instead of Playwright's own
          --headed                     show the browser

        gate thresholds (override the report's gate):
          --min-done <0..1> --max-believed-not-done <n> --max-page-said-not-done <n> --max-uncaught-lies <n>
          --max-cost <n> --max-errors <n> --max-regressions <n>
        """;

    private static readonly string[] Flags = ["--demo", "--headed", "--trace", "--gate"];

    private static readonly string[] Thresholds = ["--min-done", "--max-believed-not-done", "--max-page-said-not-done", "--max-uncaught-lies", "--max-cost", "--max-errors", "--max-regressions"];

    public static async Task<int> Run(string[] args, TextWriter output, TextWriter error)
    {
        var verb = args.Length > 1 ? args[1] : null;
        if (verb is null or "--help" or "-h" || args.Contains("--help") || args.Contains("-h"))
        {
            (verb is null ? error : output).WriteLine(Usage);
            return verb is null ? 2 : 0;
        }

        if (verb == "adapter")
        {
            output.WriteLine(DemoFiles.AdapterJson);
            return 0;
        }

        if (verb is not ("run" or "list" or "matrix" or "compare" or "gate")) return Fail(error, $"unknown ux command: {verb}");

        var flags = new HashSet<string>(StringComparer.Ordinal);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            if (Flags.Contains(args[i])) { flags.Add(args[i]); continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(args[i]); continue; }
            if (i + 1 >= args.Length) return Fail(error, $"{args[i]} needs a value");
            options[args[i]] = args[++i];
        }

        string[] known = verb switch
        {
            "run" => ["--adapter", "--base-url", "--task", "--persona", "--env", "--barriers", "--seat", "--escalate", "--design", "--ticks", "--log", "--browser", "--sessions"],
            "matrix" => ["--adapter", "--base-url", "--tasks", "--personas", "--envs", "--testers", "--barriers", "--escalate", "--design", "--ticks", "--log",
                         "--browser", "--max-cost", "--out", "--baseline", "--sessions"],
            "gate" => ["--baseline", .. Thresholds],
            "compare" => [],
            _ => ["--adapter", "--base-url"],
        };
        var unknown = options.Keys.Except(known).ToList();
        if (unknown.Count > 0) return Fail(error, $"unknown option for ux {verb}: {string.Join(", ", unknown)}");
        if (verb is "compare") return positional.Count == 2 ? Compare(positional[0], positional[1], output, error) : Fail(error, "ux compare takes two reports: before and after");
        if (verb is "gate") return positional.Count == 1 ? Gate(positional[0], options, output, error) : Fail(error, "ux gate takes one report");
        if (positional.Count > 0) return Fail(error, $"unexpected argument: {positional[0]}");

        var demo = flags.Contains("--demo");
        if (demo == options.ContainsKey("--adapter")) return Fail(error, "name one app: --demo or --adapter <file.json>");

        await using var app = demo ? DemoApp.Start() : null;
        UxAdapter adapter;
        try
        {
            adapter = app is not null ? app.Adapter() : UxAdapter.Load(options["--adapter"]);
            if (options.TryGetValue("--base-url", out var baseUrl)) adapter = adapter with { BaseUrl = baseUrl };
            if (string.IsNullOrWhiteSpace(adapter.BaseUrl)) return Fail(error, "the adapter has no baseUrl: give --base-url");
        }
        catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return Fail(error, $"the adapter could not be read: {e.Message}");
        }

        if (verb == "list")
        {
            List(adapter, output);
            return 0;
        }

        var ticks = adapter.Limits.Steps;
        if (options.TryGetValue("--ticks", out var t) && (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) || ticks < 1))
            return Fail(error, "--ticks takes a whole number of at least 1");

        var design = options.GetValueOrDefault("--design") ?? "plain";
        if (design != "plain" && AgentCatalog.TryFind(design) is null) return Fail(error, $"unknown design '{design}'; known: plain, {string.Join(", ", AgentCatalog.Names)}");
        Func<IPolicy, AgentTask, IAgent> agent = design == "plain" ? PlainLoop.Make : (p, k) => AgentCatalog.Make(design, p, k);

        var set = options.GetValueOrDefault("--barriers") ?? "none";
        var barriers = set == "none" ? new List<string>() : set.Split('+').ToList();
        var seats = new UxSeats(log: options.TryGetValue("--log", out var logPath) ? new CallLog(logPath) : null) { Escalate = options.GetValueOrDefault("--escalate") };
        try
        {
            foreach (var id in barriers) adapter.Barrier(id);
        }
        catch (ArgumentException e)
        {
            return Fail(error, e.Message);
        }

        return verb == "run"
            ? await RunOne(adapter, options, flags, demo, barriers, seats, agent, design, ticks, output, error)
            : await RunMatrix(adapter, options, flags, demo, barriers, seats, agent, design, ticks, output, error);
    }

    private static async Task<int> RunOne(UxAdapter adapter, Dictionary<string, string> options, HashSet<string> flags, bool demo, List<string> barriers,
        UxSeats seats, Func<IPolicy, AgentTask, IAgent> agent, string design, int ticks, TextWriter output, TextWriter error)
    {
        UxTask task;
        UxPersona persona;
        UxEnvironment environment;
        try
        {
            task = adapter.Task(options.GetValueOrDefault("--task"));
            persona = adapter.Persona(options.GetValueOrDefault("--persona"));
            environment = adapter.Environment(options.GetValueOrDefault("--env"));
        }
        catch (ArgumentException e)
        {
            return Fail(error, e.Message);
        }

        // The seat needs the world's screen, so it is built when the world opens; what can be wrong with it is found first.
        var seat = options.GetValueOrDefault("--seat") ?? (demo ? UxSeats.BundledScript : null);
        if (seat is null) return Fail(error, "name the persona's seat: --seat model:<name> (a model costs money) or --seat script:<file>");
        if (seats.Check(seat, adapter) is { } problem) return Fail(error, problem);

        output.WriteLine($"{adapter.App}: {task.Id} × {persona.Id} × {environment.Id} ({environment.Width}x{environment.Height}{(environment.Mobile ? ", mobile" : "")}); " +
            $"barriers: {(barriers.Count == 0 ? "none" : string.Join(", ", barriers))}; seat: {seat}; design: {design}; up to {ticks} steps");
        if (UxSeats.IsModel(seat))
            output.WriteLine("the persona is a model: each step is a call to it, at its price" + (adapter.Limits.Cost is { } cap ? string.Create(CultureInfo.InvariantCulture, $", at most {cap:0.####} spent") : ""));

        if (await Launch(options, flags, error) is not { } browser) return 3;
        UxCell cell;
        await using (browser)
        {
            try
            {
                cell = await InCulture(() => UxRunner.RunAsync(browser, adapter, task, persona, environment, w => seats.Build(seat, adapter, task, persona, environment, w), agent,
                    new WebWorldOptions { Barriers = barriers, Sessions = Sessions(options) }, ticks));
            }
            catch (Exception e) when (WebBrowser.IsBrowserFailure(e))
            {
                error.WriteLine($"the app could not be opened at {adapter.Resolve(task.Start)}: {e.Message.Split('\n')[0]}");
                return 3;
            }
        }

        var run = cell.Run!;
        output.WriteLine();
        output.WriteLine($"  {"tick",-5}{"level",-10}{"action",-24}{"kind",-13}detail");
        var rows = flags.Contains("--trace") ? run.Trace : run.Trace.Where(r => r.Kind != TraceKind.Idle).Take(16).ToList();
        foreach (var row in rows) output.WriteLine($"  {row.Tick,-5}{row.Level,-10}{row.Action,-24}{row.Kind,-13}{row.Detail}");
        var hidden = run.Trace.Count(r => r.Kind != TraceKind.Idle) - rows.Count(r => r.Kind != TraceKind.Idle);
        if (hidden > 0) output.WriteLine($"  ... {hidden} more (--trace shows every row)");
        output.WriteLine();
        output.WriteLine(cell.Summary());
        if (cell.FalseDone) output.WriteLine("  · said done, not done: the gap a judge reads");
        return 0;
    }

    private static async Task<int> RunMatrix(UxAdapter adapter, Dictionary<string, string> options, HashSet<string> flags, bool demo, List<string> barriers,
        UxSeats seats, Func<IPolicy, AgentTask, IAgent> agent, string design, int ticks, TextWriter output, TextWriter error)
    {
        static IReadOnlyList<string> Ids(string? s) => s is null or "all" ? [] : s.Split('+');
        decimal? maxCost = null;
        if (options.TryGetValue("--max-cost", out var mc))
        {
            if (!decimal.TryParse(mc, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) || m < 0) return Fail(error, "--max-cost takes a number");
            maxCost = m;
        }

        var testers = options.TryGetValue("--testers", out var ts) ? ts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : adapter.Limits.Testers.Count == 0 && demo ? [UxSeats.BundledScript] : [];
        var spec = new UxMatrixSpec
        {
            Tasks = Ids(options.GetValueOrDefault("--tasks")), Personas = Ids(options.GetValueOrDefault("--personas")), Environments = Ids(options.GetValueOrDefault("--envs")),
            Testers = testers, Barriers = barriers, MaxTicks = ticks, CostCap = maxCost,
        };

        IReadOnlyList<(UxTask Task, UxPersona Persona, UxEnvironment Environment, string Tester)> cells;
        try
        {
            cells = UxMatrix.Cells(adapter, spec);
        }
        catch (ArgumentException e)
        {
            return Fail(error, e.Message);
        }

        foreach (var tester in cells.Select(c => c.Tester).Distinct())
        {
            if (seats.Check(tester, adapter) is { } problem) return Fail(error, problem);
        }

        UxReport? baseline = null;
        if (options.TryGetValue("--baseline", out var basePath))
        {
            if (Read(basePath, error) is not { } b) return 2;
            baseline = b;
        }

        if (flags.Contains("--gate") && adapter.Gate is null) return Fail(error, "--gate needs a gate in the adapter");
        var outDir = options.GetValueOrDefault("--out")
            ?? Path.Combine("runs", "ux", $"{Observation.Slug(adapter.App)}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}");
        var models = cells.Count(c => UxSeats.IsModel(c.Tester));
        static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
        output.WriteLine($"{adapter.App}: {Count(cells.Count, "cell")} ({Count(cells.Select(c => c.Task.Id).Distinct().Count(), "task")} × " +
            $"{Count(cells.Select(c => c.Persona.Id).Distinct().Count(), "persona")} × {Count(cells.Select(c => c.Environment.Id).Distinct().Count(), "environment")} × " +
            $"{Count(cells.Select(c => c.Tester).Distinct().Count(), "tester")}); barriers: {(barriers.Count == 0 ? "none" : string.Join(", ", barriers))}; design: {design}; up to {ticks} steps a cell");
        if (models > 0)
        {
            var cap = maxCost ?? adapter.Gate?.MaxCost;
            output.WriteLine($"{models} cell{(models == 1 ? "" : "s")} with a model as the persona: each step is a call, at its price; " +
                (cap is { } c ? string.Create(CultureInfo.InvariantCulture, $"no cell starts once {c:0.####} is spent") : "no cap on the matrix (--max-cost sets one)"));
        }

        if (await Launch(options, flags, error) is not { } browser) return 3;
        UxReport report;
        await using (browser)
        {
            report = await InCulture(() => UxMatrix.RunAsync(browser, adapter, spec, seats, agent, design,
                cell => output.WriteLine("  " + cell.Summary()), Sessions(options)));
        }

        var json = report.Write(outDir);
        output.WriteLine();
        output.WriteLine(report.Totals());
        output.WriteLine($"report: {json} (and cells.csv, report.md, traces/ beside it)");
        if (baseline is not null)
        {
            var comparison = UxComparison.Of(baseline, report);
            output.WriteLine($"against the baseline: {comparison.Regressed.Count} regressed, {comparison.Fixed.Count} fixed, {comparison.NewGaps.Count} new gaps, {comparison.Slower.Count} slower");
            foreach (var key in comparison.Regressed) output.WriteLine($"  regressed: {key}");
        }

        if (!flags.Contains("--gate")) return 0;
        return Verdict(adapter.Gate!.Breaches(report, baseline), output);
    }

    /// <summary>Where signed-in sessions are kept between runs (<c>--sessions</c>); in memory, for this run only, without it.</summary>
    private static UxSessions? Sessions(Dictionary<string, string> options) =>
        options.TryGetValue("--sessions", out var folder) ? new UxSessions(folder) : null;

    private static int Compare(string before, string after, TextWriter output, TextWriter error)
    {
        if (Read(before, error) is not { } a || Read(after, error) is not { } b) return 2;
        output.Write(UxComparison.Of(a, b).ToMarkdown());
        return 0;
    }

    private static int Gate(string path, Dictionary<string, string> options, TextWriter output, TextWriter error)
    {
        // The thresholds first, so a mistyped flag is said before any file is read; then they override the report's gate.
        var overrides = new UxGate { MaxErrors = null, MaxRegressions = null };
        var ic = CultureInfo.InvariantCulture;   // numbers as typed: 0.9, never 0,9
        foreach (var name in Thresholds.Where(options.ContainsKey))
        {
            var text = options[name];
            if (name is "--min-done" or "--max-cost")
            {
                if (!decimal.TryParse(text, NumberStyles.Number, ic, out var d) || d < 0 || (name == "--min-done" && d > 1)) return Fail(error, $"{name} takes a number{(name == "--min-done" ? " from 0 to 1" : "")}");
                overrides = name == "--min-done" ? overrides with { MinDone = (double)d } : overrides with { MaxCost = d };
                continue;
            }

            if (!int.TryParse(text, NumberStyles.None, ic, out var n)) return Fail(error, $"{name} takes a whole number");
            overrides = name switch
            {
                "--max-believed-not-done" => overrides with { MaxBelievedNotDone = n },
                "--max-page-said-not-done" => overrides with { MaxPageSaidNotDone = n },
                "--max-uncaught-lies" => overrides with { MaxUncaughtLies = n },
                "--max-errors" => overrides with { MaxErrors = n },
                _ => overrides with { MaxRegressions = n },
            };
        }

        if (Read(path, error) is not { } report) return 2;
        UxReport? baseline = null;
        if (options.TryGetValue("--baseline", out var basePath))
        {
            if (Read(basePath, error) is not { } b) return 2;
            baseline = b;
        }

        var start = report.Gate ?? new UxGate();
        var gate = start with
        {
            MinDone = overrides.MinDone ?? start.MinDone,
            MaxBelievedNotDone = overrides.MaxBelievedNotDone ?? start.MaxBelievedNotDone,
            MaxPageSaidNotDone = overrides.MaxPageSaidNotDone ?? start.MaxPageSaidNotDone,
            MaxUncaughtLies = overrides.MaxUncaughtLies ?? start.MaxUncaughtLies,
            MaxCost = overrides.MaxCost ?? start.MaxCost,
            MaxErrors = overrides.MaxErrors ?? start.MaxErrors,
            MaxRegressions = overrides.MaxRegressions ?? start.MaxRegressions,
        };

        output.WriteLine(report.Totals());
        return Verdict(gate.Breaches(report, baseline), output);
    }

    private static int Verdict(IReadOnlyList<string> breaches, TextWriter output)
    {
        if (breaches.Count == 0)
        {
            output.WriteLine("gate: held");
            return 0;
        }

        output.WriteLine("gate: broken");
        foreach (var b in breaches) output.WriteLine($"  · {b}");
        return 1;
    }

    private static UxReport? Read(string path, TextWriter error)
    {
        try
        {
            return UxReport.Load(path);
        }
        catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            error.WriteLine($"the report '{path}' could not be read: {e.Message}");
            return null;
        }
    }

    private static async Task<WebBrowser?> Launch(Dictionary<string, string> options, HashSet<string> flags, TextWriter error)
    {
        try
        {
            return await WebBrowser.LaunchAsync(!flags.Contains("--headed"), options.GetValueOrDefault("--browser"));
        }
        catch (Exception e) when (WebBrowser.IsBrowserFailure(e))
        {
            error.WriteLine($"no browser could be started: {e.Message.Split('\n')[0]}");
            error.WriteLine(e.Message.Contains("Driver not found", StringComparison.Ordinal)
                ? "this creatura is a single file, which cannot carry Playwright's driver: run creatura ux from the .NET tool (dotnet tool install -g Creatura.Cli) or with dotnet run --project src/Creatura.Cli"
                : "install Chromium for Playwright (pwsh playwright.ps1 install chromium, beside Creatura.Worlds.Web.dll), or pass --browser <path to chromium>");
            return null;
        }
    }

    /// <summary>Runs in Creatura's own culture, so a run reads the same on every machine (as creatura run does).</summary>
    private static async Task<T> InCulture<T>(Func<Task<T>> body)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = TraceText.Culture;
            return await body();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    private static void List(UxAdapter adapter, TextWriter output)
    {
        output.WriteLine($"{adapter.App} at {adapter.BaseUrl}");
        output.WriteLine("tasks:");
        foreach (var t in adapter.Tasks) output.WriteLine($"  {t.Id,-20}{t.Prompt}");
        output.WriteLine("personas:");
        foreach (var p in adapter.Personas) output.WriteLine($"  {p.Id,-20}{p.Description}");
        output.WriteLine("environments:");
        foreach (var e in adapter.Environments) output.WriteLine($"  {e.Id,-20}{e.Width}x{e.Height}{(e.Mobile ? ", mobile" : "")}");
        output.WriteLine("testers: " + (adapter.Limits.Testers.Count == 0 ? "(none named)" : string.Join(", ", adapter.Limits.Testers)));
        output.WriteLine("barriers:");
        if (adapter.Barriers.Count == 0) output.WriteLine("  (none)");
        foreach (var b in adapter.Barriers) output.WriteLine($"  {b.Id,-20}{b.Kind.ToString().ToLowerInvariant()} {b.Method ?? "any"} {b.Url}");
        if (adapter.Gate is { } g)
        {
            output.WriteLine("gate: " + string.Join(", ", new[]
            {
                g.MinDone is { } d ? string.Create(TraceText.Culture, $"done ≥ {d:P0}") : null,
                g.MaxBelievedNotDone is { } b ? $"believed done but not ≤ {b}" : null,
                g.MaxPageSaidNotDone is { } p ? $"page said done but not ≤ {p}" : null,
                g.MaxUncaughtLies is { } u ? $"lies not caught ≤ {u}" : null,
                g.MaxCost is { } c ? string.Create(CultureInfo.InvariantCulture, $"cost ≤ {c:0.####}") : null,
                g.MaxErrors is { } e ? $"not run ≤ {e}" : null,
                g.MaxRegressions is { } r ? $"regressions ≤ {r} (with a baseline)" : null,
            }.OfType<string>()));
        }
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine("creatura ux --help for the options");
        return 2;
    }
}
