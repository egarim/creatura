namespace Creatura;

/// <summary>
/// The hybrid regulator: the colony's body with the self-model's mind, sharing one error channel.
///
///   - every level is a pair (set point, self-model), and the self-model is read before that level acts
///   - the organism's stress is <i>incoherence</i>, a part's belief and the world disagreeing, not an average of
///     the children's stress: error = max(contract gap, 1 − coherence)
///   - freedom = min(stage cap, plasticity(stress)): error buys improvisation, the stage refuses past the
///     demonstrated ceiling
///   - acting without moving the gap is an alarm (<see cref="StuckRegulationAlarm"/>), not high stress
///   - consolidation rewrites the self-model and <i>proposes</i> set-point changes a human signs off
///     (<see cref="ProposeOnlySleep"/>)
///
/// Two self-models with different jobs: the organism (<see cref="Identity.Hybrid"/>) is what the extensions
/// read and seed and what the stage interlock gates; the cell (<see cref="Identity.Cell"/>) is where the
/// layer's effect check records each outcome's reliability (<see cref="Ledger"/>). A record carried in arrives
/// in the organism's model and is copied into the cell lazily, on the first render or record that finds the
/// cell empty, so it lands after the extension that seeds it. Written by hand over the shared parts rather than
/// as a join of the colony and the self-model regulators: its rows and notes are a third rendering that no
/// fold of the two reproduces (design-framework.md, refuted claim 5). The words are the hybrid's, so its
/// traces are unchanged behind the facade.
///
/// Anchors: nested levels and stress (02:46:49, 00:45:18); self-model read before acting (00:04:00); freedom
/// tied to error (02:48:01) capped by stage (00:21:15); coherence as the filter (01:21:27); consolidation
/// (01:26:43); goals rewritable but signed off (01:02:21); stuck regulation (01:56:31).
/// </summary>
public sealed class HybridRegulator : IRegulator
{
    private readonly Identity _cellIdentity;
    private readonly int _stage;
    private SelfModel _organism = new();
    private SelfModel _cell = new();
    private PredictionTracker _predictions = new();
    private CoherenceAudit _audit = new();
    private SetPoint _cellPoint = null!;
    private SetPoint _tissuePoint = null!;
    private SetPoint _organismPoint = null!;
    private StuckRegulationAlarm _alarm = new(5);
    private ProposeOnlySleep _sleep = new();
    private bool _landed = true;
    private RegulatorContext? _ctx;

    /// <param name="organism">Role, principal and commitments the organism's self-model starts from.</param>
    /// <param name="cell">The effect cell's identity; its self-model is the ledger of what each tool's effect came to.</param>
    /// <param name="stage">The stage the organism starts at (the hybrid: 3, social self). The cell is always at stage 1.</param>
    public HybridRegulator(Identity? organism = null, Identity? cell = null, int stage = 3)
    {
        Identity = organism ?? Identity.Hybrid;
        _cellIdentity = cell ?? Identity.Cell;
        _stage = stage;
    }

    public Identity Identity { get; }

    /// <summary>The organism: what the extensions read, what the interlock gates.</summary>
    public SelfModel? Self => _organism;

    /// <summary>The cell: where the layer records each outcome's reliability.</summary>
    public SelfModel? Ledger => _cell;

    public SelfModel Cell => _cell;

    public PredictionTracker? Predictions => _predictions;

    public CoherenceAudit? Audit => _audit;

    public StuckRegulationAlarm Alarm => _alarm;

    /// <summary>The proposals the sleep made; none was applied.</summary>
    public IReadOnlyList<string> Proposals => _sleep.Proposals;

    public void Begin(AgentTask task)
    {
        _organism = Identity.NewSelfModel(_stage);
        _cell = _cellIdentity.NewSelfModel(1);
        _predictions = new PredictionTracker();
        _audit = new CoherenceAudit();
        _sleep = new ProposeOnlySleep("contract");
        _landed = true;
        _cellPoint = new SetPoint { Id = "effect", Space = "tool effect", Progress = _ => _landed ? 1.0 : 0.0 };
        _tissuePoint = new SetPoint { Id = "obligations", Space = "plan progress", Progress = task.Progress };
        _organismPoint = new SetPoint { Id = "contract", Space = "service health", Progress = task.Contract };
    }

