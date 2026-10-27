namespace Creatura;

/// <summary>
/// What the seat reads this tick beside the facts and the visible tools: the regulator's rendering of itself.
/// Null parts fall back to the task (goal, goal text, habit).
/// </summary>
public sealed record SeatView
{
    public static readonly SeatView Plain = new();

    /// <summary>The rendered self-model, when the regulator keeps one.</summary>
    public string? SelfModel { get; init; }

    public string? Stage { get; init; }

    /// <summary>The goal the seat is asked to reach this tick; null for the task's.</summary>
    public Func<IReadOnlySet<string>, bool>? Goal { get; init; }

    public string? GoalText { get; init; }

    /// <summary>The tools to reach for first; null for the task's habit.</summary>
    public IReadOnlyList<string>? Preferred { get; init; }

    /// <summary>Lines the seat reads before the extensions' own.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What a regulator sees and writes at a tick of the <see cref="ReferenceLoop"/>.</summary>
public sealed class RegulatorContext
{
    public required string Agent { get; init; }

    public required IWorldView World { get; init; }

    public required AgentTask Task { get; init; }

    public required string PolicyName { get; init; }

    public required AgentOptions Options { get; init; }

    public required int MaxTicks { get; init; }

    public required List<TraceEvent> Trace { get; init; }

    public required List<string> Notes { get; init; }

    public ControlLayer Layer { get; internal set; } = null!;

    public int Tick { get; internal set; }

    /// <summary>The error signal this tick, as the regulator computed it.</summary>
    public double Stress { get; internal set; }

    public double Freedom { get; internal set; }

    public int Budget { get; internal set; }

    public IReadOnlyList<Tool> Visible { get; internal set; } = [];

    /// <summary>The hook context the extensions see this tick, for a regulator that reads what they wrote.</summary>
    public TickContext Extensions { get; internal set; } = null!;

    public void Row(string level, string setPoint, double stress, double freedom, string action, TraceKind kind, string detail) =>
        Trace.Add(new TraceEvent(Tick, level, setPoint, stress, freedom, action, kind, detail));

    public void Note(string note) => Notes.Add(note);
}

/// <summary>
/// What fills the loop's slots: the error signal, what error buys, option shaping, the gate, memory, the stop,
/// consolidation, what the seat reads, and the words the rows and notes are written in (a regulator is its own
/// <see cref="IControlFrame"/>). Every member defaults to a no-op or to the plain rule, so a regulator implements
/// only the slots its design fills; a regulator with every default is a plain agent on the control layer.
/// A new design is a new regulator, never a new loop.
/// </summary>
public interface IRegulator : IControlFrame
{
    Identity Identity { get; }

    /// <summary>The self-model the regulator keeps, read by the extensions and the layer's interlock; null for a design without one.</summary>
    SelfModel? Self => null;

    /// <summary>
    /// Where the layer's effect check records each outcome's reliability. Every design records into the self-model it
    /// reads, except the hybrid, which records into its cell while the extensions read its organism (<see cref="Self"/>).
    /// </summary>
    SelfModel? Ledger => Self;

    PredictionTracker? Predictions => null;

    CoherenceAudit? Audit => null;

    /// <summary>Fresh per-run state, before the layer is built. A run starts from the design's own record, never the last run's.</summary>
    void Begin(AgentTask task)
    {
    }

    /// <summary>The header note and any tick-0 rows (the Bach agent reads its self-model before anything happens).</summary>
    void BeforeRun(RegulatorContext context)
    {
    }

    /// <summary>The stop check before the seat is asked (a story that closed, an alarm). A hand-off inside it is vetoable.</summary>
    StopReason? Stop(RegulatorContext context) => null;

    /// <summary>The error signal: distance from the set point, incoherence, anomaly. 0..1.</summary>
    double Error(RegulatorContext context) => 0.0;

    /// <summary>What error buys: freedom before the extension chain. The default is plasticity alone; a staged design caps it.</summary>
    double Freedom(RegulatorContext context, double error) => Plasticity.Freedom(error);

    /// <summary>Shape the option space after the quarantine and before the extensions.</summary>
    IReadOnlyList<Tool> Shape(RegulatorContext context, IReadOnlyList<Tool> visible) => visible;

