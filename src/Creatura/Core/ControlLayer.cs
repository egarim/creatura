namespace Creatura;

/// <summary>How the effect check seeds its verdict before any extension re-judges it.</summary>
public enum JudgeRule
{
    /// <summary>Landed or lied: the designs' documented default. A tool that reports failure with no effect counts as a lie.</summary>
    TwoWay,

    /// <summary>Landed, lied or honest failure. Landed is read first: a tool that delivered is never a liar whatever it said.</summary>
    ThreeWay,

    /// <summary>The map-keeper's three-way rule: a report of failure is an honest failure whatever else moved in the registry; only a report of success with nothing landed is a lie.</summary>
    ReportFirst,
}

/// <summary>What the layer does with an honest failure (reported failure, nothing landed).</summary>
public enum HonestFailureRule
{
    /// <summary>The designs' rule: quarantine the tool for the cooldown, record nothing, spend nothing.</summary>
    Habituate,

    /// <summary>The host's rule: the oracle that reports red is still the oracle. Prediction and reliability are recorded, budget is spent, the tool stays visible.</summary>
    StaysVisible,
}

/// <summary>When <see cref="ControlLayer.Failures"/> goes back to zero.</summary>
public enum ResetFailures
{
    /// <summary>The designs: failures accumulate across ticks and clear only on a landed effect or a forgiven veto.</summary>
    Never,

    /// <summary>The host: every round starts with a clean count.</summary>
    PerRound,
}

/// <summary>The layer's switches. The defaults are the host's (the one consumer at this step); a regulator sets its own.</summary>
public sealed record ControlLayerOptions
{
    public JudgeRule JudgeRule { get; init; } = JudgeRule.ThreeWay;

    public HonestFailureRule HonestFailureRule { get; init; } = HonestFailureRule.StaysVisible;

    /// <summary>The colony's rule: when the seat sees no route, distrust lapses at once instead of waiting out the cooldown.</summary>
    public bool ClearQuarantineOnNoRoute { get; init; }

    public ResetFailures ResetFailures { get; init; } = ResetFailures.PerRound;

    public int CooldownTicks { get; init; } = 3;

    /// <summary>How the control layer on Agent Framework has always run: three-way judge, honest failures stay visible, failures reset per round.</summary>
    public static readonly ControlLayerOptions Host = new();

    /// <summary>How the documented designs run: two-way judge, honest failures habituate, failures accumulate.</summary>
    public static readonly ControlLayerOptions Design = new()
    {
        JudgeRule = JudgeRule.TwoWay,
        HonestFailureRule = HonestFailureRule.Habituate,
        ResetFailures = ResetFailures.Never,
    };
}

/// <summary>
/// The control layer, host-free: the five principles between the seat and the tools.
///
///   1. judge by effect, not by report   — after every action, ask the registry whether the promised facts hold
///   2. quarantine with decay            — a tool whose effect did not land is hidden for a few ticks
///   3. the agent decides what is seen   — tools are filtered from the offer and refused on call
///   4. gates earned by track record     — a release-kind tool needs a prediction record, not a claim
///   5. goals written as evidence        — "done" is a predicate over facts, never the seat's text
///
/// Four components: <b>Visibility</b> (<see cref="Offer()"/>, over the <see cref="Quarantine"/>), <b>Gatekeeper</b>
/// (<see cref="Admit"/>: legality against the visible set, then the frame's gate, by default the
/// <see cref="StageInterlock"/>), <b>EffectCheck</b> (<see cref="Judge"/>: the registry, the <see cref="JudgeRule"/>,
/// the re-judge chain, the <see cref="HonestFailureRule"/>) and <b>Ledger</b> (the self-model, predictions, audit,
/// failures, trace and notes). It reads the world through <see cref="IWorldView"/> only and renders through an
/// <see cref="IControlFrame"/> (the host's by default, a regulator's in the <see cref="ReferenceLoop"/>). The
/// host's round verbs (<see cref="BeginRound()"/>, <see cref="Stop"/>, <see cref="Assemble"/>, <see cref="Close"/>,
/// <see cref="Sleep"/>) drive the Agent Framework host, whose tick is one model call.
/// </summary>
public sealed class ControlLayer
{
    private readonly IWorldView _world;
    private readonly AgentTask _task;
    private readonly ControlLayerOptions _options;
    private readonly IControlFrame _frame;
    private readonly List<TraceEvent> _trace;
    private readonly List<string> _notes;
    private double _lastProgress = -1.0;
    private IReadOnlyList<Tool>? _visible;

