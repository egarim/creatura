using System.Diagnostics;
using Sentiora.Client;

namespace Creatura.Worlds.Terminal;

/// <summary>What a <see cref="SimulatedStep"/> reacts to.</summary>
public enum SimulatedInput
{
    /// <summary>A change that happens with no input, after its delay from the first observation.</summary>
    None,
    Line,
    Type,
    Key,
}

/// <summary>
/// One scripted event of a <see cref="SimulatedTerminal"/>: an input that produces a new screen (and perhaps an exit),
/// or a change that happens with no input after a delay. A delay on an input is the application's own latency: the
/// input is accepted at once and the screen changes when the delay has passed, so a bounded wait can fail honestly in
/// between.
/// </summary>
public sealed record SimulatedStep(SimulatedInput Kind, string? Input, string Screen, int DelayMs = 0, int? ExitCode = null)
{
    public static SimulatedStep Line(string line, string screen, int delayMs = 0, int? exitCode = null) =>
        new(SimulatedInput.Line, line, screen, delayMs, exitCode);

    public static SimulatedStep Typed(string text, string screen, int delayMs = 0, int? exitCode = null) =>
        new(SimulatedInput.Type, text, screen, delayMs, exitCode);

    public static SimulatedStep Key(string key, string screen, int delayMs = 0, int? exitCode = null) =>
        new(SimulatedInput.Key, key, screen, delayMs, exitCode);

    /// <summary>A screen that changes with no input, <paramref name="afterMs"/> after the first observation.</summary>
    public static SimulatedStep Redraw(string screen, int afterMs, int? exitCode = null) =>
        new(SimulatedInput.None, null, screen, afterMs, exitCode);

    /// <summary>The child exits with <paramref name="code"/>, optionally after showing one last screen.</summary>
    public static SimulatedStep Exit(int code, string screen = "", int delayMs = 0) =>
        new(SimulatedInput.None, null, screen, delayMs, code);
}

/// <summary>
/// A terminal that needs no process: a scripted screen that reacts to the input the world sends, changes on its own
/// clock, and can exit. It exists so the world's tools, facts, bounds and verdicts run offline and deterministically;
/// the world runs exactly the same code over it and over <see cref="SentioraTerminalDriver"/>. The simulation clock
/// starts at the first observation, so a no-input redraw can never happen before the opening screen was read.
/// </summary>
public sealed class SimulatedTerminal : ITerminalDriver
{
    private sealed record Due(long At, int Index);

    private readonly object _gate = new();
    private readonly SimulatedStep[] _script;
    private readonly bool[] _taken;
    private readonly List<Due> _due = [];
    private readonly List<string> _sent = [];
    private long? _clock;
    private string _screen;
    private long _revision;
    private int? _exitCode;
    private bool _closed;

    public SimulatedTerminal(
        IReadOnlyList<SimulatedStep> script,
        string sessionId = "sim-1",
        int cols = 80,
        int rows = 24,
        string screen = "")
    {
        _script = [.. script];
        _taken = new bool[_script.Length];
        _screen = screen;
        SessionId = sessionId;
        Cols = cols;
        Rows = rows;
        for (var i = 0; i < _script.Length; i++)
        {
            if (_script[i].Kind == SimulatedInput.None) _due.Add(new Due(Math.Max(0, _script[i].DelayMs), i));
        }
    }

    public string SessionId { get; }

    public int Cols { get; }

    public int Rows { get; }

    /// <summary>The current screen, for a test that wants to look without an observation.</summary>
    public string Screen
    {
        get
        {
            lock (_gate)
            {
                Pump();
                return _screen;
            }
        }
    }

    public long Revision
    {
        get
        {
            lock (_gate) return _revision;
        }
    }

    /// <summary>Everything the world sent, in order, for a test that asserts what reached the terminal.</summary>
    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (_gate) return [.. _sent];
        }
    }

    public bool ForcedShutdown => false;

    public Task<TerminalObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Pump();
            return Task.FromResult(Snapshot());
        }
    }

    public Task<long> TypeAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Send(SimulatedInput.Type, text, "terminal.type"));
    }

    public Task<long> SendLineAsync(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Send(SimulatedInput.Line, line, "terminal.type"));
    }

    public Task<long> SendKeyAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Send(SimulatedInput.Key, key, "terminal.key"));
    }

    public async Task<TerminalObservation> WaitAsync(long afterRevision, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            TerminalObservation snapshot;
            lock (_gate)
            {
                Pump();
                snapshot = Snapshot();
            }

            if (snapshot.Revision > afterRevision || snapshot.ExitCode is not null) return snapshot;
            if (Environment.TickCount64 >= deadline) return snapshot;
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WaitForTextAsync(string needle, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            string screen;
            int? exit;
            lock (_gate)
            {
                Pump();
                screen = _screen;
                exit = _exitCode;
            }

            if (screen.Contains(needle, StringComparison.Ordinal)) return;
            // The runtime ends a text wait early when the child exits: the caller re-reads and sees no text.
            if (exit is not null) return;
            if (Environment.TickCount64 >= deadline)
                throw new SentioraException($"text \"{needle}\" not seen within {timeoutMs}ms", "terminal.stream");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<bool> CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Pump();
            if (_closed) return Task.FromResult(false);
            _closed = true;
            return Task.FromResult(true);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) _closed = true;
        return ValueTask.CompletedTask;
    }

    private long Send(SimulatedInput kind, string value, string method)
    {
        lock (_gate)
        {
            Pump();
            if (_closed) throw new SentioraException("session is released", method);
            if (_exitCode is not null) throw new SentioraException("session is not running", method);
            _sent.Add(kind == SimulatedInput.Key ? $"key:{value}" : value);
            for (var i = 0; i < _script.Length; i++)
            {
                if (_taken[i] || _script[i].Kind != kind || !string.Equals(_script[i].Input, value, StringComparison.Ordinal)) continue;
                _taken[i] = true;
                var delay = Math.Max(0, _script[i].DelayMs);
                if (delay == 0) Apply(_script[i]);
                else _due.Add(new Due(Now + delay, i));
                break;
            }

            return _revision;
        }
    }

    private long Now
    {
        get
        {
            _clock ??= Environment.TickCount64;
            return Environment.TickCount64 - _clock.Value;
        }
    }

    private void Pump()
    {
        if (_closed) return;
        while (true)
        {
            if (_exitCode is not null) return;
            var now = Now;
            Due? next = null;
            foreach (var due in _due)
            {
                if (due.At > now) continue;
                if (next is null || due.At < next.At || (due.At == next.At && due.Index < next.Index)) next = due;
            }

            if (next is null) return;
            _due.Remove(next);
            _taken[next.Index] = true;
            Apply(_script[next.Index]);
        }
    }

    private void Apply(SimulatedStep step)
    {
        _screen = step.Screen;
        _revision++;
        if (step.ExitCode is int code) _exitCode = code;
    }

    private TerminalObservation Snapshot() => new()
    {
        SessionId = SessionId,
        Pid = 4242,
        State = _closed ? "released" : _exitCode is not null ? "exited" : "running",
        Revision = _revision,
        Running = !_closed && _exitCode is null,
        ExitCode = _exitCode,
        Cols = Cols,
        Rows = Rows,
        Cursor = new Cursor(),
        Screen = _screen,
    };
}
