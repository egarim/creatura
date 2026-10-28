namespace Creatura;

/// <summary>
/// The one in-process loop. Per tick: begin → the regulator's stop check → offer (quarantine, regulator shaping,
/// extensions) → error and freedom (regulator, then the extension chain) → budget → the regulator's pre-decision
/// → assemble and ask the seat → the decision row → no-route handling → admit → gate → act → judge → the
/// regulator's record → verdict stop → goal check. Every hand-off goes through the extensions' veto, the stage
/// refusal's included (the veto cancels the hand-off, never the refusal). After the loop: the regulator's notes,
/// sleep, and the run. The control layer does the mechanics; the regulator fills
/// the slots and writes the words; nothing design-specific lives here.
/// </summary>
public sealed class ReferenceLoop : IAgent
{
    private readonly IRegulator _regulator;
    private readonly IPolicy _policy;
    private readonly AgentTask _task;
    private readonly AgentOptions _options;
    private readonly ExtensionSet _ext;
    private readonly ControlLayerOptions _layerOptions;
    private readonly IRecordStore? _store;
    private readonly string _recordKey;

    /// <param name="regulator">The design in the slots.</param>
    /// <param name="policy">The seat; the planner when null.</param>
    /// <param name="task">The toy service-restore task when null.</param>
    /// <param name="options">The extensions; none when null.</param>
    /// <param name="layer">The layer's switches; the designs' (<see cref="ControlLayerOptions.Design"/>) when null.</param>
    /// <param name="name">The run's agent name; the regulator's type name when null.</param>
    /// <param name="store">Where the record is loaded from before the run and saved to after sleep; none when null (the run still leaves its record on <see cref="AgentRun.Record"/>).</param>
    /// <param name="recordKey">The record's key in the store; the agent name when null.</param>
    public ReferenceLoop(IRegulator regulator, IPolicy? policy = null, AgentTask? task = null, AgentOptions? options = null, ControlLayerOptions? layer = null, string? name = null, IRecordStore? store = null, string? recordKey = null)
    {
        _regulator = regulator;
        _policy = policy ?? new PlannerPolicy();
        _task = task ?? RestoreServiceDomain.Task;
        _options = options ?? AgentOptions.None;
        _ext = new ExtensionSet(_options);
        _layerOptions = layer ?? ControlLayerOptions.Design;
        Name = name ?? regulator.GetType().Name;
        _store = store;
        _recordKey = recordKey ?? Name;
    }

    public string Name { get; }

    public IRegulator Regulator => _regulator;

    /// <summary>The record's key in the store: the one given, else the agent name.</summary>
    public string RecordKey => _recordKey;