    public ControlLayer(
        IWorldView world,
        AgentTask task,
        SelfModel self,
        ControlLayerOptions? options = null,
        IControlFrame? frame = null,
        PredictionTracker? predictions = null,
        CoherenceAudit? audit = null,
        List<TraceEvent>? trace = null,
        List<string>? notes = null,
        SelfModel? ledger = null,
        IDistrust? distrust = null)
    {
        _world = world;
        _task = task;
        _options = options ?? ControlLayerOptions.Host;
        _frame = frame ?? HostFrame.Instance;
        _trace = trace ?? [];
        _notes = notes ?? [];
        Self = self;
        Ledger = ledger ?? self;
        Predictions = predictions ?? new PredictionTracker();
        Audit = audit ?? new CoherenceAudit();
        Distrust = distrust ?? new Quarantine(_options.CooldownTicks);
        Interlock = new StageInterlock(Self, Predictions, task.ReleaseKind);
        Freedom = Self.Stage.FreedomCap;
    }

    public ControlLayerOptions Options => _options;

    public IWorldView World => _world;

    public AgentTask Task => _task;

    // ---- Ledger -------------------------------------------------------------------------------------------

    public SelfModel Self { get; }

    /// <summary>The self-model the effect check records outcomes into: <see cref="Self"/> unless the regulator keeps two (the hybrid's cell).</summary>
    public SelfModel Ledger { get; }

    public PredictionTracker Predictions { get; }

    public CoherenceAudit Audit { get; }

    /// <summary>Visibility's first pass: the quarantine by default, or the regulator's own policy.</summary>
    public IDistrust Distrust { get; }

    public StageInterlock Interlock { get; }

    /// <summary>One tick per seat call (the host's rule) or per loop iteration; the loop that owns the seat calls <see cref="BeginTick"/>.</summary>
    public int Tick { get; private set; }

    /// <summary>The outcome the world reported for the action under way, when the tool wrapper handed it in.</summary>
    public ActionOutcome? LastOutcome { get; private set; }

    public int Failures { get; private set; }

    /// <summary>Set when a release was refused: the decision is a human's now.</summary>
    public string? Escalation { get; private set; }

    public IReadOnlyList<TraceEvent> Trace => _trace;

    public IReadOnlyList<string> Notes => _notes;

    public double Incoherence => 1.0 - Audit.Score;

    public double Freedom { get; private set; }

    public int Budget { get; private set; } = 1;

    /// <summary>The task's progress as read at <see cref="BeginRound()"/>.</summary>
    public double Progress { get; private set; }

    /// <summary>Rounds in a row that ended no further along than they started.</summary>
    public int StaleRounds { get; private set; }

    /// <summary>The visible set last offered, which <see cref="Admit"/> judges legality against.</summary>
    public IReadOnlyList<Tool> Visible => _visible ?? Offer();

    public void Note(string note) => _notes.Add(note);

    public void Record(string level, string setPoint, double stress, string action, TraceKind kind, string detail) =>
        _trace.Add(new TraceEvent(Tick, level, setPoint, stress, Freedom, action, kind, detail));

    /// <summary>A row with its own freedom column (a design's cell row carries the cell's freedom, not the layer's).</summary>
    public void Record(string level, string setPoint, double stress, double freedom, string action, TraceKind kind, string detail) =>
        _trace.Add(new TraceEvent(Tick, level, setPoint, stress, freedom, action, kind, detail));

    public void Escalate(string escalation) => Escalation = escalation;

    /// <summary>A failure spent outside the layer's own verbs (the loop's no-route tick).</summary>
    public void Spend() => Failures++;

    /// <summary>A vetoed hand-off forgives the attempts.</summary>
    public void Forgive() => Failures = 0;

    // ---- Verbs ---------------------------------------------------------------------------------------------

