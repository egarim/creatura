using System.Globalization;
using Creatura.Bench;
using Creatura.Seats;
using Creatura.Worlds;

namespace Creatura.Cli;

/// <summary>
/// <c>creatura run</c>: one design on one world with no code (docs/design-onboarding.md), from the catalogs the
/// runners use: <see cref="AgentCatalog"/>, <see cref="WorldCatalog"/> and the seats of <see cref="SeatResolver"/>. The
/// default seat is the planner, so nothing leaves the machine unless <c>--seat model:&lt;name&gt;</c> asks for it. The
/// output is the same on every run with the planner. Exit codes: 0 ran (whether or not the goal was reached), 2 a
/// usage error.
/// </summary>
public static class RunCommand
{
    public const string Usage = """
        usage: creatura run --design <name> --world <name> [options]
               creatura run --list

          --design <name>              a design or a control from the catalog (creatura run --list)
          --world <name>               a world from the catalog
          --barriers none|all|<ids>    which of the world's barriers are on: switch ids joined with +, e.g. patchlies;
                                       default: the ones the world turns on by default
          --seat bfs|scripted:<path>|model:<name>
                                       who chooses the actions (default bfs, the built-in planner; a model costs money)
          --ticks <n>                  at most n ticks (default 40)
          --seed <n>                   for an agent that draws on a random source (default 1)
          --trace                      every row of the trace, not only the first 12 actions
        """;

    private const int ShownRows = 12;

    /// <summary>The invariant culture with the percent sign against the number ("100%").</summary>
    private static readonly CultureInfo Output = MakeOutput();

