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

}
