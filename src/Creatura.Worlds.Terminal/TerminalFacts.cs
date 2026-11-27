namespace Creatura.Worlds.Terminal;

/// <summary>
/// The facts a terminal world may hold, and nothing else. The objective half of a runtime observation is a fact
/// (the session exists, its state and revision and geometry, the child's exit code) because the runtime reports it
/// and a re-read can check it; the rendered screen is not a fact and has no name here — it is the report a persona
/// reads. The one fact that is not the runtime's word is <see cref="Done"/>, and only the injected independent check
/// grants it.
/// </summary>
public static class TerminalFacts
{
    /// <summary>The session was closed and the runtime confirmed it.</summary>
    public const string Closed = "session-closed";

    public static string Session(string sessionId) => $"session:{sessionId}";

    public static string State(string state) => $"state:{state}";

    public static string Revision(long revision) => FormattableString.Invariant($"revision:{revision}");

    public static string Size(int cols, int rows) => FormattableString.Invariant($"size:{cols}x{rows}");

    /// <summary>The child exited with this code.</summary>
    public static string Exited(int exitCode) => FormattableString.Invariant($"exited:{exitCode}");

    /// <summary>The task's own fact: what the independent check proves, never what the screen or the runtime claims.</summary>
    public static string Done(string task) => $"done:{task}";
}
