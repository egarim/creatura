namespace Creatura.Worlds;

/// <summary>One series: an agent reaching the goal, having its route blocked, and trying again, until it cannot.</summary>
public sealed record SeriesResult(
    string Agent,
    bool Opaque,
    int RoutesFound,
    int RoutesExist,
    int? FirstGoalTicks,
    int TotalTicks,
    int Runs,
    int ExploratoryTries,
    int LiesCaught,
    string EndState,
    IReadOnlyList<string> Routes,
    IReadOnlyList<string> Revoked,
    DreamJournal Dreams,
    EffectMemory Memory);

/// <summary>
/// Levin's test made into a number (the ingenuity score). Block the route the organism used, run it again with what
/// it learned, count the distinct routes it finds before it stops. "Routes that exist" is what a planner with the
/// full map finds under the same blocking, so the score is always against the same world.
/// </summary>
public static class Series
{
    /// <summary>The series over an agent named in the <see cref="AgentCatalog"/>; a seeded agent gets <c>seed + runs so far</c>.</summary>
    public static Task<SeriesResult> RunAsync(
        WorldManifest world,
        BarrierSet barriers,
        string agentKind,
        Func<IPolicy> seat,
        Func<EffectMemory, DreamJournal, AgentOptions> options,
        bool opaque,
        int maxTicks,
        int seed,
        int routesExist,
        Action<int, IReadOnlyList<string>, AgentRun>? onRun = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(world, barriers, agentKind, (runOptions, runs) => AgentCatalog.Make(agentKind, seat(), world.Task, runOptions, seed + runs), options, opaque, maxTicks, routesExist, onRun, cancellationToken);

    /// <summary>
    /// The series over an agent factory: called once per run with that run's options and the number of runs made
    /// before it. The memory and the dream journal are per series; the extensions are per run.
    /// </summary>
    public static async Task<SeriesResult> RunAsync(
        WorldManifest world,
        BarrierSet barriers,
        string label,
        Func<AgentOptions, int, IAgent> make,
        Func<EffectMemory, DreamJournal, AgentOptions> options,
        bool opaque,
        int maxTicks,
        int routesExist,
        Action<int, IReadOnlyList<string>, AgentRun>? onRun = null,
        CancellationToken cancellationToken = default)
    {
        var memory = new EffectMemory();
        var journal = new DreamJournal();
        var revoked = new List<string>();
        var routes = new List<string>();
        int? firstGoal = null;
        var totalTicks = 0;
        var runs = 0;
        var tries = 0;
        var lies = 0;
        string endState;

        while (true)
        {
            var inner = world.Create(barriers);
            var opaqueWorld = new OpaqueWorld(inner, revoked) { Transparent = !opaque };
            IWorld target = opaque ? new LearnedWorld(opaqueWorld, memory) : opaqueWorld;

            // Extensions are per run (their counters start over); the memory and the dream journal are per series.
            var runOptions = opaque ? options(memory, journal) : AgentOptions.None;
            var agent = make(runOptions, runs);

            var run = await agent.RunAsync(target, maxTicks, cancellationToken).ConfigureAwait(false);
            runs++;
            onRun?.Invoke(runs, revoked, run);
            totalTicks += run.Ticks;
            tries += run.Counters.ExploratoryTries + run.Counters.Explorations;
            lies += opaqueWorld.Lies;

            var real = world.Task.Goal(inner.Facts);
            if (!real)
            {
                endState = routes.Count == 0 ? "never reached the goal" : run.GoalReached ? "claimed without a route" : "stopped while a route existed";
                break;
            }

            firstGoal ??= run.Ticks;
            // A route is the chain of tools that first produced each goal fact; the shortcut and side-actions do not count.
            var shortcuts = inner.AllTools.Where(t => t.Kind == world.Task.ReleaseKind && t.Requires.Count == 0).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            var landed = run.Trace.Where(e => e.Level == "cell" && e.Kind == TraceKind.Acted && !shortcuts.Contains(e.Action)).Select(e => e.Action).Distinct().ToList();
            var route = Contributing(landed, inner, world);
            var key = string.Join(">", route);
            if (!routes.Contains(key)) routes.Add(key);

            // Block one tool on this route that still leaves a route in the true world.
            var block = route.FirstOrDefault(t => !revoked.Contains(t) && RouteExists(world, barriers, revoked.Append(t).ToList(), maxTicks));
            if (block is null)
            {
                endState = "no route left";
                break;
            }

            revoked.Add(block);
            if (routes.Count > 24)
            {
                endState = "cap";
                break;
            }
        }

        return new SeriesResult(label, opaque, routes.Count, routesExist, firstGoal, totalTicks, runs, tries, lies, endState, routes, revoked, journal, memory);
    }

    /// <summary>
    /// The tools that carried the goal: walk back from the goal's facts; for each needed fact, the first landed
    /// tool that produced it is on the route, and its own requirements become needed in turn. One chain per
    /// run. A walker that also fuelled the boat on its way to driving the truck did not find a second route.
    /// </summary>
    private static List<string> Contributing(IReadOnlyList<string> landed, IWorld truth, WorldManifest world)
    {
        var needed = new List<string>(world.GoalEvidence);
        if (needed.Count == 0) return landed.ToList();
        var byId = truth.AllTools.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < needed.Count; i++)
        {
            var fact = needed[i];
            if (!seen.Add(fact)) continue;
            var producer = landed.FirstOrDefault(id => byId.TryGetValue(id, out var t) && t.Grants.Contains(fact));
            if (producer is null || kept.Contains(producer)) continue;
            kept.Add(producer);
            needed.AddRange(byId[producer].Requires);
        }

        return landed.Where(kept.Contains).ToList();
    }

    /// <summary>How many distinct routes the oracle finds under successive blocking: the denominator of the score.</summary>
    public static async Task<int> CountRoutesAsync(WorldManifest world, BarrierSet barriers, int maxTicks)
    {
        var result = await RunAsync(world, barriers, "oracle", () => new PlannerPolicy(), (_, _) => AgentOptions.None, opaque: false, maxTicks, 0, 0).ConfigureAwait(false);
        return result.RoutesFound;
    }

    private static bool RouteExists(WorldManifest world, BarrierSet barriers, IReadOnlyList<string> revoked, int maxTicks)
    {
        var inner = world.Create(barriers);
        var target = new OpaqueWorld(inner, revoked) { Transparent = true };
        new OracleAgent(world.Task).RunAsync(target, maxTicks).GetAwaiter().GetResult();
        return world.Task.Goal(inner.Facts);
    }
}