    /// <summary>Before the seat is asked: ask it, run a fixed step, or skip the tick.</summary>
    PreDecision Decide(RegulatorContext context) => PreDecision.Ask;

    /// <summary>What the seat reads beside the facts and the tools.</summary>
    SeatView Seat(RegulatorContext context) => SeatView.Plain;

    /// <summary>The hand-off a tick with no route counts toward. The Bach agent and the hybrid say "no path"; the colony says "no route".</summary>
    HandOff NoRoute => HandOff.NoPath;

    /// <summary>The row for the tick's decision. The default is the designs' shared tissue row.</summary>
    void Suggested(RegulatorContext context, PolicySuggestion suggestion) =>
        context.Row("tissue", "obligations", context.Stress, context.Freedom, suggestion.Tool ?? TraceText.NoAction,
            suggestion.Tool is null ? TraceKind.Idle : TraceKind.Acted, suggestion.Rationale);

    /// <summary>A failure spent within the budget (the colony notes each one).</summary>
    void Spent(RegulatorContext context, HandOff wouldBe, string detail)
    {
    }

    /// <summary>Memory after a judged action, and the hand-off the verdict warrants, if any (incoherence, a repeated lie).</summary>
    HandOff? Record(RegulatorContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict) => null;

    /// <summary>Whether a vetoed hand-off forgives the attempts. The Bach agent keeps counting after an illegal action.</summary>
    bool ForgivesOnVeto(HandOff handOff) => true;

    /// <summary>The design's own bookkeeping when an extension vetoed a hand-off (the hybrid's alarm resets).</summary>
    void Vetoed(RegulatorContext context, HandOff handOff)
    {
    }

    /// <summary>The rows and notes of a hand-off the extensions did not veto. <paramref name="detail"/> is what the attempt was spent on (a rationale, "illegal action").</summary>
    void HandedOff(RegulatorContext context, HandOff handOff, string detail) =>
        context.Row("organism", "contract", 1.0, context.Freedom, TraceText.HandOff, TraceKind.Escalated, handOff.Text);

    /// <summary>The contract is met.</summary>
    void ContractMet(RegulatorContext context) =>
        context.Row("organism", "contract", 0.0, 0.0, TraceText.ContractMet, TraceKind.Acted, TraceText.Complete(context.Task.Name));

    /// <summary>Notes after the last tick, before sleep.</summary>
    void AfterRun(RegulatorContext context)
    {
    }

    /// <summary>The design's own consolidation. An extension may add to it or stand in for it.</summary>
    void Sleep(SleepContext context)
    {
    }

    // The frame: the plain words, for a regulator that does not render its own.
    void IControlFrame.Refused(ControlLayer layer, string toolId, RefusalReason why) =>
        layer.Record("tissue", "obligations", 1.0, layer.Freedom, toolId, TraceKind.Blocked, TraceText.NotVisible);

    void IControlFrame.StageAdvanced(ControlLayer layer) =>
        layer.Note(TraceText.StageAdvance(layer.Self.Stage, layer.Predictions.RecentAccuracy));

    void IControlFrame.ReleaseRefused(ControlLayer layer, Tool tool) =>
        layer.Record("organism", "stage", 1.0, 0.0, TraceText.DeployBlocked, TraceKind.Blocked, TraceText.StageForbidsRelease(layer.Self.Stage, layer.Predictions.RecentAccuracy));

    string IControlFrame.Evidence(ControlLayer layer, Tool tool, ActionReport report) =>
        $"reported '{report.Outcome?.Observation ?? "success"}' with no effect";

    void IControlFrame.Acted(ControlLayer layer, Tool tool, ActionReport report, ActionVerdict verdict, bool landed, bool habituated) =>
        layer.Record("cell", "effect", verdict == ActionVerdict.Landed ? 0.0 : 1.0, layer.Freedom, tool.Id,
            verdict == ActionVerdict.Landed ? TraceKind.Acted : TraceKind.Blocked,
            verdict switch
            {
                ActionVerdict.Landed => TraceText.EffectLanded,
                ActionVerdict.HonestFailure => TraceText.HonestFailure,
                _ => TraceText.EffectMissing(report.Outcome?.Observation),
            });
}