    public void BeginTick() => Tick++;

    /// <summary>
    /// Open a round for the host: stress is the worse of the contract gap and the incoherence of the record,
    /// plasticity turns it into freedom, the stage caps it, and the budget follows. Failures reset when the
    /// options say so.
    /// </summary>
    public void BeginRound()
    {
        var contract = _task.Contract(_world);
        var stress = Math.Max(contract < 1.0 ? 1.0 - contract : 0.0, Incoherence);
        var freedom = Math.Min(Self.Stage.FreedomCap, Plasticity.Freedom(stress));
        BeginRound(freedom, Creatura.Budget.Attempts(freedom));

        Progress = _task.Progress(_world);
        StaleRounds = Progress <= _lastProgress ? StaleRounds + 1 : 0;
        _lastProgress = Progress;
    }

    /// <summary>Open a round with a freedom and budget the regulator computed. Failures reset only under <see cref="ResetFailures.PerRound"/>.</summary>
    public void BeginRound(double freedom, int budget)
    {
        Freedom = freedom;
        Budget = budget;
        if (_options.ResetFailures == ResetFailures.PerRound) Failures = 0;
    }

    /// <summary>The host's stop check before the seat acts: rounds that act without moving the gap are handed to a human (Bach 01:56:31).</summary>
    public StopReason? Stop()
    {
        if (StaleRounds < 2) return null;
        var detail = $"{StaleRounds} rounds without progress at {Freedom:P0} freedom";
        Record("organism", "contract", 1.0 - Progress, TraceText.StuckRegulation, TraceKind.Escalated, detail);
        Note("stuck-regulation alarm: acting without moving the gap, handed to a human");
        return new StopReason(StopCause.StuckRegulation, detail, HandOff.StuckRegulation);
    }

    /// <summary>Visibility: the tools the seat may see this tick. Distrusted tools are simply not offered.</summary>
    public IReadOnlyList<Tool> Offer() => Offer(null);

    /// <summary>Visibility with the option space shaped after the quarantine (the regulator's rule, then the extensions). The result is what <see cref="Admit"/> judges against.</summary>
    public IReadOnlyList<Tool> Offer(Func<IReadOnlyList<Tool>, IReadOnlyList<Tool>>? shape)
    {
        var visible = Distrust.Visible(_world, Tick);
        _visible = shape is null ? visible : shape(visible);
        return _visible;
    }

    public bool IsVisible(string toolId) =>
        _world.AvailableTools.Any(t => string.Equals(t.Id, toolId, StringComparison.Ordinal))
        && !Distrust.Hides(toolId, Tick);

    /// <summary>What the seat reads before it acts on the host: its own record, and the row that says the round began.</summary>
    public string Assemble(int round)
    {
        Record("organism", "contract", 1.0 - Progress, TraceText.Round, TraceKind.Idle,
            $"round {round}: budget {Budget}, stage {Self.Stage.Level}");
        return Self.Render();
    }

    /// <summary>
    /// Gatekeeper: may this tool run? Legality against the visible set first (a seat that names a tool it was not
    /// offered is refused, never obeyed), then the gate for a release. A refusal spends budget; a refused release
    /// ends the round and leaves the decision to a human.
    /// </summary>
    public Admission Admit(string toolId)
    {
        var tool = _world.Tool(toolId);
        var visible = Visible;
        RefusalReason? refusal = tool is null ? RefusalReason.NoSuchTool
            : Distrust.Hides(toolId, Tick) ? RefusalReason.Quarantined
            : !IsVisible(toolId) ? RefusalReason.NotAvailable
            : visible.All(t => !string.Equals(t.Id, toolId, StringComparison.Ordinal)) ? RefusalReason.NotVisible
            : null;

        if (refusal is { } why)
        {
            Failures++;
            _frame.Refused(this, toolId, why);
            // A decision layer that keeps naming refused tools has run out of budget: end this round.
            return Admission.Refuse(tool, why, Failures > Budget,
                $"REFUSED: {toolId} is not available ({TraceText.Why(why)}). Choose among the tools offered.");
        }

        var gate = _frame.Gate(this, tool!);
        if (gate == GateDecision.Advanced)
        {
            _frame.StageAdvanced(this);
        }
        else if (gate == GateDecision.Refused)
        {
            _frame.ReleaseRefused(this, tool!);
            // The decision is a human's now; the function-calling loop ends here and the host reports.
            return Admission.Refuse(tool, RefusalReason.ReleaseNotEarned, true,
                $"REFUSED: {toolId} is a release action and this agent has not earned it yet. A human must approve it. Stop and report.");
        }

        LastOutcome = null;
        return Admission.Admit(tool!, gate);
    }

