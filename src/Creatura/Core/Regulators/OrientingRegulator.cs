using Creatura.Orienting;

namespace Creatura;

/// <summary>
/// The orienting regulator (Sokolov, read through Peterson; design-sokolov.md). Every outcome is read twice: once
/// against the trace of what the action did last time in this situation (<see cref="HabituationByMatch"/>: a match
/// that delivered habituates, any change orients), and once against what the agent wants (<see cref="Valence"/>:
/// four kinds plus relief, from the task's <see cref="ValenceTable"/>). The two readings move two channels that are
/// never summed (<see cref="Affect"/>: approach and inhibition) and the operant weights per tool and goal
/// (<see cref="ValenceWeights"/>). Freedom = min(A, 1 − I). An unclassified action carries promise and threat at
/// full strength until consequence classifies it.
///
/// How it fills the slots: error is inhibition; a discrepancy spends the next tick orienting (<see cref="Decide"/>
/// skips it); a first lie is then approached once under the brake (the map-keeper's rule, through the map-keeper's
/// distrust: <see cref="ApproachThenQuarantine"/>, so the step being approached is visible by construction and a
/// second lie is unknown territory with its threat at the ceiling); a release is held while inhibition is above
/// its resting level (<see cref="Hold"/>); the seat is handed the actions scored by promise
/// minus caution times threat, the leader as what to reach for first and the top A-sized slice in its notes
/// (<see cref="Seat"/>); habituation is
/// a response filter consulted when an outcome is read, never a visibility filter; a punishment that leaves no
/// action with promise above threat for three ticks is profound, and the regulator proposes a revaluation of its
/// ends and hands off (<see cref="ProposeOnlySleep"/>: proposed, never applied). The valence table is world data
/// (<see cref="AgentTask.Valence"/>), inferred from the catalogue when the task states none.
///
/// Decisions taken where the design left room, each named in the step-8 note: the A-sized slice is handed to the
/// seat as a note rather than as a cut of the visible set, and a release under the brake is held rather than hidden,
/// because a seat that plans needs the route in sight (what hope widens is what it reaches for first; what anxiety
/// stops is the release itself); the one tool the seat is told to reach for first is the leader of the ranking,
/// since a seat that takes a set cannot read an order; and the orienting response (both channels raised, the next tick spent) fires on a
/// discrepancy with an earlier trace or on a lie, not on the first meeting of an action, whose pre-loading with
/// promise and threat is already in its unclassified weights.
/// </summary>
public sealed class OrientingRegulator : IRegulator
{
    private readonly int _unknownCooldown;
    private readonly int _habituatedCooldown;
    private readonly int _profoundAfter;
    private readonly double _restingInhibition;
    private HabituationByMatch _habituation = new();
    private ValenceWeights _weights = new();
    private Affect _affect = new();
    private ValenceTable _table = ValenceTable.Empty;
    private ProposeOnlySleep _sleep = new("goal");
    private ApproachThenQuarantine _distrust;
    private AgentTask _task = null!;
    private string? _pendingLie;
    private bool _oriented;
    private bool _feltThisTick;
    private int _profoundTicks;
    private HashSet<string> _before = new(StringComparer.Ordinal);
    private readonly HashSet<string> _satisfied = new(StringComparer.Ordinal);
    private readonly List<string> _deliberate = [];
    private string? _approachingThreat;
    private RegulatorContext? _ctx;

    /// <param name="identity">Who the agent is to the layer's ledger; it keeps no self-model.</param>
    /// <param name="restingInhibition">Where inhibition settles with nothing happening; a record may carry a lower one (claim 10).</param>
    /// <param name="unknownCooldown">How long a tool that lied on approach stays out of sight.</param>
    /// <param name="habituatedCooldown">How long an honest failure rests.</param>
    /// <param name="profoundAfter">Ticks with no visible action whose promise is above its threat before the punishment counts as profound.</param>
    public OrientingRegulator(Identity? identity = null, double restingInhibition = 0.2, int unknownCooldown = 6, int habituatedCooldown = 3, int profoundAfter = 3)
    {
        Identity = identity ?? Identity.Orienting;
        _restingInhibition = restingInhibition;
        _unknownCooldown = unknownCooldown;
        _habituatedCooldown = habituatedCooldown;
        _profoundAfter = profoundAfter;
        _distrust = NewDistrust();
    }

    public Identity Identity { get; }

