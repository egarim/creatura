// Vendored from Sentiora (C:\Users\joche\source\repos\st-agent, commit 302d71f),
// clients/dotnet/Sentiora.Client/SentioraClient.cs. Copied unchanged apart from this comment; the namespace stays
// Sentiora.Client. This copy is to be replaced by a PackageReference once Sentiora publishes one (PACKAGE.md).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Sentiora.Client;

/// <summary>
/// A typed client for the Sentiora runtime's versioned JSON-RPC protocol (protocolVersion 1),
/// over a subprocess stdio transport.
///
/// The transport is deliberately a child process with its own stdio: a shell that prints a
/// forged JSON-RPC line or a byte of binary cannot reach this channel (WIN-15), so a
/// misbehaving terminal can never corrupt the host's protocol stream.
///
/// Two protocol sharp edges are handled here rather than by every caller:
/// terminal.type requires the current revision (TypeAsync observes and retries on
/// "Stale observation"), and terminal.stream is a cursor rather than a subscription
/// (WaitForTextAsync handles the attach snapshot and resync so collected text is not
/// duplicated).
/// </summary>
public sealed class SentioraClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly ConcurrentDictionary<int, PendingRequest> _pending = new();
    private readonly object _writeLock = new();
    private readonly StringBuilder _stderr = new();
    private readonly Task _reader;
    private readonly Task _stderrPump;
    private int _nextId;
    private volatile bool _closed;
    private bool _disposed;

    private SentioraClient(Process process, int defaultTimeoutMs)
    {
        _process = process;
        _stdin = process.StandardInput;
        DefaultTimeoutMs = defaultTimeoutMs;
        _reader = Task.Run(ReadLoopAsync);
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    public int DefaultTimeoutMs { get; }
    /// <summary>
    /// True when dispose had to kill the host instead of letting it exit. A supervisor must
    /// treat this as a runtime leak, not as a clean shutdown.
    /// </summary>
    public bool ForcedShutdown { get; private set; }
    public InitializeResult? Info { get; private set; }
    public string Stderr => _stderr.ToString();

    /// <summary>Spawns the runtime and completes initialize.</summary>
    public static async Task<SentioraClient> StartAsync(
        string serverPath,
        string? nodeExecutable = null,
        string? workingDirectory = null,
        int defaultTimeoutMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(nodeExecutable ?? "node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(serverPath);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the runtime");
        var client = new SentioraClient(process, defaultTimeoutMs);
        await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    public async Task<InitializeResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("initialize", new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false);
        Info = result.Deserialize<InitializeResult>(Json) ?? throw new SentioraException("initialize returned no result", "initialize");
        return Info;
    }

    public async Task<SessionHandle> CreateAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["command"] = request.Command,
            ["args"] = request.Args,
            ["cols"] = request.Cols,
            ["rows"] = request.Rows,
        };
        if (request.Cwd is not null) parameters["cwd"] = request.Cwd;
        if (request.Env is not null) parameters["env"] = request.Env;
        if (request.ConptyDll is not null) parameters["conptyDll"] = request.ConptyDll;
        if (request.RecordInput is not null) parameters["recordInput"] = request.RecordInput;
        if (request.RecordBytes is not null) parameters["recordBytes"] = request.RecordBytes;

        var result = await RequestAsync("terminal.create", parameters, cancellationToken).ConfigureAwait(false);
        var observation = result.Deserialize<Observation>(Json) ?? throw new SentioraException("terminal.create returned no observation", "terminal.create");
        return new SessionHandle
        {
            SessionId = observation.SessionId,
            Owner = observation.Owner ?? throw new SentioraException("terminal.create returned no owner token", "terminal.create"),
            Pid = observation.Pid,
            Cols = observation.Cols,
            Rows = observation.Rows,
        };
    }

    public async Task<IReadOnlyList<Observation>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("terminal.list", new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false);
        return result.Deserialize<List<Observation>>(Json) ?? new List<Observation>();
    }

    public async Task<Observation> ObserveAsync(SessionHandle session, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("terminal.observe", Session(session), cancellationToken).ConfigureAwait(false);
        return result.Deserialize<Observation>(Json) ?? throw new SentioraException("terminal.observe returned no observation", "terminal.observe");
    }

    /// <summary>
    /// Revision-armed input. The runtime rejects a stale revision instead of guessing, so the
    /// observe-type-retry loop lives here: that is the difference between a client an agent can
    /// use and a protocol an agent has to memorise.
    /// </summary>
    public async Task<long> TypeAsync(SessionHandle session, string text, int attempts = 3, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= attempts; attempt += 1)
        {
            var observation = await ObserveAsync(session, cancellationToken).ConfigureAwait(false);
            if (observation.Exit is not null || observation.State == "released")
            {
                throw new SentioraException("session is not running", "terminal.type");
            }
            var parameters = Session(session);
            parameters["revision"] = observation.Revision;
            parameters["text"] = text;
            try
            {
                var result = await RequestAsync("terminal.type", parameters, cancellationToken).ConfigureAwait(false);
                return result.GetProperty("revision").GetInt64();
            }
            catch (SentioraException error) when (error.Message.Contains("Stale observation", StringComparison.Ordinal) && attempt < attempts)
            {
                // output moved the revision between observe and type; observe again
            }
        }
        throw new SentioraException("terminal.type kept returning a stale revision", "terminal.type");
    }

    public Task<long> SendLineAsync(SessionHandle session, string line, CancellationToken cancellationToken = default)
        => TypeAsync(session, line + "\r", cancellationToken: cancellationToken);

    public async Task<Observation> WaitAsync(SessionHandle session, long afterRevision, int timeoutMs = 1000, CancellationToken cancellationToken = default)
    {
        var parameters = Session(session);
        parameters["afterRevision"] = afterRevision;
        parameters["timeoutMs"] = timeoutMs;
        var result = await RequestAsync("terminal.wait", parameters, cancellationToken, timeoutMs + DefaultTimeoutMs).ConfigureAwait(false);
        return result.Deserialize<Observation>(Json) ?? throw new SentioraException("terminal.wait returned no observation", "terminal.wait");
    }

    public async Task<StreamFrame> StreamAsync(SessionHandle session, long afterSequence = 0, int timeoutMs = 1000, CancellationToken cancellationToken = default)
    {
        var parameters = Session(session);
        parameters["afterSequence"] = afterSequence;
        parameters["timeoutMs"] = timeoutMs;
        var result = await RequestAsync("terminal.stream", parameters, cancellationToken, timeoutMs + DefaultTimeoutMs).ConfigureAwait(false);
        return result.Deserialize<StreamFrame>(Json) ?? throw new SentioraException("terminal.stream returned no frame", "terminal.stream");
    }

    /// <summary>
    /// Consumes the stream until the collected text contains <paramref name="needle"/>. A resync
    /// replaces the collected text with the snapshot (the snapshot already accounts for the bytes),
    /// and a child exit ends the wait with the frame instead of burning the timeout.
    /// </summary>
    public async Task<(string Text, StreamFrame Frame, long NextSequence)> WaitForTextAsync(
        SessionHandle session,
        string needle,
        int timeoutMs = 20000,
        long afterSequence = 0,
        CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var cursor = afterSequence;
        var text = new StringBuilder();
        while (true)
        {
            var remaining = (int)Math.Max(1, Math.Min(500, deadline - Environment.TickCount64));
            var frame = await StreamAsync(session, cursor, remaining, cancellationToken).ConfigureAwait(false);
            cursor = frame.NextSequence;
            if (frame.Resync || (frame.Snapshot is not null && frame.Events.Count == 0))
            {
                text.Clear();
                text.Append(frame.Snapshot?.Screen ?? "");
            }
            else
            {
                foreach (var streamEvent in frame.Events)
                {
                    if (streamEvent.Kind == "output" && streamEvent.Data is not null)
                    {
                        text.Append(Encoding.UTF8.GetString(Convert.FromBase64String(streamEvent.Data)));
                    }
                }
            }
            if (text.ToString().Contains(needle, StringComparison.Ordinal)) return (text.ToString(), frame, cursor);
            if (frame.Exit is not null) return (text.ToString(), frame, cursor);
            if (Environment.TickCount64 >= deadline)
            {
                throw new SentioraException($"text {JsonSerializer.Serialize(needle)} not seen within {timeoutMs}ms", "terminal.stream");
            }
        }
    }

    public async Task<bool> CloseAsync(SessionHandle session, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("terminal.close", Session(session), cancellationToken).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.True;
    }

    private static Dictionary<string, object?> Session(SessionHandle session) => new()
    {
        ["sessionId"] = session.SessionId,
        ["owner"] = session.Owner,
    };

    private async Task<JsonElement> RequestAsync(string method, Dictionary<string, object?> parameters, CancellationToken cancellationToken, int timeoutMs = 0)
    {
        if (_closed) throw new SentioraException("client is closed", method);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(completion, method);
        _pending[id] = pending;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs);
        using var registration = timeout.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out _))
            {
                completion.TrySetException(new SentioraException($"{method} did not answer within {(timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs)}ms", method));
            }
        });

        var message = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };
        var line = JsonSerializer.Serialize(message, Json);
        lock (_writeLock)
        {
            _stdin.WriteLine(line);
            _stdin.Flush();
        }
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        while (true)
        {
            string? line;
            try
            {
                line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // The runtime never writes non-JSON to stdout. If that ever changes, keep the
                // evidence instead of dropping the line silently.
                _stderr.Append("unparseable stdout line: ").Append(line).Append('\n');
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number) continue;
                var id = idElement.GetInt32();
                if (!_pending.TryRemove(id, out var pending)) continue;
                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "runtime error" : "runtime error";
                    var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : (int?)null;
                    pending.Completion.TrySetException(new SentioraException(message, pending.Method, code));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    pending.Completion.TrySetResult(result.Clone());
                }
            }
        }

        _closed = true;
        foreach (var (id, pending) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                pending.Completion.TrySetException(new SentioraException("runtime exited before answering", pending.Method));
            }
        }
    }

    private async Task PumpStderrAsync()
    {
        while (true)
        {
            var line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
            if (line is null) break;
            _stderr.Append(line).Append('\n');
        }
    }

    public async ValueTask DisposeAsync()
    {
        // A second dispose must be a no-op: Process.HasExited throws once the Process
        // object has been disposed, and 'await using' plus an explicit dispose is a
        // normal shape for callers.
        if (_disposed) return;
        _disposed = true;
        try { _stdin.Close(); } catch { /* already gone */ }
        if (!_process.WaitForExit(20000))
        {
            ForcedShutdown = true;
            try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        _closed = true;
        try { await Task.WhenAll(_reader, _stderrPump).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { /* shutdown */ }
        _process.Dispose();
    }

    private sealed class PendingRequest(TaskCompletionSource<JsonElement> completion, string method)
    {
        public TaskCompletionSource<JsonElement> Completion { get; } = completion;
        public string Method { get; } = method;
    }
}
