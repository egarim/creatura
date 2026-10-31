using System.Globalization;
using Creatura.Seats;
using Creatura.Worlds;
using Creatura.Worlds.Web;

namespace Creatura.Cli;

/// <summary>
/// <c>creatura ux run</c>: one cell of a web world (docs/design-web-worlds.md): a persona uses a real web app through
/// Playwright on one device, and the app's own data says whether the task was done. <c>--demo</c> serves the bundled
/// notes app on 127.0.0.1 and drives it with the bundled script, so it runs with no backend and no model; an adapter
/// file runs any app, with a model as the persona (it costs money, so the seat is never chosen for you). Exit codes:
/// 0 ran (whatever the data says), 2 a usage error, 3 no browser could be started.
/// </summary>
public static class UxCommand
{
    public const string Usage = """
        usage: creatura ux run --demo [options]
               creatura ux run --adapter <file.json> --seat model:<name> [options]
               creatura ux list (--demo | --adapter <file.json>)
               creatura ux adapter          print the demo's adapter, to start a file of your own from

          --demo                       the bundled notes app, served on 127.0.0.1 by this command; offline
          --adapter <file.json>        an app's adapter: personas, environments, tasks with their data checks, barriers
          --base-url <url>             where the app is served (overrides the adapter's baseUrl)
          --task <id> --persona <id> --env <id>
                                       the cell to run (default: the first of each)
          --barriers none|<ids>        barriers to put between the browser and the app, joined with +, e.g. save-lies
          --seat script:<path>|model:<name>
                                       who is the persona: a script of actions by control name, or a catalogued model
                                       (default for --demo: the bundled script; an adapter needs one named)
          --escalate <name>            the stronger model asked when the persona is stuck (default: the adapter's
                                       limits.models.escalate, for a model seat)
          --design plain|<name>        the design in the loop (default plain: the control layer alone); creatura run --list
          --ticks <n>                  at most n steps (default: the adapter's limits.steps)
          --log <path>                 the model calls, one JSON line each
          --browser <path>             a Chromium to launch instead of Playwright's own
          --headed                     show the browser
          --trace                      every row of the trace
        """;

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

        if (verb is not ("run" or "list")) return Fail(error, $"unknown ux command: {verb}");