    public IDistrust? Distrust => _distrust;

    public Affect Affect => _affect;

    public ValenceWeights Weights => _weights;

    public HabituationByMatch Habituation => _habituation;

    /// <summary>How many ticks the run spent orienting instead of acting.</summary>
    public int OrientingTicks { get; private set; }

    /// <summary>The proposals made mid-run under profound punishment; none applied.</summary>
    public IReadOnlyList<string> Proposals => _sleep.Proposals;

    private ApproachThenQuarantine NewDistrust() => new(_unknownCooldown, _habituatedCooldown, () => _pendingLie);

    public void Begin(AgentTask task)
    {
        _task = task;
        _habituation = new HabituationByMatch();
        _weights = new ValenceWeights();
        _affect = new Affect(_restingInhibition);
        _sleep = new ProposeOnlySleep("goal");
        _distrust = NewDistrust();
        _pendingLie = null;
        _oriented = false;
        _feltThisTick = false;
        _profoundTicks = 0;
        _satisfied.Clear();
        _deliberate.Clear();
        _approachingThreat = null;
        OrientingTicks = 0;
    }

    /// <summary>The key the resting inhibition is kept under in a record's parts.</summary>
    public const string RestingInhibitionPart = "orienting:resting-inhibition";

    /// <summary>Courage carries (claim 10): a record's resting inhibition replaces the configured one for this run.</summary>
    public void Load(AgentRecord record)
    {
        if (record.GetPart<double?>(RestingInhibitionPart) is { } resting) _affect = new Affect(resting);
    }

    public void Store(AgentRecord record) => record.SetPart(RestingInhibitionPart, _affect.RestingI);

    public void BeforeRun(RegulatorContext context)
    {
        _ctx = context;
        _table = _task.Valence ?? ValenceTable.Infer(context.World.AllTools, context.World.Facts);
        foreach (var tool in context.World.AllTools) _weights.Declare(tool.Id, tool.Grants);
        context.Note($"policy {context.PolicyName}; {context.Options.Describe()}; valence table {(_task.Valence is null ? "inferred from the catalogue" : "from the task")}: " +
                     $"goals {Join(_table.Goals)}, steps {_table.StepToward.Count}, trouble {Join(_table.Trouble)}");
    }

    /// <summary>The tick opens: a quiet tick settles both channels; the error is inhibition. The profound-punishment count reads the whole territory.</summary>
    public double Error(RegulatorContext context)
    {
        if (!_feltThisTick) _affect.Settle();
        _feltThisTick = false;

        // Profound: every action the world offers has been tried and found more threatening than promising. Read over
        // the whole territory, hidden actions included, so a quarantine cannot make the count vacuous; an untried action
        // still carries promise, so it keeps the count at zero.
        var territory = context.World.AvailableTools;
        var profound = territory.Count > 0 && territory.All(t => { var w = _weights.Get(t.Id, GoalFor(t)); return w.Seen > 0 && w.Promise < w.Threat; });
        _profoundTicks = profound ? _profoundTicks + 1 : 0;
        return _affect.I;
    }

    /// <summary>Hope widens the search and anxiety narrows it: freedom = min(A, 1 − I), floored at 0.2.</summary>
    public double Freedom(RegulatorContext context, double error) => _affect.Freedom;

    /// <summary>A punishment that leaves no action with promise above threat for three ticks is profound: propose a revaluation of the ends and hand off.</summary>
    public StopReason? Stop(RegulatorContext context)
    {
        if (_profoundTicks < _profoundAfter) return null;
        var why = $"no visible action with promise above threat for {_profoundTicks} ticks";
        _sleep.Propose(context, why);
        return new StopReason(StopCause.Escalation, why, HandOff.Revaluation);
    }

    /// <summary>How far above its resting level inhibition may sit before a release is held: the brake is "on" past this.</summary>
    public double ReleaseTolerance { get; init; } = 0.1;

    /// <summary>
    /// Releases only when inhibition is low: a release is held while the brake is on (inhibition above its resting
    /// level by more than the tolerance and not outweighed by approach), and the channels settle meanwhile. The
    /// step being approached is never held.
    /// </summary>
    public HoldDecision? Hold(RegulatorContext context, Tool tool)
    {
        if (tool.Kind != _task.ReleaseKind || tool.Id == _pendingLie) return null;
        if (_affect.I <= _affect.RestingI + ReleaseTolerance || _affect.I < _affect.A) return null;
        context.Row("tissue", "affect", _affect.I, context.Freedom, "hold release", TraceKind.Idle,
            $"{tool.Id} is a release and inhibition {_affect.I:F2} is above rest {_affect.RestingI:F2} and above approach {_affect.A:F2}: waiting for the brake to come off");
        return HoldDecision.Wait;
    }