    /// <summary>The tool wrapper reports what the world answered, so the effect check can tell an honest failure from a lie.</summary>
    public void ReportOutcome(ActionOutcome outcome) => LastOutcome = outcome;

    /// <summary>
    /// Raised once per judged action, at the end of <see cref="Judge"/>: after the verdict, the re-judge chain and
    /// the observers, and after the record (predictions, ledger, distrust, audit) and the frame's row are written.
    /// The critic's seam: a <see cref="Hotz.HotzAgent"/> watches the layer through it instead of the world. What
    /// a subscriber does cannot reach the verdict or the feedback; an exception it throws does.
    /// </summary>
    public event Action<ActionObservation>? ActionObserved;

    /// <summary>
    /// EffectCheck: judge by the registry, never by the report. Landed is read first; then the report decides
    /// between a lie and an honest failure under <see cref="JudgeRule.ThreeWay"/>; then the re-judge chain (the
    /// extensions) and the observers run. A report the host did not see counts as a claim of success. The record
    /// is updated for everything but a habituated honest failure; the feedback is what the seat reads next; and
    /// <see cref="ActionObserved"/> is raised last.
    /// </summary>
    public EffectJudgement Judge(Tool tool, ActionReport report, Func<ActionVerdict, ActionVerdict>? rejudge = null, Action<ActionVerdict>? observed = null)
    {
        // A report the host did not see is a claim of success.
        var outcome = report.Outcome ?? new ActionOutcome(tool.Id, true, [], null);
        var judgement = JudgeAndRecord(tool, report, outcome, rejudge, observed);
        ActionObserved?.Invoke(new ActionObservation(Tick, tool, outcome, judgement.Verdict, judgement.Landed));
        return judgement;
    }

    private EffectJudgement JudgeAndRecord(Tool tool, ActionReport report, ActionOutcome outcome, Func<ActionVerdict, ActionVerdict>? rejudge, Action<ActionVerdict>? observed)
    {
        var landed = _world.EffectsLanded(tool);
        var verdict = _options.JudgeRule switch
        {
            // The world's own three-way verdict, report first (the map-keeper's order).
            JudgeRule.ReportFirst => _world.Verdict(tool, outcome),
            // Landed first, as the host has always judged; the world's verdict tells the honest failure from the lie.
            JudgeRule.ThreeWay => landed ? ActionVerdict.Landed : _world.Verdict(tool, outcome),
            _ => landed ? ActionVerdict.Landed : ActionVerdict.Lied,
        };
        if (rejudge is not null) verdict = rejudge(verdict);
        observed?.Invoke(verdict);
        var missing = tool.Grants.Where(g => !_world.Has(g)).ToList();

        if (verdict == ActionVerdict.HonestFailure && _options.HonestFailureRule == HonestFailureRule.Habituate)
        {
            // Not a lie and not a wrong prediction about the tool: habituate, say so, spend nothing.
            Distrust.Judged(tool.Id, verdict, Tick, habituated: true);
            _frame.Acted(this, tool, report, verdict, landed, habituated: true);
            return new EffectJudgement(verdict, false, missing, false,
                $"{report.Reported}\n\n[not verified: {string.Join(", ", missing)} still false; the tool reported failure]");
        }

        Predictions.Record(landed);
        Ledger.RecordOutcome(tool.Id, landed);
        Audit.Claim();
        if (verdict == ActionVerdict.Lied)
        {
            Audit.Accept($"{tool.Id} produces {string.Join(",", tool.Grants)}", false, _frame.Evidence(this, tool, report), Tick);
        }

        Distrust.Judged(tool.Id, verdict, Tick, habituated: false);
        if (landed)
        {
            if (_options.ResetFailures == ResetFailures.Never) Failures = 0;
            _frame.Acted(this, tool, report, verdict, landed, habituated: false);
            return new EffectJudgement(verdict, true, missing, false, $"{report.Reported}\n\n[verified: {string.Join(", ", tool.Grants)} now hold]");
        }

        Failures++;
        var terminate = Failures > Budget;

        if (verdict == ActionVerdict.HonestFailure)
        {
            // An honest failure: the tool said it failed and it did. That costs budget and reliability, but
            // it is not a lie, so the tool stays visible — the oracle that reports red is still the oracle.
            _frame.Acted(this, tool, report, verdict, landed, habituated: false);
            return new EffectJudgement(verdict, false, missing, terminate,
                $"{report.Reported}\n\n[not verified: {string.Join(", ", missing)} still false; the tool reported failure]");
        }

        // Reported success, effect missing: a contradiction. Distrust has hidden the tool; tell the seat why.
        _frame.Acted(this, tool, report, verdict, landed, habituated: false);
        return new EffectJudgement(verdict, false, missing, terminate,
            $"WARNING: {tool.Id} reported '{report.Summary}' but {string.Join(", ", missing)} did not become true. " +
            "Its effect did not land. It is distrusted for now; use a different route.");
    }

