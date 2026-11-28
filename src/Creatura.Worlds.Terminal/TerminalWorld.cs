using Sentiora.Client;

namespace Creatura.Worlds.Terminal;

/// <summary>What the world cannot do without a person: the loop tick it was seen on, and the prompt on the screen.</summary>
public sealed record TerminalHandoff(int Tick, string Prompt);

/// <summary>
/// A terminal application as a Creatura world (docs/design-terminal-worlds.md). The persona gets the rendered screen
/// as a report and one tool per action — read, type, line, key, wait for text, close, commit. The facts are only what
/// the world can prove: the session exists, the runtime's objective state/revision/geometry, the child's exit code,
/// and <c>done:&lt;task&gt;</c>, which only the injected independent check grants. The screen is never a fact, and no
/// success is reported that the world's own read (or the check) does not back.
/// <para>
/// <see cref="IWorld.Act"/> is synchronous and the runtime is not: an act runs on the thread pool and waits for it,
/// as <c>WebWorld</c> does, so a caller's synchronization context is never needed.
/// </para>
/// <para>
/// One world owns one driver. <see cref="SentioraTerminalDriver"/> opens one runtime process and one session; a
/// client or session injected through <see cref="TerminalWorldOptions"/> is shared and left to its owner.
/// </para>
/// </summary>
public sealed class TerminalWorld : ITickedWorld, IAsyncDisposable
{
    private readonly TerminalTask _task;
    private readonly TerminalWorldOptions _options;
    private readonly ITerminalDriver _driver;
    private readonly HashSet<string> _facts = new(StringComparer.Ordinal);
    private readonly List<Tool> _tools;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _closed;
    private bool _disposed;
    private int? _exitCode;
    private int _loopTick;

    private TerminalWorld(TerminalTask task, TerminalWorldOptions options, ITerminalDriver driver)
    {
        _task = task;
        _options = options;
        _driver = driver;
        _tools = [.. TerminalTools.For(task, options)];
    }