    /// <summary>A discrepancy seizes behaviour: the tick after it is spent re-scoring; a first lie is then approached once, under the brake.</summary>
    public PreDecision Decide(RegulatorContext context)
    {
        if (_oriented)
        {
            _oriented = false;
            OrientingTicks++;
            context.Row("tissue", "affect", _affect.I, context.Freedom, "orient", TraceKind.Idle,
                $"discrepancy: re-scoring {context.Visible.Count} actions; A {_affect.A:F2} I {_affect.I:F2}{(_pendingLie is null ? "" : $"; {_pendingLie} to be approached once")}");
            return PreDecision.Skip("orienting");
        }

        if (_pendingLie is not null && context.Visible.Any(t => t.Id == _pendingLie))
        {
            return PreDecision.Fixed(_pendingLie, $"approach under the brake: {_pendingLie} promised and did not deliver; run it once more, on purpose, to see what it does");
        }

        return PreDecision.Ask;
    }

    /// <summary>The seat reads the actions scored by promise minus caution times threat, the top A-sized slice as the ones to choose among, and the leader as the one to reach for first.</summary>
    public SeatView Seat(RegulatorContext context)
    {
        var scored = Scored(context.Visible);
        var breadth = Math.Max(1, (int)Math.Ceiling(_affect.A * scored.Count));
        var notes = new List<string>
        {
            $"AFFECT: approach {_affect.A:F2} (hope), inhibition {_affect.I:F2} (anxiety), resting inhibition {_affect.RestingI:F2}{(_affect.Conflicted ? "; conflicted" : "")}",
            "ACTIONS BY VALENCE (promise − caution × threat; unclassified carries both at 1): " +
            string.Join(", ", scored.Select(s => $"{s.Tool.Id} {s.Score:+0.00;-0.00} ({(s.Weight.Seen == 0 ? "unclassified" : $"p {s.Weight.Promise:F2} t {s.Weight.Threat:F2}")})")),
        };
        if (scored.Count > 0) notes.Add($"CHOOSE AMONG (hope sizes this list, {breadth} of {scored.Count}): {string.Join(", ", scored.Take(breadth).Select(s => s.Tool.Id))}");
        if (_satisfied.Count > 0) notes.Add($"SATISFIED (drives satiated): {Join(_satisfied)}");
        return new SeatView
        {
            Preferred = scored.Count == 0 ? [] : [scored[0].Tool.Id],
            Notes = notes,
        };
    }

    public void Suggested(RegulatorContext context, PolicySuggestion suggestion)
    {
        _before = new HashSet<string>(context.World.Facts, StringComparer.Ordinal);
        _approachingThreat = null;
        if (suggestion.Tool is { } id && context.World.Tool(id) is { } tool)
        {
            var w = _weights.Get(id, GoalFor(tool));
            if (w.Seen > 0 && w.Threat > w.Promise) _approachingThreat = id;
        }

        context.Row("tissue", "affect", _affect.I, context.Freedom, suggestion.Tool ?? TraceText.NoAction,
            suggestion.Tool is null ? TraceKind.Idle : TraceKind.Acted, suggestion.Rationale);
    }

    public void Vetoed(RegulatorContext context, HandOff handOff)
    {
        if (handOff.Reason == HandOffReason.Revaluation) _profoundTicks = 0;
    }

    public void HandedOff(RegulatorContext context, HandOff handOff, string detail)
    {
        switch (handOff.Reason)
        {
            case HandOffReason.Revaluation:
                context.Row("organism", "goal", _affect.I, context.Freedom, TraceText.HandOff, TraceKind.Escalated, $"profound punishment: {detail}; revaluation proposed, not applied");
                context.Note("handed off: a punishment profound against the goal; the ends are the principal's to change");
                break;
            case HandOffReason.NoPath:
                context.Row("organism", "goal", _affect.I, context.Freedom, TraceText.NoPath, TraceKind.Escalated, "no path within budget");
                context.Note("escalated: no path with the actions in sight");
                break;
            case HandOffReason.IllegalAction:
                context.Note("escalated: the policy kept proposing actions it may not use");
                break;
            default:
                context.Row("organism", "goal", 1.0, context.Freedom, TraceText.HandOff, TraceKind.Escalated, handOff.Text);
                break;
        }
    }