    /// <summary>The key the cell's self-model is kept under in a record's parts.</summary>
    public const string CellPart = "hybrid:cell";

    /// <summary>A carried cell arrives whole: the ledger of what each tool's effect came to, so the lazy organism-to-cell copy finds it full and leaves it.</summary>
    public void Load(AgentRecord record)
    {
        if (record.GetPart<SelfModelSnapshot>(CellPart) is { } cell) _cell.Restore(cell);
    }

    /// <summary>The cell is the part of this design worth keeping privately: the organism's model is the Core carry a record extension already keeps.</summary>
    public void Store(AgentRecord record) => record.SetPart(CellPart, _cell.Snapshot());

    public void BeforeRun(RegulatorContext context)
    {
        _ctx = context;
        // The alarm's patience is the quarantine's cooldown plus two: a distrusted tool has had time to come back.
        _alarm = new StuckRegulationAlarm(context.Layer.Options.CooldownTicks + 2);
        context.Note($"policy {context.PolicyName}; {context.Options.Describe()}");
    }

    /// <summary>The organism's error, made of the colony's channel: the parts disagree with the world. The alarm reads progress here, once per tick.</summary>
    public double Error(RegulatorContext context)
    {
        _alarm.Observe(context.Task.Progress(context.World));
        return Math.Max(_organismPoint.Stress(context.World), 1.0 - _audit.Score);
    }

    /// <summary>Error buys improvisation; the stage refuses past the demonstrated ceiling.</summary>
    public double Freedom(RegulatorContext context, double error) =>
        Math.Min(_organism.Stage.FreedomCap, Plasticity.Freedom(error));

    /// <summary>Stuck regulation: the alarm fired on this tick's progress; the hand-off is vetoable and carries this tick's freedom.</summary>
    public StopReason? Stop(RegulatorContext context) =>
        _alarm.Fired ? new StopReason(StopCause.StuckRegulation, _alarm.Detail(context.Freedom), HandOff.StuckRegulation) : null;

    public SeatView Seat(RegulatorContext context)
    {
        SeedCell();
        var cellRecord = _cell.Skills.Count == 0
            ? ""
            : Environment.NewLine + "cell record, how often each tool's effect landed when it said so:" + Environment.NewLine +
              string.Join(Environment.NewLine, _cell.Skills.Values.OrderBy(s => s.Action, StringComparer.Ordinal).Select(s => string.Create(TraceText.Culture, $"  {s.Action,-16} reliable {s.Reliability:P0} ({s.Supported}/{s.Attempted})")));
        return new SeatView
        {
            SelfModel = _organism.Render() + cellRecord,
            Stage = string.Create(TraceText.Culture, $"{_organism.Stage.Level} ({_organism.Stage.Name}), freedom cap {_organism.Stage.FreedomCap:F2}"),
        };
    }

    /// <summary>The tissue's row carries the tissue's own stress, not the organism's error that bought the freedom.</summary>
    public void Suggested(RegulatorContext context, PolicySuggestion suggestion) =>
        context.Row("tissue", "obligations", _tissuePoint.Stress(context.World), context.Freedom, suggestion.Tool ?? TraceText.NoAction,
            suggestion.Tool is null ? TraceKind.Idle : TraceKind.Acted, suggestion.Rationale);

    public HandOff? Record(RegulatorContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict) =>
        verdict == ActionVerdict.Lied ? HandOff.Incoherent : null;

    /// <summary>Every veto forgives the attempts but the illegal-action one: the colony keeps counting what it may not use.</summary>
    public bool ForgivesOnVeto(HandOff handOff) => handOff.Reason != HandOffReason.IllegalAction;

    /// <summary>A vetoed alarm counts again from zero.</summary>
    public void Vetoed(RegulatorContext context, HandOff handOff)
    {
        if (handOff.Reason == HandOffReason.StuckRegulation) _alarm.Reset();
    }

