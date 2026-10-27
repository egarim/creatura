namespace Creatura.Orienting;

/// <summary>
/// The behaviourist: the orienting agent's control (design-sokolov.md, §6). One scalar per action, +1 for a goal
/// fact or a step toward one, −1 for a failure or a lie, a value table with the planner's habit as the prior,
/// epsilon-greedy choice. No trace, no habituation, no orienting tick, no split between promise and threat. It is
/// the control for the claim that the split and the habituation matter. A plain <see cref="IAgent"/> on purpose,
/// not a regulator and not an option of the control layer: a control must not inherit the effect check by accident.
/// With epsilon 0 (the default) it is greedy and draws on no random source, so it can sit in a golden trace.
/// </summary>
public sealed class BehaviouristAgent : IAgent
{
    private readonly AgentTask _task;
    private readonly double _epsilon;
    private readonly int _seed;

    /// <param name="task">The task; its valence table says which facts reward (inferred from the catalogue when it states none).</param>
    /// <param name="epsilon">The share of choices made at random; 0 is greedy and deterministic.</param>
    /// <param name="seed">The random source's seed, when epsilon is above 0.</param>
    public BehaviouristAgent(AgentTask task, double epsilon = 0.0, int seed = 0)
    {
        _task = task;
        _epsilon = epsilon;
        _seed = seed;
    }

    public string Name => "behaviourist";

    /// <summary>What the habit is worth before any consequence.</summary>
    public double HabitPrior { get; init; } = 0.5;

    /// <summary>How far a value moves toward each reward.</summary>
    public double Rate { get; init; } = 0.5;

    public Task<AgentRun> RunAsync(IWorld world, int maxTicks = 40, CancellationToken cancellationToken = default)
    {
        var trace = new List<TraceEvent>();
        var table = _task.Valence ?? ValenceTable.Infer(world.AllTools, world.Facts);
        var notes = new List<string>
        {
            $"one scalar per action; epsilon {_epsilon:F2}; habit prior {HabitPrior:F2}; valence table {(_task.Valence is null ? "inferred from the catalogue" : "from the task")}",
        };
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var tool in world.AllTools) values[tool.Id] = _task.Habit.Contains(tool.Id, StringComparer.Ordinal) ? HabitPrior : 0.0;
        var random = _epsilon > 0 ? new Random(_seed) : null;
        var goalReached = false;
        var lastTick = 0;
        var rewards = 0;
        var punishments = 0;

        for (var tick = 1; tick <= maxTicks; tick++)
        {
            lastTick = tick;
            if (_task.Goal(world.Facts))
            {
                goalReached = true;
                trace.Add(new TraceEvent(tick, "organism", "contract", 0.0, 0.0, TraceText.ContractMet, TraceKind.Acted, TraceText.Complete(_task.Name)));
                break;
            }

            var tools = world.AvailableTools;
            if (tools.Count == 0)
            {
                trace.Add(new TraceEvent(tick, "tissue", "value", 1.0, 1.0, TraceText.NoAction, TraceKind.Idle, "nothing to do"));
                break;
            }

            Tool choice;
            string why;
            if (random is not null && random.NextDouble() < _epsilon)
            {
                choice = tools[random.Next(tools.Count)];
                why = "explore (epsilon)";
            }
            else
            {
                choice = tools.OrderByDescending(t => values.GetValueOrDefault(t.Id)).ThenBy(t => t.Id, StringComparer.Ordinal).First();
                why = $"greedy: value {values.GetValueOrDefault(choice.Id):+0.00;-0.00}";
            }

            trace.Add(new TraceEvent(tick, "tissue", "value", 0.0, 1.0, choice.Id, TraceKind.Acted, why));
            var before = new HashSet<string>(world.Facts, StringComparer.Ordinal);
            var outcome = world.Act(choice.Id);
            var gained = world.Facts.Where(f => !before.Contains(f)).ToList();
            var rewarded = gained.Any(f => table.Goals.Contains(f) || table.StepToward.ContainsKey(f));
            var reward = rewarded ? 1.0 : -1.0;
            values[choice.Id] += (reward - values[choice.Id]) * Rate;
            if (rewarded) rewards++; else punishments++;
            trace.Add(new TraceEvent(tick, "cell", "reward", rewarded ? 0.0 : 1.0, 1.0, choice.Id, rewarded ? TraceKind.Acted : TraceKind.Blocked,
                $"{(rewarded ? "+1" : "-1")}: {(gained.Count == 0 ? outcome.Succeeded ? "reported success, nothing gained" : outcome.Observation ?? "failed" : string.Join(",", gained))}; value now {values[choice.Id]:+0.00;-0.00}"));
        }

        if (!goalReached && _task.Goal(world.Facts))
        {
            goalReached = true;
            trace.Add(new TraceEvent(lastTick, "organism", "contract", 0.0, 0.0, TraceText.ContractMet, TraceKind.Acted, TraceText.Complete(_task.Name)));
        }

        notes.Add($"values: {string.Join(", ", values.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value:+0.00;-0.00}"))}");
        notes.Add($"rewards {rewards}, punishments {punishments}");
        return Task.FromResult(new AgentRun(Name, goalReached, lastTick, trace, notes));
    }
}