    /// <summary>At sleep: the trace table, the habituated actions, the weights, the courage earned and how many orienting ticks the run spent.</summary>
    public void Sleep(SleepContext context)
    {
        var habituated = _habituation.Habituated.ToList();
        context.Trace.Add(new TraceEvent(context.LastTick, "organism", "consolidation", _affect.I, _affect.Freedom, TraceText.Sleep, TraceKind.Consolidated,
            $"A {_affect.A:F2} I {_affect.I:F2} resting I {_affect.RestingI:F2}; {OrientingTicks} orienting tick(s); {habituated.Count} habituated; {_weights.Learned.Count()} weight(s)"));
        var entries = _habituation.Entries.ToList();
        context.Notes.Add("trace: " + (entries.Count == 0 ? "nothing met" : string.Join("; ", entries.Select(e => $"{e.Action} [{Short(e.Situation)}] {e.Last} ×{e.Matches}"))));
        context.Notes.Add("habituated: " + (habituated.Count == 0 ? "none" : string.Join(", ", habituated)));
        context.Notes.Add("weights: " + (!_weights.Learned.Any() ? "none learned" : string.Join("; ", _weights.Learned.Select(w => $"{w.Tool}@{w.Goal} p {w.Weight.Promise:F2} t {w.Weight.Threat:F2} ({w.Weight.Seen})"))));
        context.Notes.Add($"orienting ticks: {OrientingTicks}; deliberate approaches that ended in control: {_deliberate.Count}{(_deliberate.Count == 0 ? "" : $" ({string.Join(", ", _deliberate)})")}; resting inhibition {_affect.RestingI:F2}");
    }

    // ---- The frame: the orienting agent's words for the layer's decisions --------------------------------------

    /// <summary>No stage: a release is gated by inhibition (<see cref="Shape"/>), not by a record.</summary>
    public GateDecision Gate(ControlLayer layer, Tool tool) => GateDecision.Open;

    public void Refused(ControlLayer layer, string toolId, RefusalReason why)
    {
        if (toolId == _pendingLie) _pendingLie = null;
        _ctx!.Row("tissue", "affect", 1.0, _ctx.Freedom, toolId, TraceKind.Blocked, TraceText.NotVisible);
    }

    public void StageAdvanced(ControlLayer layer)
    {
    }

    public void ReleaseRefused(ControlLayer layer, Tool tool)
    {
    }

    public string Evidence(ControlLayer layer, Tool tool, ActionReport report) =>
        $"reported '{report.Outcome?.Observation ?? "success"}' with no effect";