    public void HandedOff(RegulatorContext context, HandOff handOff, string detail)
    {
        switch (handOff.Reason)
        {
            case HandOffReason.StuckRegulation:
                context.Row("organism", "contract", context.Stress, context.Freedom, TraceText.StuckRegulation, TraceKind.Blocked, detail);
                context.Note("stuck-regulation alarm: acting without moving the gap, handed to a human");
                break;
            case HandOffReason.NoPath:
                context.Row("organism", "contract", context.Stress, context.Freedom, TraceText.NoPath, TraceKind.Escalated, "no coherent path within budget");
                context.Note("escalated: no coherent path; the goal is unregulatable with trusted tools");
                break;
            case HandOffReason.IllegalAction:
                context.Note("escalated: the policy kept proposing tools the colony may not use");
                break;
            default:
                context.Row("organism", "contract", 1.0, context.Freedom, TraceText.HandOff, TraceKind.Escalated, "coherence could not be restored");
                context.Note("escalated: coherence could not be restored inside the freedom budget");
                break;
        }
    }

    /// <summary>The contract-met row carries the tick's live freedom.</summary>
    public void ContractMet(RegulatorContext context) =>
        context.Row("organism", "contract", 0.0, context.Freedom, TraceText.ContractMet, TraceKind.Acted, TraceText.Complete(context.Task.Name));

    /// <summary>The cell's reliabilities are the part of this design worth carrying; the organism's model is what a record extension reads.</summary>
    public void AfterRun(RegulatorContext context)
    {
        foreach (var (k, v) in _cell.Skills) _organism.Skills[k] = v;
    }

    /// <summary>Consolidation rewrites the self-model and proposes goal edits; it never applies them.</summary>
    public void Sleep(SleepContext context)
    {
        context.Trace.Add(new TraceEvent(context.LastTick, "organism", "consolidation", 0, 0, TraceText.Sleep, TraceKind.Consolidated,
            string.Create(TraceText.Culture, $"coherence {_audit.Score:P0}, predictions {_predictions.Accuracy:P0}, stage {_organism.Stage.Level}")));
        context.Notes.Add(string.Create(TraceText.Culture, $"coherence {_audit.Score:P0}; cell reliabilities: ") +
                          string.Join(", ", _cell.Skills.Values.OrderBy(s => s.Action, StringComparer.Ordinal)
                              .Select(s => string.Create(TraceText.Culture, $"{s.Action} {s.Reliability:P0}"))));
        _sleep.Propose(context);
    }

    // ---- The frame: the hybrid's words for the layer's decisions ----------------------------------------------

    public void Refused(ControlLayer layer, string toolId, RefusalReason why) =>
        _ctx!.Row("organism", "coherence", _ctx.Stress, _ctx.Freedom, toolId, TraceKind.Blocked, TraceText.RefusedNotVisible);

    public void StageAdvanced(ControlLayer layer) =>
        _ctx!.Note(TraceText.OrganismStageAdvance(_organism.Stage));

    public void ReleaseRefused(ControlLayer layer, Tool tool)
    {
        _ctx!.Row("organism", "stage", _ctx.Stress, 0.0, TraceText.DeployBlocked, TraceKind.Blocked, TraceText.StageBlocksRelease(_organism.Stage, _predictions.RecentAccuracy));
        _ctx.Note("release refused by the stage interlock");
    }

    public string Evidence(ControlLayer layer, Tool tool, ActionReport report) =>
        $"reported '{report.Outcome?.Observation ?? "success"}' with no effect";

    public void Acted(ControlLayer layer, Tool tool, ActionReport report, ActionVerdict verdict, bool landed, bool habituated)
    {
        var ctx = _ctx!;
        _landed = landed;
        switch (verdict)
        {
            case ActionVerdict.HonestFailure:
                // Not a lie and not a wrong prediction about the tool: habituate, say so, move on.
                ctx.Row("cell", "effect", 0.0, ctx.Freedom, tool.Id, TraceKind.Blocked, TraceText.HonestFailure);
                break;
            case ActionVerdict.Landed:
                SeedCell();
                ctx.Row("cell", "effect", 0.0, ctx.Freedom, tool.Id, TraceKind.Acted, string.Create(TraceText.Culture, $"coherence {_audit.Score:P0}"));
                break;
            default:
                SeedCell();
                ctx.Row("cell", "effect", 1.0, ctx.Freedom, tool.Id, TraceKind.Blocked, TraceText.ContradictionQuarantined);
                break;
        }
    }

    /// <summary>A carried record arrives in the organism's model (that is what extensions see); the cell is where it is used. Copied once, when the cell is still empty.</summary>
    private void SeedCell()
    {
        if (_cell.Skills.Count != 0) return;
        foreach (var (k, v) in _organism.Skills) _cell.Skills[k] = v;
    }
}
