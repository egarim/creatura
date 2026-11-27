namespace Creatura.Worlds.Terminal;

/// <summary>What an independent check says about a terminal task: the application's outcome, read outside the PTY.</summary>
public sealed record TerminalCheckResult(bool Done, string Detail);

/// <summary>
/// The independent check of a terminal task: reads the application's outcome from outside the terminal (a file it
/// wrote, its API, its database, its exit contract), never through the screen. <see cref="TerminalWorld"/> grants the
/// task's done fact only while this agrees, which is the whole point of the split: the screen is the report and the
/// check is the registry.
/// </summary>
public interface ITerminalCheck
{
    Task<TerminalCheckResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>A check written as a delegate: the ordinary way a host states its task.</summary>
public sealed class DelegateTerminalCheck(Func<CancellationToken, Task<TerminalCheckResult>> run) : ITerminalCheck
{
    public DelegateTerminalCheck(Func<TerminalCheckResult> answer)
        : this(_ => Task.FromResult(answer()))
    {
    }

    public Task<TerminalCheckResult> RunAsync(CancellationToken cancellationToken = default) => run(cancellationToken);
}

/// <summary>The two shapes a check is written in, so a host does not have to name <see cref="DelegateTerminalCheck"/>.</summary>
public static class TerminalCheck
{
    public static ITerminalCheck From(Func<CancellationToken, Task<TerminalCheckResult>> run) => new DelegateTerminalCheck(run);

    public static ITerminalCheck From(Func<TerminalCheckResult> answer) => new DelegateTerminalCheck(answer);
}

/// <summary>
/// The task a terminal world is pointed at: the name its done fact is built from (<c>done:&lt;name&gt;</c>), the
/// independent check that alone may grant it, and the facts a commit needs before it is attempted. The check is not
/// the task's prompt and not its goal: what the persona is asked to do is the host's business.
/// </summary>
public sealed record TerminalTask
{
    public required string Name { get; init; }

    public string Prompt { get; init; } = "";

    /// <summary>The application's outcome, read outside the terminal; null means a commit can only fail honestly.</summary>
    public ITerminalCheck? Check { get; init; }

    /// <summary>
    /// Facts the task's check implies must hold before a commit is attempted (a task whose check needs a live session
    /// names <c>state:running</c>, a task whose check reads a file the application leaves behind names nothing).
    /// </summary>
    public IReadOnlyList<string> CommitRequires { get; init; } = [];

    public string DoneFact => TerminalFacts.Done(Name);
}