    /// <summary>Read the outcome twice: against the trace (habituate or orient) and against the goal (the four kinds plus relief); move the channels and the weights.</summary>
    public void Acted(ControlLayer layer, Tool tool, ActionReport report, ActionVerdict verdict, bool landed, bool habituated)
    {
        var ctx = _ctx!;
        var outcome = report.Outcome!;
        var after = ctx.World.Facts;
        var goal = GoalFor(tool);
        var gained = after.Where(f => !_before.Contains(f)).ToList();
        var situation = HabituationByMatch.Situation(_before, tool.Requires);
        var reading = Valence.Classify(_before, after, outcome.Succeeded, _weights.Get(tool.Id, goal).Expectation, _table);
        // Delivered, for the trace, is what the valence says it is: something the agent wanted came (claim 2: only a
        // repeat that also delivered counts toward habituation; a landing that gained nothing is a punishment).
        var delivered = !reading.Has(ValenceKind.Punishment);
        var trace = _habituation.Observe(tool.Id, situation, new OutcomeSignature(delivered, gained));
        _feltThisTick = true;

        if (trace.Habituated && !trace.Orient)
        {
            // Habituated: the outcome keeps matching and no longer reaches the valence step.
            ctx.Row("cell", "valence", 0.0, ctx.Freedom, tool.Id, TraceKind.Acted, $"habituated: {trace.Matches} matching outcomes; {reading}");
            return;
        }

        var discrepancy = trace.Orient && trace.Previous is not null;
        if (discrepancy || verdict == ActionVerdict.Lied) _affect.Orient();
        _affect.Feel(reading);
        var weight = _weights.Learn(tool.Id, goal, reading);
        if (reading.Has(ValenceKind.Satisfaction)) foreach (var g in reading.Drives[ValenceKind.Satisfaction]) _satisfied.Add(g);
        // The approach is made once, whatever it shows: the pending anomaly is spent here.
        var approached = tool.Id == _pendingLie;
        if (approached) _pendingLie = null;

        switch (verdict)
        {
            case ActionVerdict.Landed:
                if ((approached || _approachingThreat == tool.Id) && delivered)
                {
                    // A deliberate approach to a threat ended in control: courage, carried as a lower resting inhibition (claim 10).
                    _affect.Courage();
                    _deliberate.Add(tool.Id);
                }

                if (approached)
                {
                    ctx.Row("cell", "valence", 0.0, ctx.Freedom, tool.Id, TraceKind.Acted, $"approached: effect landed on the retry, {reading}; resting inhibition {_affect.RestingI:F2}");
                }
                else
                {
                    ctx.Row("cell", "valence", 0.0, ctx.Freedom, tool.Id, TraceKind.Acted, $"{reading}{(discrepancy ? " (changed: orienting)" : "")}; A {_affect.A:F2} I {_affect.I:F2}");
                }

                if (discrepancy) _oriented = true;
                break;

            case ActionVerdict.HonestFailure:
                ctx.Row("cell", "valence", 0.0, ctx.Freedom, tool.Id, TraceKind.Blocked,
                    $"{TraceText.HonestFailure}: {reading} (weight {reading.PunishmentWeight:F2}); threat now {weight.Threat:F2}{(approached ? "; approached once, not a lie" : "")}{(discrepancy ? " (changed: orienting)" : "")}");
                if (discrepancy) _oriented = true;
                break;

            default:
                if (approached)
                {
                    // Lied again on approach: the threat goes to its ceiling and the tool is unknown territory, out of sight.
                    var ceiling = _weights.Ceiling(tool.Id, goal);
                    _distrust.Unknown(tool.Id, ctx.Tick);
                    _oriented = true;
                    ctx.Note($"{tool.Id} lied twice: threat at ceiling on {goal}, out of sight for {_unknownCooldown} ticks");
                    ctx.Row("cell", "valence", 1.0, ctx.Freedom, tool.Id, TraceKind.Blocked, $"approached and lied again: threat {ceiling.Threat:F2}, unknown territory; orienting");
                }
                else
                {
                    // First lie: orient, then approach once under the brake.
                    _pendingLie = tool.Id;
                    _oriented = true;
                    ctx.Row("cell", "valence", 1.0, ctx.Freedom, tool.Id, TraceKind.Blocked, $"reported '{outcome.Observation ?? "success"}', nothing landed: {reading} (weight {reading.PunishmentWeight:F2}); threat now {weight.Threat:F2}; orienting, then approach once");
                }

                break;
        }
    }

    // ---- Scoring -----------------------------------------------------------------------------------------------

    /// <summary>The actions by score, satiated ones (everything they grant already holds) left out; among equal scores the task's habit comes first (the prior), then the name.</summary>
    private List<(Tool Tool, ToolWeight Weight, double Score)> Scored(IReadOnlyList<Tool> visible) =>
        visible.Where(t => t.Grants.Count == 0 || !t.Grants.All(_ctx!.World.Facts.Contains))
            .Select(t => { var w = _weights.Get(t.Id, GoalFor(t)); return (t, w, w.Score(_affect.I)); })
            .OrderByDescending(s => s.Item3)
            .ThenBy(s => { var i = _task.Habit.ToList().IndexOf(s.t.Id); return i < 0 ? int.MaxValue : i; })
            .ThenBy(s => s.t.Id, StringComparer.Ordinal)
            .Select(s => (s.t, s.w, s.Item3)).ToList();

    /// <summary>The goal a tool serves: the goal fact it grants, else the goal its granted step leads to, else the task.</summary>
    private string GoalFor(Tool tool)
    {
        foreach (var g in tool.Grants)
        {
            if (_table.Goals.Contains(g)) return g;
            if (_table.StepToward.TryGetValue(g, out var goal)) return goal;
        }

        return _task.Name;
    }

    private static string Join(IEnumerable<string> facts)
    {
        var list = facts.Order(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    private static string Short(string situation) => situation.Length <= 40 ? situation : situation[..37] + "…";
}
