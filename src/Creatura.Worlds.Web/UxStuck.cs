namespace Creatura.Worlds.Web;

/// <summary>
/// What the loop's stuck alarm tells the seat: a reason, raised once and taken once. The world carries one
/// (<see cref="WebWorld.Stuck"/>), so the alarm and the seat share it without knowing each other.
/// </summary>
public sealed class UxStuckSignal
{
    private string? _reason;

    public void Raise(string reason) => _reason = reason;

    /// <summary>The reason, if the alarm has raised one since the last take; cleared by taking it.</summary>
    public string? Take()
    {
        var reason = _reason;
        _reason = null;
        return reason;
    }
}

/// <summary>
/// The stuck alarm, in the loop (uxkit's "no change after two steps", docs/design-web-worlds.md, phase 2c). After each
/// action it compares the screen with the one before. When <see cref="Patience"/> actions in a row have changed nothing,
/// it writes an alarm row, tells the seat in its notes, and raises the world's <see cref="UxStuckSignal"/>, so a persona
/// asks its stronger seat (with a screenshot, when a vision model is named) at once. When <see cref="Limit"/> actions in
/// a row have changed nothing, it takes the controls away: no tool is visible, and each design meets "no route" and
/// hands off by its own rule (the plain loop's budget, Bach's "no coherent path", the colony's handoff), which its
/// extensions may veto. It is an extension, so it rides every design unchanged. A step with no action is not counted.
/// </summary>
public sealed class UxStuckAlarm : IAgentExtension
{
    private readonly Func<string> _screen;
    private readonly UxStuckSignal _signal;
    private string? _last;

    /// <summary>The alarm over a web world: its rendered screen and its signal.</summary>
    public UxStuckAlarm(WebWorld world, int patience = 2, int limit = 6) : this(() => world.Current.Render(), world.Stuck, patience, limit)
    {
    }

    /// <param name="screen">The screen as the persona reads it, after the last action.</param>
    /// <param name="signal">Where the alarm tells the seat.</param>
    public UxStuckAlarm(Func<string> screen, UxStuckSignal signal, int patience = 2, int limit = 6)
    {
        if (patience < 1 || limit <= patience) throw new ArgumentException("the stuck alarm needs patience of at least 1 and a limit above it");
        _screen = screen;
        _signal = signal;
        Patience = patience;
        Limit = limit;
    }

    public string Name => "ux:stuck-alarm";

    public int Patience { get; }

    public int Limit { get; }

    /// <summary>Actions in a row that changed nothing on screen.</summary>
    public int Stale { get; private set; }

    /// <summary>How many times the alarm fired in the run (once per streak).</summary>
    public int Fired { get; private set; }

    /// <summary>Whether it took the controls away.</summary>
    public bool TookControls { get; private set; }

    public void BeforeRun(TickContext context) => _last = _screen();

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        var now = _screen();
        Stale = now == _last ? Stale + 1 : 0;
        _last = now;
        if (Stale == Patience)
        {
            Fired++;
            var reason = $"the screen has not changed after the last {Stale} steps";
            _signal.Raise(reason);
            context.Trace.Add(new TraceEvent(context.Tick, "organism", "screen", 1, context.Freedom, "stuck alarm", TraceKind.Idle, reason + ": the seat asks its stronger model"));
        }

        if (Stale >= Limit && !TookControls)
        {
            TookControls = true;
            context.Trace.Add(new TraceEvent(context.Tick, "organism", "screen", 1, context.Freedom, "stuck alarm", TraceKind.Idle,
                $"the screen has not changed after {Stale} steps: the controls are taken away; the design hands off by its own rule"));
        }
    }

    public IEnumerable<string> PolicyNotes(TickContext context) =>
        Stale >= Patience ? [$"The screen has not changed after your last {Stale} steps."] : [];

    public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible) => TookControls ? [] : visible;
}