        var flags = new HashSet<string>(StringComparer.Ordinal);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] is "--demo" or "--headed" or "--trace") { flags.Add(args[i]); continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) return Fail(error, $"unexpected argument: {args[i]}");
            options[args[i]] = args[++i];
        }

        string[] known = ["--adapter", "--base-url", "--task", "--persona", "--env", "--barriers", "--seat", "--escalate", "--design", "--ticks", "--log", "--browser"];
        var unknown = options.Keys.Except(known).ToList();
        if (unknown.Count > 0) return Fail(error, $"unknown option: {string.Join(", ", unknown)}");
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

        UxTask task;
        UxPersona persona;
        UxEnvironment environment;
        List<string> barriers;
        try
        {
            task = adapter.Task(options.GetValueOrDefault("--task"));
            persona = adapter.Persona(options.GetValueOrDefault("--persona"));
            environment = adapter.Environment(options.GetValueOrDefault("--env"));
            var set = options.GetValueOrDefault("--barriers") ?? "none";
            barriers = set == "none" ? [] : set.Split('+').ToList();
            foreach (var id in barriers) adapter.Barrier(id);
        }
        catch (ArgumentException e)
        {
            return Fail(error, e.Message);
        }

        var ticks = adapter.Limits.Steps;
        if (options.TryGetValue("--ticks", out var t) && (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) || ticks < 1))
            return Fail(error, "--ticks takes a whole number of at least 1");

        var design = options.GetValueOrDefault("--design") ?? "plain";
        if (design != "plain" && AgentCatalog.TryFind(design) is null) return Fail(error, $"unknown design '{design}'; known: plain, {string.Join(", ", AgentCatalog.Names)}");
        Func<IPolicy, AgentTask, IAgent> agent = design == "plain" ? PlainLoop.Make : (p, k) => AgentCatalog.Make(design, p, k);

        // The seat needs the world's screen, so it is built when the world opens; what can be wrong with it is found first.
        var seat = options.GetValueOrDefault("--seat") ?? (demo ? "script:(bundled)" : null);
        if (seat is null) return Fail(error, "name the persona's seat: --seat model:<name> (a model costs money) or --seat script:<file>");
        Func<WebWorld, IPolicy> build;
        string? cost = null;
        if (seat.StartsWith("script:", StringComparison.Ordinal))
        {
            var path = seat["script:".Length..];
            if (path == "(bundled)") build = w => new WebScriptPolicy(DemoFiles.Script, () => w.Current, "script:notes (bundled)");
            else if (!File.Exists(path)) return Fail(error, $"the script '{path}' does not exist");
            else build = w => WebScriptPolicy.FromFile(path, () => w.Current);
        }
        else
        {
            var catalog = ModelCatalog.Current;
            var name = seat.StartsWith("model:", StringComparison.Ordinal) ? seat["model:".Length..] : seat;
            if (catalog.TryFind(name) is not { } decide) return Fail(error, $"no model '{name}' in the catalog ({catalog.Source}); known: {string.Join(", ", catalog.Names)}");
            var escalateName = options.GetValueOrDefault("--escalate") ?? adapter.Limits.Models.GetValueOrDefault("escalate");
            var escalate = escalateName is null ? null : catalog.TryFind(escalateName);
            if (escalateName is not null && escalate is null) return Fail(error, $"no model '{escalateName}' to escalate to in the catalog");
            foreach (var endpoint in new[] { decide, escalate }.OfType<ModelEndpoint>())
            {
                if (SeatFactory.MissingKey(endpoint) is { } missing) return Fail(error, $"{endpoint.Name} needs a key in {missing}: set it and retry");
            }

            var log = options.TryGetValue("--log", out var logPath) ? new CallLog(logPath) : null;
            var client = new ModelClient();
            build = w => new PersonaPolicy(() => w.Current, persona, environment, decide, client, escalate, adapter.Limits, log);
            cost = adapter.Limits.Cost is { } cap ? string.Create(CultureInfo.InvariantCulture, $", at most {cap:0.####} spent") : "";
        }

        output.WriteLine($"{adapter.App}: {task.Id} × {persona.Id} × {environment.Id} ({environment.Width}x{environment.Height}{(environment.Mobile ? ", mobile" : "")}); " +
            $"barriers: {(barriers.Count == 0 ? "none" : string.Join(", ", barriers))}; seat: {seat}; design: {design}; up to {ticks} steps");
        if (cost is not null) output.WriteLine($"the persona is a model: each step is a call to it, at its price{cost}");

        WebBrowser browser;
        try
        {
            browser = await WebBrowser.LaunchAsync(!flags.Contains("--headed"), options.GetValueOrDefault("--browser"));
        }
        catch (Exception e) when (WebBrowser.IsBrowserFailure(e))
        {
            error.WriteLine($"no browser could be started: {e.Message.Split('\n')[0]}");
            error.WriteLine(e.Message.Contains("Driver not found", StringComparison.Ordinal)
                ? "this creatura is a single file, which cannot carry Playwright's driver: run creatura ux from the .NET tool (dotnet tool install -g Creatura.Cli) or with dotnet run --project src/Creatura.Cli"
                : "install Chromium for Playwright (pwsh playwright.ps1 install chromium, beside Creatura.Worlds.Web.dll), or pass --browser <path to chromium>");
            return 3;
        }

        UxCell cell;
        var culture = CultureInfo.CurrentCulture;
        await using (browser)
        {
            try
            {
                CultureInfo.CurrentCulture = TraceText.Culture;
                cell = await UxRunner.RunAsync(browser, adapter, task, persona, environment, build, agent, new WebWorldOptions { Barriers = barriers }, ticks);
            }
            catch (Exception e) when (WebBrowser.IsBrowserFailure(e))
            {
                error.WriteLine($"the app could not be opened at {adapter.Resolve(task.Start)}: {e.Message.Split('\n')[0]}");
                return 3;
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
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

    private static void List(UxAdapter adapter, TextWriter output)
    {
        output.WriteLine($"{adapter.App} at {adapter.BaseUrl}");
        output.WriteLine("tasks:");
        foreach (var t in adapter.Tasks) output.WriteLine($"  {t.Id,-20}{t.Prompt}");
        output.WriteLine("personas:");
        foreach (var p in adapter.Personas) output.WriteLine($"  {p.Id,-20}{p.Description}");
        output.WriteLine("environments:");
        foreach (var e in adapter.Environments) output.WriteLine($"  {e.Id,-20}{e.Width}x{e.Height}{(e.Mobile ? ", mobile" : "")}");
        output.WriteLine("barriers:");
        if (adapter.Barriers.Count == 0) output.WriteLine("  (none)");
        foreach (var b in adapter.Barriers) output.WriteLine($"  {b.Id,-20}{b.Kind.ToString().ToLowerInvariant()} {b.Method ?? "any"} {b.Url}");
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine("creatura ux --help for the options");
        return 2;
    }
}
