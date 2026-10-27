using Creatura;

namespace Creatura.Worlds;

/// <summary>
/// The agent the designs are compared against: it believes every report, keeps no record of itself, hides
/// nothing from its planner, and counts "resolved" as done. It plans over what the office shows it plus what
/// it has come to believe, which is how a lie becomes a belief and a belief becomes a wall.
/// </summary>
public sealed class CarelessAgent : IAgent
{
    private readonly AgentTask _task;

    public CarelessAgent(AgentTask task) => _task = task;

    public string Name => "Careless agent";

    public Task<AgentRun> RunAsync(IWorld world, int maxTicks = 40, CancellationToken cancellationToken = default)
    {
        var trace = new List<TraceEvent>();
        var notes = new List<string> { "no set points, no record, no quarantine: believes what it is told" };
        var beliefs = new HashSet<string>(world.Facts, StringComparer.Ordinal);
        var retries = 0;
        var tick = 0;
        var claimed = false;

        // A shortcut is any release-kind tool with no preconditions: a button that closes, logs or marks
        // something done. The careless agent counts whatever such a button grants as the goal.
        var shortcutGrants = world.AvailableTools
            .Where(t => t.Kind == _task.ReleaseKind && t.Requires.Count == 0)
            .SelectMany(t => t.Grants)
            .ToHashSet(StringComparer.Ordinal);
        bool Goal(IReadOnlySet<string> f) => shortcutGrants.Overlaps(f) || _task.Goal(f);

        while (tick < maxTicks)
        {
            tick++;
            var known = new HashSet<string>(world.Facts, StringComparer.Ordinal);
            known.UnionWith(beliefs);
            var plan = Planner.Find(known, world.AvailableTools, Goal, 10, _task.Habit);
            if (plan is null)
            {
                trace.Add(new TraceEvent(tick, "tissue", "plan", 0, 1, "no action", TraceKind.Idle, "no route"));
                if (++retries > 3)
                {
                    trace.Add(new TraceEvent(tick, "organism", "contract", 1, 1, "gave up", TraceKind.Escalated, "no route four times"));
                    break;
                }

                continue;
            }

            if (plan.Count == 0)
            {
                claimed = true;
                break;
            }

            var id = plan[0];
            var tool = world.Tool(id)!;
            trace.Add(new TraceEvent(tick, "tissue", "plan", 0, 1, id, TraceKind.Idle, string.Join(" → ", plan)));
            var outcome = world.Act(id);
            if (outcome.Succeeded)
            {
                beliefs.UnionWith(tool.Grants);
                var landed = world.EffectsLanded(tool);
                trace.Add(new TraceEvent(tick, "cell", "effect", landed ? 0 : 1, 1, id, landed ? TraceKind.Acted : TraceKind.Blocked,
                    landed ? outcome.Observation ?? "" : $"{outcome.Observation} · believed"));
                retries = 0;
            }
            else
            {
                trace.Add(new TraceEvent(tick, "cell", "effect", 1, 1, id, TraceKind.Blocked, outcome.Observation ?? "failed"));
                if (++retries > 3)
                {
                    trace.Add(new TraceEvent(tick, "organism", "contract", 1, 1, "gave up", TraceKind.Escalated, "same failing attempt four times"));
                    break;
                }
            }

            if (shortcutGrants.Overlaps(beliefs) || shortcutGrants.Overlaps(world.Facts) || _task.Goal(world.Facts))
            {
                claimed = true;
                break;
            }
        }

        var real = _task.Goal(world.Facts);
        if (claimed)
        {
            trace.Add(new TraceEvent(tick, "organism", "contract", real ? 0 : 1, 1, "resolved", real ? TraceKind.Acted : TraceKind.Blocked,
                real ? "contract met" : "claimed"));
            notes.Add(real ? "the registry agrees" : "reported resolved; the registry holds no permit");
        }

        // GoalReached is what the agent claims; the demo judges against the registry separately.
        return System.Threading.Tasks.Task.FromResult(new AgentRun(Name, claimed, tick, trace, notes));
    }
}
