namespace Creatura.Worlds;

/// <summary>
/// The planner with the full map and no budget: plans over the true catalogue every tick, acts, and keeps
/// going until the goal or the tick limit, with the one rule every design shares, three-tick habituation of
/// a tool whose effect did not land. It never takes the shortcut because its goal is the contract. Its routes
/// under blocking are what "routes that exist" means here.
/// </summary>
public sealed class OracleAgent : IAgent
{
    private readonly AgentTask _task;

    public OracleAgent(AgentTask task) => _task = task;

    public string Name => "Oracle planner";

    public Task<AgentRun> RunAsync(IWorld world, int maxTicks = 40, CancellationToken cancellationToken = default)
    {
        var trace = new List<TraceEvent>();
        var quarantine = new Quarantine();
        var tick = 0;
        var idle = 0;
        while (tick < maxTicks && !_task.Goal(world.Facts))
        {
            tick++;
            var visible = quarantine.Visible(world, tick).Where(t => !t.NeedsPayload).ToList();
            var plan = Planner.Find(world.Facts, visible, _task.Goal, 12, _task.Habit);
            if (plan is null || plan.Count == 0)
            {
                if (++idle > 4) break;
                continue;
            }

            var tool = world.Tool(plan[0])!;
            var outcome = world.Act(tool.Id);
            var landed = world.EffectsLanded(tool);
            if (!landed) quarantine.Add(tool.Id, tick);
            idle = 0;
            trace.Add(new TraceEvent(tick, "cell", "effect", landed ? 0 : 1, 1, tool.Id, landed ? TraceKind.Acted : TraceKind.Blocked, outcome.Observation ?? ""));
        }

        var reached = _task.Goal(world.Facts);
        return Task.FromResult(new AgentRun(Name, reached, tick, trace, ["full map, no budget, three-tick habituation"]));
    }
}
