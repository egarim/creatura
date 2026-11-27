using Sentiora.Client;

namespace Creatura.Worlds.Terminal;

/// <summary>
/// The real driver: one Sentiora runtime process (a child that speaks JSON-RPC 2.0 over stdio) and one PTY session in
/// it. What this driver opened it disposes; a <see cref="SentioraClient"/> or <see cref="SessionHandle"/> handed in
/// through <see cref="TerminalWorldOptions"/> belongs to the caller and is left alone. Keys are sent as the runtime's
/// own sequences through <c>terminal.type</c> (see <see cref="TerminalKeys"/>), and a host that stopped answering is
/// restarted by <see cref="RecycleAsync"/> rather than reported as a live world.
/// </summary>
public sealed class SentioraTerminalDriver : ITerminalDriver
{
    private readonly bool _ownsClient;
    private readonly bool _ownsSession;
    private readonly string? _serverPath;
    private readonly string? _nodeExecutable;
    private readonly string? _workingDirectory;
    private readonly int _timeoutMs;
    private readonly CreateSessionRequest? _create;
    private SentioraClient _client;
    private SessionHandle _session;
    private bool _closed;
    private bool _disposed;

    private SentioraTerminalDriver(
        SentioraClient client,
        SessionHandle session,
        bool ownsClient,
        bool ownsSession,
        string? serverPath,
        string? nodeExecutable,
        string? workingDirectory,
        int timeoutMs,
        CreateSessionRequest? create)
    {
        _client = client;
        _session = session;
        _ownsClient = ownsClient;
        _ownsSession = ownsSession;
        _serverPath = serverPath;
        _nodeExecutable = nodeExecutable;
        _workingDirectory = workingDirectory;
        _timeoutMs = timeoutMs;
        _create = create;
    }

    public bool ForcedShutdown => _client.ForcedShutdown;

    /// <summary>The runtime start plus the session create; the options must already have been validated.</summary>
    public static async Task<SentioraTerminalDriver> StartAsync(TerminalWorldOptions options, CancellationToken cancellationToken = default)
    {
        var client = options.Client;
        var ownsClient = client is null;
        client ??= await SentioraClient.StartAsync(
            options.ServerPath!,
            options.NodeExecutable,
            options.WorkingDirectory,
            options.ActionTimeoutMs,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var session = options.Session;
            var ownsSession = session is null;
            session ??= await client.CreateAsync(options.Create!, cancellationToken).ConfigureAwait(false);
            return new SentioraTerminalDriver(
                client, session, ownsClient, ownsSession,
                options.ServerPath, options.NodeExecutable, options.WorkingDirectory, options.ActionTimeoutMs, options.Create);
        }
        catch
        {
            if (ownsClient) await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<TerminalObservation> ObserveAsync(CancellationToken cancellationToken) =>
        TerminalObservation.From(await _client.ObserveAsync(_session, cancellationToken).ConfigureAwait(false));

    public Task<long> TypeAsync(string text, CancellationToken cancellationToken) =>
        _client.TypeAsync(_session, text, cancellationToken: cancellationToken);

    public Task<long> SendLineAsync(string line, CancellationToken cancellationToken) =>
        _client.SendLineAsync(_session, line, cancellationToken);

    public Task<long> SendKeyAsync(string key, CancellationToken cancellationToken) =>
        _client.TypeAsync(_session, TerminalKeys.Sequence(key), cancellationToken: cancellationToken);

    public async Task<TerminalObservation> WaitAsync(long afterRevision, int timeoutMs, CancellationToken cancellationToken) =>
        TerminalObservation.From(await _client.WaitAsync(_session, afterRevision, timeoutMs, cancellationToken).ConfigureAwait(false));

    public Task WaitForTextAsync(string needle, int timeoutMs, CancellationToken cancellationToken) =>
        _client.WaitForTextAsync(_session, needle, timeoutMs, cancellationToken: cancellationToken);

    public async Task<bool> CloseAsync(CancellationToken cancellationToken)
    {
        if (_closed) return false;
        _closed = await _client.CloseAsync(_session, cancellationToken).ConfigureAwait(false);
        return _closed;
    }

    /// <summary>
    /// Restarts the runtime and creates the session again, for a host that stopped answering (the client's own doc:
    /// a blocked host is a client timeout, and an adapter must recycle the host). Only a runtime this driver opened
    /// can be restarted: a client handed in belongs to the caller, and a session handed in to its session store.
    /// </summary>
    public async Task<bool> RecycleAsync(CancellationToken cancellationToken)
    {
        if (!_ownsClient) return false;
        try
        {
            if (_ownsSession && !_closed) await _client.CloseAsync(_session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The old host is being abandoned; its close is best effort.
        }

        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Same: a forced shutdown is reported by ForcedShutdown, not thrown out of a recycle.
        }

        try
        {
            _client = await SentioraClient.StartAsync(_serverPath!, _nodeExecutable, _workingDirectory, _timeoutMs, cancellationToken).ConfigureAwait(false);
            _session = await _client.CreateAsync(_create!, cancellationToken).ConfigureAwait(false);
            _closed = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsSession && !_closed)
        {
            try
            {
                _closed = await _client.CloseAsync(_session).ConfigureAwait(false);
            }
            catch
            {
                // A session that cannot be closed leaves the host to be shut down, which is exactly what the
                // client's dispose does and reports through ForcedShutdown.
            }
        }

        if (_ownsClient) await _client.DisposeAsync().ConfigureAwait(false);
    }
}
