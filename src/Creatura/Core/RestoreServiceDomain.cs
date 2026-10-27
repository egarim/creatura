namespace Creatura;

/// <summary>
/// The demo domain: bring a broken service back to health. It is deliberately small enough that a
/// deterministic planner can play it, because the point of the code is the control architecture, not search.
///
/// Barriers available (see the demo):
///   - revoke "apply-patch"          → forces the same goal by different means
///   - make "apply-patch" a liar     → reports success, grants nothing
///   - revoke "permission.deploy"    → makes the goal unregulatable
/// </summary>
public static class RestoreServiceDomain
{
    public static readonly string[] Initial = ["permission.deploy"];

    /// <summary>Human-readable goal, for policies that read text instead of predicates.</summary>
    public const string GoalText =
        "Alert resolved: logs inspected, broken component identified, a fix applied, tests green, deployed.";

    /// <summary>The habit: the tool the agent reaches for first. A detour only counts against a habit.</summary>
    public static readonly string[] Habit = ["apply-patch"];

    public static IReadOnlyList<Tool> Tools() =>
    [
        new("inspect-logs", [], ["logs.inspected"], "read"),
        new("identify-component", ["logs.inspected"], ["component.identified"], "read"),
        new("apply-patch", ["component.identified"], ["patch.applied"], "write"),
        new("apply-config", ["component.identified"], ["patch.applied"], "write"),
        new("run-tests", ["patch.applied"], ["tests.green"], "verify"),
        new("deploy", ["tests.green", "permission.deploy"], ["deployed"], "release"),
        new("resolve-alert", ["deployed"], ["alert.resolved"], "close"),
    ];

    public static World NewWorld() => new(Tools(), Initial);

    public static bool Goal(IReadOnlySet<string> facts) => facts.Contains("alert.resolved");

    /// <summary>The toy domain packaged as a task, so the agents take it as data.</summary>
    public static AgentTask Task { get; } = new(
        "restore the service",
        GoalText,
        Goal,
        Progress,
        ContractProgress,
        Habit);

    public static double ContractProgress(IWorldView world) => world.Has("alert.resolved") ? 1.0 : 0.0;

    /// <summary>Progress toward the contract, counted in facts so partial work is visible as partial stress.</summary>
    public static double Progress(IWorldView world)
    {
        string[] required = ["logs.inspected", "component.identified", "patch.applied", "tests.green", "deployed", "alert.resolved"];
        return (double)required.Count(world.Has) / required.Length;
    }
}
