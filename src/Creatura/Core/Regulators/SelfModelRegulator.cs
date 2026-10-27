namespace Creatura;

/// <summary>
/// The self-model regulator (Bach): the self-model is load-bearing, coherence gates what it believes, and the
/// right to act at all is staged behind demonstrated consequence-modelling. Error is incoherence; the stage
/// interlock is the gate; a contradiction quarantines the belief and, past the budget, halts. The words are
/// the Bach agent's, so its traces are unchanged behind the facade.
///
/// Anchors: self-model created after the world (00:04:00) and read before acting; the outer mind is smarter
/// than the narration (00:32:04); coherence as the learning filter (01:21:27); stages as interlocks
/// (00:21:15, 00:22:11); sleep/consolidation (01:26:43).
/// </summary>
public sealed class SelfModelRegulator : IRegulator
{
    private readonly int _stage;
    private readonly string? _commitment;
    private SelfModel _self = new();
    private PredictionTracker _predictions = new();
    private CoherenceAudit _audit = new();
    private RegulatorContext? _ctx;

    /// <param name="identity">Role, principal, commitments and vessel the self-model starts from.</param>
    /// <param name="stage">The stage the self-model starts at (the Bach agent: 3, social self).</param>
    /// <param name="commitment">One more open commitment beside the identity's; null for the identity's alone.</param>
    public SelfModelRegulator(Identity identity, int stage = 3, string? commitment = null)
    {
        Identity = identity;
        _stage = stage;
        _commitment = commitment;
    }

    public Identity Identity { get; }

    public SelfModel? Self => _self;

    public PredictionTracker? Predictions => _predictions;

    public CoherenceAudit? Audit => _audit;

    public void Begin(AgentTask task)
    {
        _self = Identity.NewSelfModel(_stage);
        if (_commitment is not null) _self.OpenCommitments.Add(_commitment);
        _predictions = new PredictionTracker();
        _audit = new CoherenceAudit();
    }

    public void BeforeRun(RegulatorContext context)
    {
        _ctx = context;
        context.Note($"policy {context.PolicyName}; {context.Options.Describe()}");
        // The record is read before anything else happens — that read is what makes it causal.
        context.Trace.Add(new TraceEvent(0, "self", "self-model", 0, 0, TraceText.ReadSelfModel, TraceKind.Idle,
            $"role={_self.Role} principal={_self.Principal} stage={_self.Stage.Level} ({_self.Stage.Name})"));
    }

    public double Error(RegulatorContext context) => 1.0 - _audit.Score;

    public SeatView Seat(RegulatorContext context) => new()
    {
        SelfModel = _self.Render(),
        Stage = $"{_self.Stage.Level} ({_self.Stage.Name})",
    };

    public HandOff? Record(RegulatorContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict) =>
        verdict == ActionVerdict.Lied ? HandOff.Incoherent : null;

    public bool ForgivesOnVeto(HandOff handOff) => handOff.Reason != HandOffReason.IllegalAction;

    public void HandedOff(RegulatorContext context, HandOff handOff, string detail)
    {
        switch (handOff.Reason)
        {
            case HandOffReason.NoPath:
                context.Row("self", "coherence", context.Stress, context.Freedom, TraceText.NoPath, TraceKind.Escalated, "policy found no coherent path within budget");
                context.Note("escalated: the policy sees no coherent path with trusted tools");
                break;
            case HandOffReason.IllegalAction:
                context.Note("halted: the policy kept proposing tools it may not use");
                break;
            default:
                context.Row("self", "coherence", 1.0 - _audit.Score, context.Freedom, TraceText.Halt, TraceKind.Escalated, "beliefs kept failing the audit");
                context.Note("halted: coherence could not be restored");
                break;
        }
    }

    public void Sleep(SleepContext context)
    {
        // Consolidation: rewrite the self-model from the day's evidence (01:26:43).
        var skills = _self.Skills.Values
            .OrderByDescending(s => s.Reliability)
            .Select(s => $"{s.Action} {s.Reliability:P0}");
        context.Trace.Add(new TraceEvent(context.LastTick, "self", "consolidation", 0, 0, TraceText.Sleep, TraceKind.Consolidated,
            $"coherence {_audit.Score:P0}, predictions {_predictions.Accuracy:P0} over {_predictions.Total}, stage {_self.Stage.Level}"));
        context.Notes.Add(skills.Any() ? $"consolidated: {string.Join(", ", skills)}" : "consolidated: no evidence collected");
        context.Notes.Add($"coherence {_audit.Score:P0} ({_audit.Contradictions.Count} contradictions)");
    }

    // ---- The frame: the Bach agent's words for the layer's decisions ----------------------------------------

    public void Refused(ControlLayer layer, string toolId, RefusalReason why) =>
        _ctx!.Row("self", "coherence", _ctx.Stress, _ctx.Freedom, toolId, TraceKind.Blocked, TraceText.RefusedNotVisible);

    public void StageAdvanced(ControlLayer layer) =>
        _ctx!.Note(TraceText.StageAdvance(_self.Stage, _predictions.RecentAccuracy));

    public void ReleaseRefused(ControlLayer layer, Tool tool)
    {
        // Stage interlock: release is a capability, not a step in a plan.
        _ctx!.Row("self", "stage", 1.0, 0.0, TraceText.DeployBlocked, TraceKind.Blocked, TraceText.StageForbidsRelease(_self.Stage, _predictions.RecentAccuracy));
        _ctx.Note("release refused: the record does not support the capability");
    }

    public string Evidence(ControlLayer layer, Tool tool, ActionReport report) =>
        $"postcondition missing after '{report.Outcome?.Observation ?? "success"}'";

    public void Acted(ControlLayer layer, Tool tool, ActionReport report, ActionVerdict verdict, bool landed, bool habituated)
    {
        var ctx = _ctx!;
        switch (verdict)
        {
            case ActionVerdict.HonestFailure:
                // Not a lie and not a wrong prediction about the tool: habituate, say so, move on.
                ctx.Row("cell", "effect", 0.0, ctx.Freedom, tool.Id, TraceKind.Blocked, TraceText.HonestFailure);
                break;
            case ActionVerdict.Landed:
                ctx.Row("cell", "effect", 0.0, ctx.Freedom, tool.Id, TraceKind.Acted, $"predicted correctly ({_predictions.RecentAccuracy:P0})");
                break;
            default:
                // Fast appraiser acted; the slow auditor disagrees, and the disagreement is logged, not smoothed.
                ctx.Row("cell", "effect", 1.0, ctx.Freedom, tool.Id, TraceKind.Blocked, TraceText.Contradiction(report.Outcome?.Observation));
                ctx.Note($"belief quarantined: {tool.Id} produces {string.Join(",", tool.Grants)} — {report.Outcome?.Observation ?? "no effect"}");
                break;
        }
    }
}
