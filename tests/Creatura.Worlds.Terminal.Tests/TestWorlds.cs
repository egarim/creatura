using Creatura.Worlds.Terminal;

namespace Creatura.Worlds.Terminal.Tests;

/// <summary>The small shapes every offline test shares: a task, a world over a scripted terminal, and a stub check.</summary>
internal static class TestWorlds
{
    public static TerminalTask Spec(string name = "menu", ITerminalCheck? check = null, params string[] requires) => new()
    {
        Name = name,
        Check = check,
        CommitRequires = requires,
    };

    public static Task<TerminalWorld> OpenAsync(
        SimulatedTerminal terminal,
        TerminalTask? task = null,
        TerminalWorldOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TerminalWorld.OpenAsync(task ?? Spec(), (options ?? new TerminalWorldOptions()) with { Driver = terminal }, cancellationToken);

    public static ITerminalCheck Check(bool done, string detail = "the app's data says so") =>
        TerminalCheck.From(() => new TerminalCheckResult(done, detail));
}
