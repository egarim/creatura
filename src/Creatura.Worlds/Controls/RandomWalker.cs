using Creatura;

namespace Creatura.Worlds;

/// <summary>
/// The control every battery runs beside the designs: picks any visible tool uniformly, acts, and stops
/// when the goal is met or the ticks run out. No set points, no record, no distrust, no plan. If a design
/// does not beat this, the design was not doing the solving.
/// </summary>
public sealed class RandomWalker : IAgent
{
    private readonly AgentTask _task;
    private readonly int _seed;

    public RandomWalker(AgentTask task, int seed = 1)
    {
        _task = task;
        _seed = seed;
    }

    public string Name => "Random walker";

    public Task<AgentRun> RunAsync(IWorld world, int maxTicks = 40, CancellationToken cancellationToken = default)
    {
        var rng = new Random(_seed);
        var trace = new List<TraceEvent>();
        var tick = 0;
        var reached = false;
        while (tick < maxTicks)
        {
            tick++;
            if (_task.Goal(world.Facts))
            {
                reached = true;
                break;
            }

            var tools = world.AvailableTools.Where(t => !t.NeedsPayload).ToList();
            if (tools.Count == 0) break;
            var tool = tools[rng.Next(tools.Count)];
            var outcome = world.Act(tool.Id);
            var landed = world.EffectsLanded(tool);
            trace.Add(new TraceEvent(tick, "cell", "effect", landed ? 0 : 1, 1, tool.Id, landed ? TraceKind.Acted : TraceKind.Blocked, outcome.Observation ?? ""));
        }

        if (!reached && _task.Goal(world.Facts)) reached = true;
        if (reached) trace.Add(new TraceEvent(tick, "organism", "contract", 0, 1, "contract met", TraceKind.Acted, "by chance"));
        return Task.FromResult(new AgentRun(Name, reached, tick, trace, ["uniform choice over the visible tools, seeded"]));
    }
}
