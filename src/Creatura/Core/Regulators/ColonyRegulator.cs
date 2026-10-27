namespace Creatura;

/// <summary>Which level of the stress bus buys the colony's freedom.</summary>
public enum ErrorChannel
{
    Cell,
    Tissue,
    Organism,
}

/// <summary>
/// The colony regulator (Levin): nested homeostatic controllers, not a planner with tools. Three set points in
/// their own spaces (cell: my tool's effect landed; tissue: the obligations are closing; organism: the contract),
/// one leaky stress bus between them, freedom bought by the effective error of one channel, and an organism
/// whose only lever is to widen the budget or hand the goal to a human. No self-model, no stage, no sleep of
/// its own: it regulates without representing itself. The words are the Levin colony's, so its traces are
/// unchanged behind the facade.
///
/// Anchors: set points are writable objects (01:01:45); stress is the error function and it leaks (02:46:49,
/// 02:48:35); plasticity rises with error (02:48:01); the light cone bounds the goal (00:25:59); the top level
/// shapes option spaces rather than issuing actions (02:38:52).
/// </summary>
public sealed class ColonyRegulator : IRegulator
{
    private readonly double _leak;
    private readonly ErrorChannel _channel;
    private StressBus _bus = new();
    private SetPoint _cellPoint = null!;
    private SetPoint _tissuePoint = null!;
    private SetPoint _organismPoint = null!;
    private bool _landed = true;
    private double _cellFreedom;
    private RegulatorContext? _ctx;

    public ColonyRegulator(Identity identity, double leak = 0.5, ErrorChannel channel = ErrorChannel.Tissue)
    {
        Identity = identity;
        _leak = leak;
        _channel = channel;
    }

    public Identity Identity { get; }

    public StressBus Bus => _bus;

    public HandOff NoRoute => HandOff.NoRoute;

    public void Begin(AgentTask task)
    {
        _bus = new StressBus();
        _landed = true;
        _cellPoint = new SetPoint { Id = "effect", Space = "tool effect", Progress = _ => _landed ? 1.0 : 0.0 };
        _tissuePoint = new SetPoint { Id = "obligations", Space = "plan progress", Progress = task.Progress };
        _organismPoint = new SetPoint { Id = "contract", Space = "service health", Progress = task.Contract };
    }

    public void BeforeRun(RegulatorContext context)
    {
        _ctx = context;
        context.Note($"levels: cell(effect) → tissue(obligations) → organism(contract); policy {context.PolicyName}; {context.Options.Describe()}");
    }

    /// <summary>Every level publishes its stress; the error is the chosen channel's effective stress, raised by what leaks from its neighbours.</summary>
    public double Error(RegulatorContext context)
    {
        _bus.Tick = context.Tick;
        _bus.Publish("cell", _cellPoint, context.World);
        _bus.Publish("tissue", _tissuePoint, context.World);
        _bus.Publish("organism", _organismPoint, context.World);
        return _bus.Effective(Level(_channel), _leak);
    }

    /// <summary>The tissue's row carries the tissue's own stress, not the effective error that bought the freedom.</summary>
    public void Suggested(RegulatorContext context, PolicySuggestion suggestion) =>
        context.Row("tissue", "obligations", _tissuePoint.Stress(context.World), context.Freedom, suggestion.Tool ?? TraceText.NoAction,
            suggestion.Tool is null ? TraceKind.Idle : TraceKind.Acted, suggestion.Rationale);

    /// <summary>The organism's lever, short of a hand-off: widen the budget and say so.</summary>
    public void Spent(RegulatorContext context, HandOff wouldBe, string detail) =>
        context.Note($"organism shaping: budget {context.Budget}/4, attempts {context.Layer.Failures} — widening autonomy ({detail})");

    public HandOff? Record(RegulatorContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict) =>
        verdict == ActionVerdict.Lied ? HandOff.RepeatedEffectFailure : null;

    public void HandedOff(RegulatorContext context, HandOff handOff, string detail)
    {
        // A lie is judged at the cell's freedom; the other hand-offs at the tick's.
        var freedom = handOff.Reason == HandOffReason.RepeatedEffectFailure ? _cellFreedom : context.Freedom;
        context.Note($"organism: goal is not regulatable ({detail}) — escalating to a human");
        context.Row("organism", "contract", 1.0, freedom, TraceText.HandOff, TraceKind.Escalated, $"stuck regulation after {context.Layer.Failures} attempts");
    }

    public void AfterRun(RegulatorContext context)
    {
        var stillQuarantined = context.Layer.Distrust.Hidden(context.Tick);
        context.Note($"quarantined at end: {(stillQuarantined.Count == 0 ? "none" : string.Join(", ", stillQuarantined))}");
    }

    // ---- The frame: the colony's words for the layer's decisions ----------------------------------------------

    /// <summary>The colony stages nothing: a release is a step like any other.</summary>
    public GateDecision Gate(ControlLayer layer, Tool tool) => GateDecision.Open;

    public void Refused(ControlLayer layer, string toolId, RefusalReason why) =>
        _ctx!.Row("tissue", "obligations", 1.0, _ctx.Freedom, toolId, TraceKind.Blocked, TraceText.NotVisible);

    public void StageAdvanced(ControlLayer layer)
    {
    }

    public void ReleaseRefused(ControlLayer layer, Tool tool)
    {
    }

    public string Evidence(ControlLayer layer, Tool tool, ActionReport report) =>
        $"reported '{report.Outcome?.Observation ?? "success"}' with no effect";

    /// <summary>The cell publishes what came of its action; its row carries the cell's own stress and the freedom that stress buys.</summary>
    public void Acted(ControlLayer layer, Tool tool, ActionReport report, ActionVerdict verdict, bool landed, bool habituated)
    {
        var ctx = _ctx!;
        _landed = landed;
        _bus.Publish("cell", _cellPoint, ctx.World);
        _cellFreedom = Plasticity.Freedom(_bus.Effective("cell", _leak));
        ctx.Row("cell", "effect", _bus.Own("cell"), _cellFreedom, tool.Id,
            landed ? TraceKind.Acted : TraceKind.Blocked,
            verdict switch
            {
                ActionVerdict.Landed => TraceText.EffectLanded,
                ActionVerdict.HonestFailure => TraceText.HonestFailure,
                _ => TraceText.EffectMissing(report.Outcome?.Observation),
            });
        if (verdict == ActionVerdict.Lied)
        {
            ctx.Note($"{tool.Id}: claimed success, effect missing — distrusted, re-planning");
        }
    }

    private static string Level(ErrorChannel channel) => channel switch
    {
        ErrorChannel.Cell => "cell",
        ErrorChannel.Organism => "organism",
        _ => "tissue",
    };
}