    private static CultureInfo MakeOutput()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PercentPositivePattern = 1;
        culture.NumberFormat.PercentNegativePattern = 1;
        return CultureInfo.ReadOnly(culture);
    }

    public static async Task<int> Run(string[] args, TextWriter output, TextWriter error, SeatResolver? seats = null)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            output.WriteLine(Usage);
            return 0;
        }
        if (args.Contains("--list"))
        {
            List(output, (seats ?? new SeatResolver()).Catalog);
            return 0;
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--trace") { options["--trace"] = ""; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) return Fail(error, $"unexpected argument: {args[i]}");
            options[args[i]] = args[++i];
        }
        var unknown = options.Keys.Except(["--design", "--world", "--barriers", "--seat", "--ticks", "--seed", "--trace"]).ToList();
        if (unknown.Count > 0) return Fail(error, $"unknown option: {string.Join(", ", unknown)}");
        if (!options.TryGetValue("--design", out var design)) return Fail(error, $"--design is needed; known: {string.Join(", ", AgentCatalog.Names)}");
        if (!options.TryGetValue("--world", out var worldId)) return Fail(error, $"--world is needed; known: {string.Join(", ", WorldCatalog.Names)}");
        if (AgentCatalog.TryFind(design) is null) return Fail(error, $"unknown design '{design}'; known: {string.Join(", ", AgentCatalog.Names)}");
        if (WorldCatalog.TryFind(worldId) is not { } manifest) return Fail(error, $"unknown world '{worldId}'; known: {string.Join(", ", WorldCatalog.Names)}");

        BarrierSet barriers;
        if (options.TryGetValue("--barriers", out var set))
        {
            var ids = manifest.Barriers.Select(b => b.Id).ToList();
            var wrong = set is "none" or "all" ? [] : set.Split('+').Where(id => !ids.Contains(id)).ToList();
            if (wrong.Count > 0) return Fail(error, $"{worldId} has no barrier {string.Join(", ", wrong)}; its barriers: {string.Join(", ", ids)}, or none, or all");
            barriers = BarrierSet.Parse(manifest, set);
        }
        else barriers = BarrierSet.Defaults(manifest);

        if (!Number(options, "--ticks", 40, out var ticks) || ticks < 1) return Fail(error, "--ticks takes a whole number of at least 1");
        if (!Number(options, "--seed", 1, out var seed)) return Fail(error, "--seed takes a whole number");

        var seat = options.GetValueOrDefault("--seat") ?? "bfs";
        IPolicy policy;
        try
        {
            var resolver = seats ?? new SeatResolver();
            var spec = new SeatSpec(seat);
            if (SeatResolver.KindOf(spec) == SeatKind.Model
                && resolver.Catalog.TryFind(SeatResolver.ModelName(spec)) is { } endpoint && SeatFactory.MissingKey(endpoint) is { } missing)
            {
                return Fail(error, $"{endpoint.Name} needs a key in {missing}: set it and retry, or leave out --seat to use the planner");
            }
            policy = resolver.Resolve(spec);
        }
        catch (ArgumentException e)
        {
            return Fail(error, e.Message);
        }

        var on = manifest.Barriers.Where(b => barriers.On(b.Id)).Select(b => b.Id).ToList();
        output.WriteLine($"{design} on {worldId} ({manifest.Task.Name}); barriers: {(on.Count == 0 ? "none" : string.Join(", ", on))}; seat: {policy.Name}; up to {ticks} ticks");
        if (SeatResolver.KindOf(new SeatSpec(seat)) == SeatKind.Model) output.WriteLine("the seat is a model: each tick is a call to it, at its price");

        var agent = AgentCatalog.Make(design, new AgentBuild(policy, manifest.Task, AgentOptions.None, seed));
        AgentRun run;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // The trace's text is formatted where it is written, in the machine's culture ("100 %" in German); this
            // command shows it in one culture so it reads the same on every machine.
            CultureInfo.CurrentCulture = Output;
            run = await agent.RunAsync(manifest.NewWorld(barriers), ticks);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        output.WriteLine();
        output.WriteLine($"  {"tick",-5}{"level",-10}{"stress",-8}{"free",-7}{"action",-22}{"kind",-11}detail");
        var rows = options.ContainsKey("--trace") ? run.Trace : run.Trace.Where(r => r.Kind != TraceKind.Idle).Take(ShownRows).ToList();
        foreach (var row in rows)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {row.Tick,-5}{row.Level,-10}{row.Stress,-8:F2}{row.Freedom,-7:F2}{row.Action,-22}{row.Kind,-11}{row.Detail}"));
        }
        var hidden = run.Trace.Count(r => r.Kind != TraceKind.Idle) - rows.Count(r => r.Kind != TraceKind.Idle);
        if (hidden > 0) output.WriteLine($"  ... {hidden} more (--trace shows every row)");

        output.WriteLine();
        output.WriteLine($"{run.Agent}: goal {(run.GoalReached ? "reached" : "not reached")} in {run.Ticks} tick{(run.Ticks == 1 ? "" : "s")}; " +
            $"{run.Blocks} blocked, {run.Escalations} escalation{(run.Escalations == 1 ? "" : "s")}");
        foreach (var note in run.Notes) output.WriteLine($"  · {note}");
        return 0;
    }

    private static void List(TextWriter output, ModelCatalog models)
    {
        output.WriteLine("designs:   " + string.Join(", ", AgentCatalog.All.Where(a => a.Role == AgentRole.Design).Select(a => a.Name)));
        output.WriteLine("controls:  " + string.Join(", ", AgentCatalog.All.Where(a => a.Role == AgentRole.Control).Select(a => a.Name)));
        output.WriteLine();
        output.WriteLine("worlds, with their barriers (* on by default):");
        foreach (var world in WorldCatalog.All)
        {
            var barriers = world.Barriers.Select(b => $"{b.Id}{(b.DefaultOn ? "*" : "")} ({b.Role.ToString().ToLowerInvariant()})");
            output.WriteLine($"  {world.Id,-14}{world.Task.Name}");
            if (world.Barriers.Count > 0) output.WriteLine($"  {"",-14}{string.Join(", ", barriers)}");
        }
        output.WriteLine();
        output.WriteLine($"seats: bfs (the planner, the default), scripted:<file>, or a model from {models.Source}:");
        foreach (var model in models.Endpoints)
        {
            var key = model.KeyEnv is null ? "no key needed" : $"key in {model.KeyEnv}";
            output.WriteLine($"  model:{model.Name,-24}{key}");
        }
    }

    private static bool Number(Dictionary<string, string> options, string name, int fallback, out int value)
    {
        value = fallback;
        return !options.TryGetValue(name, out var text) || int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine("creatura run --help for the options");
        return 2;
    }
}
