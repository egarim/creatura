namespace Creatura;

/// <summary>
/// A task the agents can be pointed at without changing the architecture: the goal, how progress is
/// measured, the habit, and which tool kind needs a stage interlock before it may run.
/// The toy service-restore domain and the real "rebuild Planner.Find" task are both just tasks.
/// </summary>
public sealed record AgentTask(
    string Name,
    string GoalText,
    Func<IReadOnlySet<string>, bool> Goal,
    Func<IWorldView, double> Progress,
    Func<IWorldView, double> Contract,
    IReadOnlyList<string> Habit)
{
    /// <summary>The tool kind gated by the stage interlock (release vs release of a code fix).</summary>
    public string ReleaseKind { get; init; } = "release";

    /// <summary>
    /// What this world's facts mean to an agent that judges outcomes against what it wants (the orienting regulator):
    /// which facts are goals, which are steps toward them, which signal trouble. World data, carried by the task until
    /// the world manifest exists; null for a table inferred from the tool catalogue (<see cref="Orienting.ValenceTable.Infer"/>).
    /// </summary>
    public Orienting.ValenceTable? Valence { get; init; }
}
