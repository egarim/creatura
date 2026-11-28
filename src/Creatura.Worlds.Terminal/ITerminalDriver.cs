namespace Creatura.Worlds.Terminal;

/// <summary>
/// What a terminal world needs from a terminal: reads, four kinds of input, a bounded wait, and a close. It is
/// implemented twice — <see cref="SentioraTerminalDriver"/> over a real Sentiora runtime process, and
/// <see cref="SimulatedTerminal"/> over a scripted screen — and <see cref="TerminalWorld"/> runs the same tools,
/// facts, verdicts and bounds over either one. Every member is asynchronous and takes a token.
/// </summary>
public interface ITerminalDriver : IAsyncDisposable
{
    /// <summary>One read: the objective state and the rendered screen.</summary>
    Task<TerminalObservation> ObserveAsync(CancellationToken cancellationToken);

    /// <summary>Types text at the revision the runtime reports now (the runtime rejects a stale one).</summary>
    Task<long> TypeAsync(string text, CancellationToken cancellationToken);

    /// <summary>Types a line and presses Enter.</summary>
    Task<long> SendLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>Sends one key by its runtime name (see <see cref="TerminalKeys"/>).</summary>
    Task<long> SendKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>Waits until the revision moves past <paramref name="afterRevision"/> or the bound passes, and returns the read either way.</summary>
    Task<TerminalObservation> WaitAsync(long afterRevision, int timeoutMs, CancellationToken cancellationToken);

    /// <summary>
    /// Waits until the rendered text contains <paramref name="needle"/>. Throws
    /// <see cref="Sentiora.Client.SentioraException"/> when the bound passes (and returns without the text when the
    /// child exits first) — the world re-reads the screen afterwards and never takes this call's word for it.
    /// </summary>
    Task WaitForTextAsync(string needle, int timeoutMs, CancellationToken cancellationToken);

    /// <summary>Ends the session; false when the runtime did not confirm it.</summary>
    Task<bool> CloseAsync(CancellationToken cancellationToken);

    /// <summary>True when a dispose had to kill the host instead of letting it exit.</summary>
    bool ForcedShutdown { get; }

    /// <summary>
    /// Restarts a host that stopped answering, for a driver that owns one. The world calls this after a fatal
    /// <c>SentioraException</c> or timeout; false means this driver cannot restart, and the world says so instead of
    /// pretending to keep working.
    /// </summary>
    Task<bool> RecycleAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}