    /// <summary>
    /// Close a round on the host: success is a predicate over the world, never the seat's text; then the
    /// escalations in the host's order (a refused release, a spent budget), the seat's reply as a row, and no
    /// route, which is a wait while something is quarantined and a hand-off when nothing is.
    /// </summary>
    public Closure Close(string reply, bool noRoute)
    {
        if (_task.Goal(_world.Facts))
        {
            Record("organism", "contract", 0.0, TraceText.ContractMet, TraceKind.Acted, TraceText.Complete(_task.Name));
            return new Closure(true, new StopReason(StopCause.GoalMet, TraceText.Complete(_task.Name)));
        }

        if (Escalation is not null)
        {
            Note($"escalated: {Escalation}");
            return new Closure(false, new StopReason(StopCause.Escalation, Escalation));
        }

        if (Failures > Budget)
        {
            var detail = $"{Failures} failed calls exceeded the budget of {Budget}";
            Record("organism", "contract", 1.0 - _task.Progress(_world), TraceText.HandOff, TraceKind.Escalated, detail);
            Note("escalated: failures exceeded the freedom budget");
            return new Closure(false, new StopReason(StopCause.BudgetExceeded, detail));
        }

        Record("model", "reply", 1.0 - _task.Progress(_world), TraceText.Reply, TraceKind.Idle,
            reply.Length > 90 ? reply[..90] + "…" : reply);

        if (noRoute)
        {
            // The seat sees no route with the trusted tools. If something is quarantined, waiting is a route;
            // otherwise the goal is unregulatable with what this agent has.
            if (Distrust.Hidden(Tick).Count == 0)
            {
                Record("organism", "contract", 1.0 - _task.Progress(_world), TraceText.NoPath, TraceKind.Escalated, "no route with the available tools");
                Note("escalated: no path with the available tools; the goal is unregulatable");
                return new Closure(false, new StopReason(StopCause.NoPath, "no route with the available tools", HandOff.NoPath));
            }

            if (_options.ClearQuarantineOnNoRoute) Distrust.Clear();
            Note("no path right now; a distrusted tool may return after its cooldown");
        }

        return new Closure(false, null);
    }

    /// <summary>Consolidation on the host: the record the run leaves, as a row and a note.</summary>
    public void Sleep()
    {
        var skills = Self.Skills.Values.OrderBy(s => s.Action, StringComparer.Ordinal)
            .Select(s => $"{s.Action} {s.Reliability:P0}").ToList();
        Record("organism", "consolidation", 0.0, TraceText.Sleep, TraceKind.Consolidated,
            $"coherence {Audit.Score:P0}, predictions {Predictions.Accuracy:P0} over {Predictions.Total}, stage {Self.Stage.Level}");
        Note($"coherence {Audit.Score:P0}; reliabilities: {(skills.Count > 0 ? string.Join(", ", skills) : "none")}");
    }
}