    /// <summary>
    /// Opens the world: validates the options, starts the runtime and its session (or uses the driver, client and
    /// session handed in), and reads the terminal once. The world owns the driver it is handed; it disposes the
    /// client and the session only when it opened them.
    /// </summary>
    public static async Task<TerminalWorld> OpenAsync(
        TerminalTask task,
        TerminalWorldOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TerminalWorldOptions();
        var problems = options.Problems();
        if (problems.Count > 0)
            throw new ArgumentException("the terminal world options are not usable: " + string.Join("; ", problems), nameof(options));

        var driver = options.Driver ?? await SentioraTerminalDriver.StartAsync(options, cancellationToken).ConfigureAwait(false);
        var world = new TerminalWorld(task, options, driver);
        try
        {
            if (!await world.RefreshAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("the terminal could not be read: " + (world.Failure ?? "no observation"));
            return world;
        }
        catch
        {
            await world.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public TerminalTask Task => _task;

    public TerminalWorldOptions Options => _options;

    /// <summary>The last read: the objective state and the rendered screen (the report, not a fact).</summary>
    public TerminalObservation Current { get; private set; } = TerminalObservation.Empty;

    /// <summary>The last independent check of the task's outcome.</summary>
    public TerminalCheckResult LastCheck { get; private set; } = new(false, "not checked yet");

    /// <summary>The prompt that means a person must answer, when one is on screen; null otherwise.</summary>
    public TerminalHandoff? Handoff { get; private set; }

    /// <summary>Whether the screen is waiting for a person this world will not speak for.</summary>
    public bool AwaitingHuman => Handoff is not null;

    /// <summary>True when a dispose had to kill the host instead of letting it exit; see <see cref="Shutdown"/>.</summary>
    public bool ForcedShutdown => _driver.ForcedShutdown;

    /// <summary>What disposal had to say, when it had something to say (a forced shutdown); null on a clean close.</summary>
    public string? Shutdown { get; private set; }

    /// <summary>The runtime failure that stopped this world acting, in the runtime's own words; null while it is usable.</summary>
    public string? Failure { get; private set; }

    public bool Faulted => Failure is not null;

    /// <summary>The last action asked of the world: the tool and what it was handed.</summary>
    public (string Tool, string? Payload)? LastAct { get; private set; }

    public IReadOnlySet<string> Facts => _facts;

    public IReadOnlyList<Tool> AvailableTools => _tools;

    public IReadOnlyList<Tool> AllTools => _tools;

    public int Ticks { get; private set; }

    public bool Has(string fact) => _facts.Contains(fact);

    public Tool? Tool(string id) => _tools.FirstOrDefault(t => t.Id == id);

    /// <summary>The registry holds what the tool promised (a tool that promises nothing has landed).</summary>
    public bool EffectsLanded(Tool tool) => tool.Grants.All(_facts.Contains);

    public ActionOutcome Act(string toolId, string? payload = null) =>
        System.Threading.Tasks.Task.Run(() => ActAsync(toolId, payload)).GetAwaiter().GetResult();

    /// <summary>The loop's tick, so a handoff is placed on the tick the loop judged it.</summary>
    public void BeginTick(int tick) => _loopTick = tick;

    /// <summary>One action on the terminal, the screen read again, and the task's check asked again.</summary>
    public async Task<ActionOutcome> ActAsync(string toolId, string? payload = null, CancellationToken cancellationToken = default)
    {
        Ticks++;
        LastAct = (toolId, payload);
        var tool = Tool(toolId);
        if (tool is null) return new ActionOutcome(toolId, false, [], "unknown tool");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (tool.NeedsPayload && string.IsNullOrEmpty(payload))
                return Fail(tool, "the tool needs a payload: " + tool.PayloadHint);
            if (_disposed) return Fail(tool, "the world is disposed");
            if (_closed && toolId is not (TerminalTools.Commit or TerminalTools.Close)) return Fail(tool, ClosedText());
            if (Handoff is { } handoff && toolId is TerminalTools.Type or TerminalTools.Line or TerminalTools.Key)
                return Fail(tool, $"a person is needed: the screen shows \"{handoff.Prompt}\", and this world will not answer it for them");
            var unmet = tool.Requires.Where(f => !_facts.Contains(f)).ToList();
            if (unmet.Count > 0) return Fail(tool, "preconditions unmet: " + string.Join(", ", unmet));

            return toolId switch
            {
                TerminalTools.ReadScreen => await ReadScreenAsync(tool, cancellationToken).ConfigureAwait(false),
                TerminalTools.Type => await SendAsync(tool, payload, key: null, cancellationToken: cancellationToken).ConfigureAwait(false),
                TerminalTools.Line => await SendAsync(tool, payload, key: null, cancellationToken: cancellationToken).ConfigureAwait(false),
                TerminalTools.Key => await KeyAsync(tool, payload!, cancellationToken).ConfigureAwait(false),
                TerminalTools.WaitForText => await WaitForTextAsync(tool, payload!, cancellationToken).ConfigureAwait(false),
                TerminalTools.Close => await CloseAsync(tool, cancellationToken).ConfigureAwait(false),
                TerminalTools.Commit => await CommitAsync(tool, cancellationToken).ConfigureAwait(false),
                _ => Fail(tool, "unknown tool"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (IsRuntimeFailure(e))
        {
            return await FatalAsync(tool.Id, e.Message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the terminal and asks the task's check: the observation, the facts and the tools for the next tick.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_closed)
        {
            await CheckAsync(cancellationToken).ConfigureAwait(false);
            RebuildFacts();
            return true;
        }

        var (observation, _) = await TryObserveAsync("window", cancellationToken).ConfigureAwait(false);
        if (observation is null) return false;
        await AdoptAsync(observation, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<ActionOutcome> ReadScreenAsync(Tool tool, CancellationToken cancellationToken)
    {
        var (observation, failure) = await TryObserveAsync(tool.Id, cancellationToken).ConfigureAwait(false);
        if (observation is null) return failure!;
        await AdoptAsync(observation, cancellationToken).ConfigureAwait(false);
        return new ActionOutcome(tool.Id, true, tool.Grants, Report(Current));
    }

    private async Task<ActionOutcome> SendAsync(Tool tool, string? payload, string? key, CancellationToken cancellationToken)
    {
        if (NotRunning(tool) is { } notRunning) return notRunning;
        var before = Current;
        var (sent, failure) = await TrySendAsync(tool, key is not null
            ? token => _driver.SendKeyAsync(key, token)
            : tool.Id == TerminalTools.Line
                ? token => _driver.SendLineAsync(payload!, token)
                : token => _driver.TypeAsync(payload!, token), cancellationToken).ConfigureAwait(false);
        if (!sent) return failure!;

        await SettleAsync(before.Revision, cancellationToken).ConfigureAwait(false);
        var (after, readFailure) = await TryObserveAsync(tool.Id, cancellationToken).ConfigureAwait(false);
        if (after is null) return readFailure!;
        await AdoptAsync(after, cancellationToken).ConfigureAwait(false);
        var verb = key is not null ? $"sent the {key} key" : tool.Id == TerminalTools.Line ? "sent a line" : "typed";
        var length = payload?.Length ?? 0;
        var amount = length == 1 ? "1 character" : $"{length} characters";
        return new ActionOutcome(tool.Id, true, tool.Grants, $"{verb} ({amount}); {Describe(before, Current)}");
    }

    private async Task<ActionOutcome> KeyAsync(Tool tool, string payload, CancellationToken cancellationToken)
    {
        if (!_options.Keys.Contains(payload, StringComparer.Ordinal))
            return Fail(tool, $"\"{payload}\" is not one of this world's keys: {string.Join(", ", _options.Keys)}");
        return await SendAsync(tool, payload: null, key: payload, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActionOutcome> WaitForTextAsync(Tool tool, string needle, CancellationToken cancellationToken)
    {
        if (Faulted) return Fail(tool, "the runtime is not usable: " + Failure);

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.WaitTimeoutMs + 1000);
            try
            {
                await _driver.WaitForTextAsync(needle, _options.WaitTimeoutMs, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Fail(tool, $"\"{needle}\" was not seen within {_options.WaitTimeoutMs}ms");
            }
            catch (SentioraException e) when (IsBenign(e))
            {
                return Fail(tool, e.Message);
            }
            catch (Exception e) when (IsRuntimeFailure(e))
            {
                return await FatalAsync(tool.Id, e.Message, cancellationToken).ConfigureAwait(false);
            }
        }

        // The runtime's word is not the report: read the screen again and only then say the text is there.
        var (observation, failure) = await TryObserveAsync(tool.Id, cancellationToken).ConfigureAwait(false);
        if (observation is null) return failure!;
        if (!observation.Screen.Contains(needle, StringComparison.Ordinal))
        {
            var why = observation.ExitCode is int code
                ? $"the child exited with code {code} before the text appeared"
                : "a fresh read of the screen does not show it";
            return Fail(tool, $"the text \"{needle}\" was not seen: {why}");
        }

        await AdoptAsync(observation, cancellationToken).ConfigureAwait(false);
        return new ActionOutcome(tool.Id, true, tool.Grants, $"saw \"{needle}\" at revision {Current.Revision}");
    }

    private async Task<ActionOutcome> CloseAsync(Tool tool, CancellationToken cancellationToken)
    {
        if (_closed) return Fail(tool, "the session is already closed");
        var (last, _) = await TryObserveAsync(tool.Id, cancellationToken).ConfigureAwait(false);
        var exit = last?.ExitCode ?? _exitCode;

        bool closed;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.ActionTimeoutMs + 500);
            try
            {
                closed = await _driver.CloseAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Fail(tool, $"the close did not answer within {_options.ActionTimeoutMs}ms");
            }
            catch (SentioraException e) when (IsBenign(e))
            {
                return Fail(tool, e.Message);
            }
            catch (Exception e) when (IsRuntimeFailure(e))
            {
                return await FatalAsync(tool.Id, e.Message, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!closed) return Fail(tool, "the runtime did not confirm the close");
        _closed = true;
        _exitCode = exit;
        Current = (last ?? Current) with { State = "released", Running = false };
        await CheckAsync(cancellationToken).ConfigureAwait(false);
        RebuildFacts();
        Handoff = null;
        var ended = exit is int code
            ? $"the child had exited (exit code {code})"
            : "the child was still running and was stopped";
        return new ActionOutcome(tool.Id, true, tool.Grants, $"the session is closed; {ended}");
    }

    private async Task<ActionOutcome> CommitAsync(Tool tool, CancellationToken cancellationToken)
    {
        if (_task.Check is null)
            return Fail(tool, "the task declares no independent check, so there is nothing that could prove it done");
        await CheckAsync(cancellationToken).ConfigureAwait(false);
        RebuildFacts();
        if (!LastCheck.Done) return Fail(tool, $"the check does not agree: {LastCheck.Detail}");
        return new ActionOutcome(tool.Id, true, tool.Grants, $"the check agrees: {LastCheck.Detail}");
    }

    private async Task AdoptAsync(TerminalObservation observation, CancellationToken cancellationToken)
    {
        Current = observation.Cut(_options.MaxText);
        if (observation.ExitCode is int code) _exitCode = code;
        if (observation.State == "released") _closed = true;
        await CheckAsync(cancellationToken).ConfigureAwait(false);
        RebuildFacts();
        Handoff = _closed ? null : PromptOnScreen(observation);
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (_task.Check is null)
        {
            LastCheck = new TerminalCheckResult(false, "no independent check is declared");
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ActionTimeoutMs);
        try
        {
            LastCheck = await _task.Check.RunAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LastCheck = new TerminalCheckResult(false, $"the check did not answer within {_options.ActionTimeoutMs}ms");
        }
        catch (Exception e)
        {
            LastCheck = new TerminalCheckResult(false, "the check failed: " + FirstLine(e.Message));
        }
    }

    private void RebuildFacts()
    {
        _facts.Clear();
        if (Faulted)
        {
            // The runtime stopped answering: the session's state, revision and geometry can no longer be proved, so
            // the registry keeps only an exit code that was observed before the failure (and the check, below).
            if (_exitCode is int ended) _facts.Add(TerminalFacts.Exited(ended));
        }
        else if (_closed)
        {
            _facts.Add(TerminalFacts.Closed);
            if (_exitCode is int ended) _facts.Add(TerminalFacts.Exited(ended));
        }
        else
        {
            var observation = Current;
            if (observation.SessionId.Length > 0) _facts.Add(TerminalFacts.Session(observation.SessionId));
            if (observation.State.Length > 0) _facts.Add(TerminalFacts.State(observation.State));
            _facts.Add(TerminalFacts.Revision(observation.Revision));
            if (observation.Cols > 0 && observation.Rows > 0) _facts.Add(TerminalFacts.Size(observation.Cols, observation.Rows));
            if (observation.ExitCode is int code) _facts.Add(TerminalFacts.Exited(code));
        }

        if (LastCheck.Done) _facts.Add(_task.DoneFact);
    }

    private async Task SettleAsync(long afterRevision, CancellationToken cancellationToken)
    {
        if (_options.SettleMs <= 0) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.SettleMs);
        try
        {
            await _driver.WaitAsync(afterRevision, _options.SettleMs, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && (e is SentioraException or OperationCanceledException or TimeoutException))
        {
            // A settle is not an action: input that produced nothing new on screen is legitimate, so this waits as
            // long as it may and then reads the screen as it is.
        }
    }

    private async Task<(TerminalObservation? Observation, ActionOutcome? Failure)> TryObserveAsync(string toolId, CancellationToken cancellationToken)
    {
        if (Faulted) return (null, FailureOutcome(toolId, "the runtime is not usable: " + Failure));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ActionTimeoutMs + 500);
        try
        {
            return (await _driver.ObserveAsync(timeout.Token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, FailureOutcome(toolId, $"the read did not answer within {_options.ActionTimeoutMs}ms"));
        }
        catch (SentioraException e) when (IsBenign(e))
        {
            return (null, FailureOutcome(toolId, e.Message));
        }
        catch (Exception e) when (IsRuntimeFailure(e))
        {
            return (null, await FatalAsync(toolId, e.Message, cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task<(bool Sent, ActionOutcome? Failure)> TrySendAsync(Tool tool, Func<CancellationToken, Task<long>> send, CancellationToken cancellationToken)
    {
        if (Faulted) return (false, FailureOutcome(tool.Id, "the runtime is not usable: " + Failure));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ActionTimeoutMs + 500);
        try
        {
            await send(timeout.Token).ConfigureAwait(false);
            return (true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, FailureOutcome(tool.Id, $"the input did not answer within {_options.ActionTimeoutMs}ms"));
        }
        catch (SentioraException e) when (IsBenign(e))
        {
            return (false, FailureOutcome(tool.Id, e.Message));
        }
        catch (Exception e) when (IsRuntimeFailure(e))
        {
            return (false, await FatalAsync(tool.Id, e.Message, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// A runtime that stopped answering is a failed episode, not a live world: the driver is asked to restart the
    /// host, and the outcome says whether it did or whether this world is finished.
    /// </summary>
    private async Task<ActionOutcome> FatalAsync(string toolId, string message, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        bool recycled;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.ActionTimeoutMs + 1000);
            try
            {
                recycled = await _driver.RecycleAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                recycled = false;
            }
        }

        if (recycled)
        {
            _closed = false;
            _exitCode = null;
            Failure = null;
            try
            {
                // A plain read, not TryObserveAsync: a restart that still cannot be read must not start another
                // recycle from inside a recycle.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.ActionTimeoutMs + 1000);
                var observation = await _driver.ObserveAsync(timeout.Token).ConfigureAwait(false);
                await AdoptAsync(observation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                Failure = "the restarted runtime could not be read";
                RebuildFacts();
            }

            return FailureOutcome(toolId, message + "; the runtime was restarted, so its session starts again");
        }

        Failure = message;
        RebuildFacts();
        return FailureOutcome(toolId, message + "; the runtime could not be restarted, so this world cannot act further");
    }

    private ActionOutcome? NotRunning(Tool tool)
    {
        if (Current.ExitCode is int code)
            return Fail(tool, $"the child has exited with code {code}; there is nothing to send it");
        if (!Current.Running)
            return Fail(tool, $"the session is not running ({Current.State}); there is nothing to send it");
        return null;
    }

    private TerminalHandoff? PromptOnScreen(TerminalObservation observation) =>
        _options.ApprovalPrompts.FirstOrDefault(prompt => observation.Screen.Contains(prompt, StringComparison.OrdinalIgnoreCase)) is { } found
            ? new TerminalHandoff(_loopTick, found)
            : null;

    private static ActionOutcome Fail(Tool tool, string why) => new(tool.Id, false, [], why);

    private static ActionOutcome FailureOutcome(string toolId, string why) => new(toolId, false, [], why);

    private string ClosedText() =>
        _exitCode is int code ? $"the session is closed (the child had exited with code {code})" : "the session is closed";

    private static string Report(TerminalObservation observation) => observation.Summary + "\n" + observation.Screen;

    /// <summary>What the screen says after an action, in one line: the objective state and the first new lines.</summary>
    private static string Describe(TerminalObservation before, TerminalObservation after)
    {
        var fresh = Fresh(before.Screen, after.Screen).Take(2).ToList();
        var state = after.Running
            ? $"running at revision {after.Revision}"
            : after.ExitCode is int code ? $"exited with code {code}" : $"not running ({after.State})";
        return fresh.Count == 0 ? $"{state}; nothing new on screen" : $"{state}; new: {string.Join(" / ", fresh)}";
    }

    /// <summary>The lines on screen after that were not before, counted as a multiset: a repeated line is new twice.</summary>
    private static IEnumerable<string> Fresh(string before, string after)
    {
        var old = before.Split('\n').Where(line => line.Length > 0)
            .GroupBy(line => line, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var line in after.Split('\n').Where(line => line.Length > 0))
        {
            if (old.TryGetValue(line, out var count) && count > 0) old[line] = count - 1;
            else yield return line.TrimEnd('\r');
        }
    }

    private static bool IsBenign(SentioraException error) =>
        error.Message.Contains("not seen within", StringComparison.Ordinal)
        || error.Message.Contains("Stale observation", StringComparison.Ordinal)
        || error.Message.Contains("stale revision", StringComparison.Ordinal)
        || error.Message.Contains("not running", StringComparison.Ordinal)
        || error.Message.Contains("Unknown session", StringComparison.Ordinal)
        || error.Message.Contains("Ownership required", StringComparison.Ordinal)
        || error.Message.Contains("Invalid input", StringComparison.Ordinal)
        || error.Message.Contains("Unknown key", StringComparison.Ordinal);

    private static bool IsRuntimeFailure(Exception error) =>
        error is SentioraException or TimeoutException or IOException or OperationCanceledException;

    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _driver.DisposeAsync().ConfigureAwait(false);
            Shutdown = _driver.ForcedShutdown
                ? "the host had to be killed: it did not exit on its own"
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