    public async Task<AgentRun> RunAsync(IWorld world, int maxTicks = 40, CancellationToken cancellationToken = default)
    {
        var trace = new List<TraceEvent>();
        var notes = new List<string>();
        var history = new List<string>();

        var counters = new RunCounters();

        _regulator.Begin(_task);
        // The record: loaded before the layer is built, so a regulator restores its parts into the instances the layer will hold.
        var record = _store?.Load(_recordKey) ?? new AgentRecord { Key = _recordKey };
        var casesBefore = record.Cases;
        _regulator.Load(record);
        var self = _regulator.Self ?? _regulator.Identity.NewSelfModel();
        var layer = new ControlLayer(world, _task, self, _layerOptions, _regulator, _regulator.Predictions, _regulator.Audit, trace, notes, _regulator.Ledger, _regulator.Distrust);
        var ctx = new RegulatorContext
        {
            Agent = Name, World = world, Task = _task, PolicyName = _policy.Name, Options = _options, MaxTicks = maxTicks,
            Trace = trace, Notes = notes, Layer = layer, History = history,
        };
        _regulator.BeforeRun(ctx);
        ctx.Extensions = new TickContext
        {
            Agent = Name, World = world, Task = _task, Tick = 0, MaxTicks = maxTicks, Notes = notes, Trace = trace,
            SelfModel = _regulator.Self, Predictions = _regulator.Predictions, Counters = counters,
        };
        _ext.BeforeRun(ctx.Extensions);

        var goalReached = false;
        var lastTick = 0;

        for (var tick = 1; tick <= maxTicks; tick++)
        {
            lastTick = tick;
            (world as ITickedWorld)?.BeginTick(tick);
            layer.BeginTick();
            ctx.Tick = tick;
            var tctx = new TickContext
            {
                Agent = Name, World = world, Task = _task, Tick = tick, MaxTicks = maxTicks, Notes = notes, Trace = trace,
                SelfModel = _regulator.Self, Predictions = _regulator.Predictions, Counters = counters,
            };
            ctx.Extensions = tctx;

            // Error and freedom first, as the designs compute them, then the option space.
            var error = _regulator.Error(ctx);
            ctx.Stress = error;
            var freedom = _ext.Freedom(tctx, _regulator.Freedom(ctx, error));
            var budget = Budget.Attempts(freedom);
            ctx.Freedom = freedom;
            ctx.Budget = budget;
            tctx.Freedom = freedom;
            layer.BeginRound(freedom, budget);

            // The regulator's own stop (an alarm, a story that closed); a hand-off inside it is vetoable, and one the
            // extensions let stand is rendered by the regulator like any other.
            if (_regulator.Stop(ctx) is { } stop)
            {
                if (stop.Cause == StopCause.GoalMet)
                {
                    goalReached = true;
                    break;
                }

                if (stop.HandOff is not { } alarm) break;
                if (_ext.SuppressHandOff(tctx, alarm))
                {
                    _regulator.Vetoed(ctx, alarm);
                }
                else
                {
                    _regulator.HandedOff(ctx, alarm, stop.Detail);
                    break;
                }
            }

            var visible = layer.Offer(v => _ext.VisibleTools(tctx, _regulator.Shape(ctx, v)));
            ctx.Visible = visible;
            tctx.Visible = visible;
            tctx.History = history.TakeLast(8).ToList();

            // The seat: the regulator may fix the step (an approach) or skip; otherwise the policy decides.
            PolicySuggestion suggestion;
            var pre = _regulator.Decide(ctx);
            if (pre.Kind == PreDecisionKind.Skip) continue;
            if (pre.Kind == PreDecisionKind.Fixed)
            {
                suggestion = new PolicySuggestion(pre.ToolId, pre.Rationale ?? "");
            }
            else
            {
                var seat = _regulator.Seat(ctx);
                var context = new PolicyContext
                {
                    Facts = seat.Facts ?? world.Facts,
                    Tools = visible,
                    Goal = seat.Goal ?? _task.Goal,
                    GoalText = seat.GoalText ?? _task.GoalText,
                    Tick = tick,
                    MaxTicks = maxTicks,
                    Freedom = freedom,
                    SelfModel = seat.SelfModel,
                    Stage = seat.Stage,
                    History = tctx.History,
                    Preferred = seat.Preferred ?? _task.Habit,
                    Notes = seat.Notes.Count == 0 ? _ext.PolicyNotes(tctx) : [.. seat.Notes, .. _ext.PolicyNotes(tctx)],
                };
                suggestion = await PolicyGuard.NextAsync(_policy, context, cancellationToken).ConfigureAwait(false);
                if (suggestion.Tool is null && _regulator.Unrouted(ctx) is { Tool: not null } own) suggestion = own;
                if (suggestion.Tool is null && _ext.NoRoute(tctx) is { } alternative) suggestion = alternative;
            }

            _regulator.Suggested(ctx, suggestion);

            if (suggestion.Tool is null)
            {
                // No path *right now* is not the same as no path: a quarantined oracle comes back after its
                // cooldown, so the agent waits it out as long as its budget allows.
                layer.Spend();
                if (HandsOff(layer, ctx, tctx, _regulator.NoRoute, suggestion.Rationale)) break;
                if (_layerOptions.ClearQuarantineOnNoRoute) layer.Distrust.Clear();
                continue;
            }

            var admission = layer.Admit(suggestion.Tool);
            if (!admission.Admitted)
            {
                // The stage refusal stands whatever happens next (the frame wrote it; the tool did not run); the hand-off
                // it amounts to is vetoable, and a veto keeps the run going past it.
                if (admission.Refusal == RefusalReason.ReleaseNotEarned)
                {
                    if (!_ext.SuppressHandOff(tctx, HandOff.ReleaseRefused)) break;
                    _regulator.Vetoed(ctx, HandOff.ReleaseRefused);
                    continue;
                }

                history.Add($"{suggestion.Tool} → refused: not visible");
                if (HandsOff(layer, ctx, tctx, HandOff.IllegalAction, "illegal action")) break;
                continue;
            }

            var tool = admission.Tool!;
            if (_regulator.Hold(ctx, tool) is { } hold)
            {
                // Held this tick (a sub-story opened); the hold may amount to a hand-off, which is vetoable and never budgeted.
                if (hold.HandOff is { } held && Escalates(layer, ctx, tctx, held, held.Text)) break;
                continue;
            }

            var outcome = world.Act(tool.Id, suggestion.Content);
            var landed = world.EffectsLanded(tool);
            var judgement = layer.Judge(tool, ActionReport.Of(outcome),
                v => _ext.Judge(tctx, tool, outcome, landed, v),
                v => _ext.AfterAction(tctx, tool, outcome, v));
            history.Add(_regulator.Remembered(tool, outcome, judgement.Verdict, landed));

            if (judgement.Verdict == ActionVerdict.HonestFailure && _layerOptions.HonestFailureRule == HonestFailureRule.Habituate) continue;

            if (_regulator.Record(ctx, tool, outcome, judgement.Verdict) is { } handOff)
            {
                if (HandsOff(layer, ctx, tctx, handOff, handOff.Text)) break;
                continue;
            }

            if (_regulator.ContractIsMet(ctx))
            {
                goalReached = true;
                _regulator.ContractMet(ctx);
                break;
            }
        }

        _regulator.AfterRun(ctx);
        var sleep = new SleepContext
        {
            Agent = Name, World = world, Task = _task, LastTick = lastTick, GoalReached = goalReached, Notes = notes, Trace = trace,
            SelfModel = _regulator.Self, Audit = _regulator.Audit, Predictions = _regulator.Predictions, Counters = counters,
        };
        _ext.Sleep(sleep, () => _regulator.Sleep(sleep));

        // The record after sleep. Read the store again first: a record extension sharing it has just saved its carry and its
        // parts, and this must build on that, not on what was loaded before the run. Then the Core's own state after sleep,
        // the regulator's parts, and the case count: one more than before the run, unless a sharer counted it already.
        record = _store?.Load(_recordKey) ?? record;
        record.Key = _recordKey;
        record.Cases = Math.Max(record.Cases, casesBefore + 1);
        if (_regulator.Self is { } model)
        {
            record.Skills = new Dictionary<string, SkillBelief>(model.Skills, StringComparer.Ordinal);
            record.RecentErrors = [.. model.RecentErrors];
            record.StageLevel = Math.Max(record.StageLevel, model.StageLevel);
        }

        if (_regulator.Predictions is { } predictions) record.Predictions = predictions.Snapshot();
        if (_regulator.Audit is { } audit) record.Coherence = audit.Snapshot();
        _regulator.Store(record);
        _store?.Save(_recordKey, record);
        return new AgentRun(Name, goalReached, lastTick, trace, notes) { Record = record, Proposals = _regulator.Proposals, Counters = counters };
    }

    /// <summary>
    /// The check behind a counted failure: the regulator says whether the attempts are exhausted (by default, the
    /// layer's budget; the map-keeper runs its collapse chain instead) and which hand-off that is; then the hand-off
    /// goes through <see cref="Escalates"/>. True when the loop stops.
    /// </summary>
    private bool HandsOff(ControlLayer layer, RegulatorContext ctx, TickContext tctx, HandOff wouldBe, string detail) =>
        _regulator.Exhausted(ctx, wouldBe, detail) is { } handOff && Escalates(layer, ctx, tctx, handOff, detail);

    /// <summary>
    /// A hand-off about to be made: the extensions may veto, which forgives the attempts when the regulator says
    /// so; otherwise the regulator renders the hand-off and the loop stops. True when the loop stops.
    /// </summary>
    private bool Escalates(ControlLayer layer, RegulatorContext ctx, TickContext tctx, HandOff handOff, string detail)
    {
        if (_ext.SuppressHandOff(tctx, handOff))
        {
            if (_regulator.ForgivesOnVeto(handOff)) layer.Forgive();
            _regulator.Vetoed(ctx, handOff);
            return false;
        }

        _regulator.HandedOff(ctx, handOff, detail);
        return true;
    }
}
