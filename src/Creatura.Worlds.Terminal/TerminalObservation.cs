using System.Globalization;
using Sentiora.Client;

namespace Creatura.Worlds.Terminal;

/// <summary>
/// One read of the terminal, split the way the runtime's own observation asks a consumer to split it: the objective
/// half (state, pid, revision, running, exit, geometry) is what a world may put in its facts; <see cref="Screen"/> is
/// the rendered report the persona reads, and is never a fact. The world cuts the screen to its text budget before
/// it keeps it, so a large scrollback cannot become the seat's context.
/// </summary>
public sealed record TerminalObservation
{
    public static readonly TerminalObservation Empty = new();

    public string SessionId { get; init; } = "";

    public int? Pid { get; init; }

    public string State { get; init; } = "";

    public long Revision { get; init; }

    public bool Running { get; init; }

    public int? ExitCode { get; init; }

    public int Cols { get; init; }

    public int Rows { get; init; }

    public Cursor Cursor { get; init; } = new();

    public long IdleMilliseconds { get; init; }

    public int WriteErrors { get; init; }

    public int RenderErrors { get; init; }

    /// <summary>The rendered screen: the report, not a fact.</summary>
    public string Screen { get; init; } = "";

    public static TerminalObservation From(Observation observation) => new()
    {
        SessionId = observation.SessionId,
        Pid = observation.Pid,
        State = observation.State,
        Revision = observation.Revision,
        Running = observation.Running,
        ExitCode = observation.Exit?.ExitCode,
        Cols = observation.Cols,
        Rows = observation.Rows,
        Cursor = observation.Cursor,
        IdleMilliseconds = observation.IdleMilliseconds,
        WriteErrors = observation.WriteErrors,
        RenderErrors = observation.RenderErrors,
        Screen = observation.Screen,
    };

    /// <summary>The same observation with the screen cut to the world's text budget, saying that it was cut.</summary>
    public TerminalObservation Cut(int maxText) =>
        Screen.Length <= maxText ? this : this with { Screen = string.Concat(Screen.AsSpan(0, maxText), "…") };

    /// <summary>The objective half in one line, for an outcome: what is true, without the screen.</summary>
    public string Summary => Running
        ? string.Create(CultureInfo.InvariantCulture, $"running, {Cols}x{Rows} at revision {Revision}")
        : ExitCode is int code
            ? string.Create(CultureInfo.InvariantCulture, $"exited with code {code}, {Cols}x{Rows} at revision {Revision}")
            : string.Create(CultureInfo.InvariantCulture, $"not running ({State}), {Cols}x{Rows} at revision {Revision}");
}
