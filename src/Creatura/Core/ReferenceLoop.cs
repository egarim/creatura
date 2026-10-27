namespace Creatura;

/// <summary>
/// The one in-process loop. Per tick: begin → the regulator's stop check → offer (quarantine, regulator shaping,
/// extensions) → error and freedom (regulator, then the extension chain) → budget → the regulator's pre-decision
/// → assemble and ask the seat → the decision row → no-route handling → admit → gate → act → judge → the
/// regulator's record → verdict stop → goal check. Every hand-off goes through the extensions' veto. After the
/// loop: the regulator's notes, sleep, and the run. The control layer does the mechanics; the regulator fills
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

    /// <param name="regulator">The design in the slots.</param>
    /// <param name="policy">The seat; the planner when null.</param>
    /// <param name="task">The toy service-restore task when null.</param>
    /// <param name="options">The extensions; none when null.</param>
    /// <param name="layer">The layer's switches; the designs' (<see cref="ControlLayerOptions.Design"/>) when null.</param>
    /// <param name="name">The run's agent name; the regulator's type name when null.</param>
    public ReferenceLoop(IRegulator regulator, IPolicy? policy = null, AgentTask? task = null, AgentOptions? options = null, ControlLayerOptions? layer = null, string? name = null)
    {
        _regulator = regulator;
        _policy = policy ?? new PlannerPolicy();
        _task = task ?? RestoreServiceDomain.Task;
        _options = options ?? AgentOptions.None;
        _ext = new ExtensionSet(_options);
        _layerOptions = layer ?? ControlLayerOptions.Design;
        Name = name ?? regulator.GetType().Name;
    }

    public string Name { get; }

    public IRegulator Regulator => _regulator;

    public async Task<AgentRun> RunAsync(IWorld world, int maxTicks = 40, CancellationToken cancellationToken = default)
    {
        var trace = new List<TraceEvent>();
        var notes = new List<string>();
        var history = new List<string>();

        _regulator.Begin(_task);
        var self = _regulator.Self ?? _regulator.Identity.NewSelfModel();
        var layer = new ControlLayer(world, _task, self, _layerOptions, _regulator, _regulator.Predictions, _regulator.Audit, trace, notes, _regulator.Ledger);
        var ctx = new RegulatorContext
        {
            Agent = Name, World = world, Task = _task, PolicyName = _policy.Name, Options = _options, MaxTicks = maxTicks,
            Trace = trace, Notes = notes, Layer = layer,
        };
        _regulator.BeforeRun(ctx);

        var goalReached = false;
        var lastTick = 0;

        for (var tick = 1; tick <= maxTicks; tick++)
        {
            lastTick = tick;
            layer.BeginTick();
            ctx.Tick = tick;
            var tctx = new TickContext
            {
                Agent = Name, World = world, Task = _task, Tick = tick, MaxTicks = maxTicks, Notes = notes, Trace = trace,
                SelfModel = _regulator.Self, Predictions = _regulator.Predictions,
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
                    Facts = world.Facts,
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
                if (suggestion.Tool is null && _ext.NoRoute(tctx) is { Tool: not null } alternative) suggestion = alternative;
            }

            _regulator.Suggested(ctx, suggestion);

            if (suggestion.Tool is null)
            {
                // No path *right now* is not the same as no path: a quarantined oracle comes back after its
                // cooldown, so the agent waits it out as long as its budget allows.
                layer.Spend();
                if (HandsOff(layer, ctx, tctx, _regulator.NoRoute, suggestion.Rationale)) break;
                if (_layerOptions.ClearQuarantineOnNoRoute) layer.Quarantine.Clear();
                continue;
            }

            var admission = layer.Admit(suggestion.Tool);
            if (!admission.Admitted)
            {
                // The stage refusal is the one stop no extension vetoes (until step 9 names that change).
                if (admission.Refusal == RefusalReason.ReleaseNotEarned) break;
                history.Add($"{suggestion.Tool} → refused: not visible");
                if (HandsOff(layer, ctx, tctx, HandOff.IllegalAction, "illegal action")) break;
                continue;
            }

            var tool = admission.Tool!;
            var outcome = world.Act(tool.Id, suggestion.Content);
            var landed = world.EffectsLanded(tool);
            var judgement = layer.Judge(tool, ActionReport.Of(outcome),
                v => _ext.Judge(tctx, tool, outcome, landed, v),
                v => _ext.AfterAction(tctx, tool, outcome, v));
            var observed = string.IsNullOrWhiteSpace(outcome.Observation) ? "" : $"\n{outcome.Observation}";
            history.Add(judgement.Verdict == ActionVerdict.HonestFailure
                ? $"{tool.Id} → reported failure{observed}"
                : $"{tool.Id} → {(landed ? "effect landed" : "no effect")}{observed}");

            if (judgement.Verdict == ActionVerdict.HonestFailure && _layerOptions.HonestFailureRule == HonestFailureRule.Habituate) continue;

            if (_regulator.Record(ctx, tool, outcome, judgement.Verdict) is { } handOff)
            {
                if (HandsOff(layer, ctx, tctx, handOff, handOff.Text)) break;
                continue;
            }

            if (_task.Goal(world.Facts))
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
            SelfModel = _regulator.Self, Audit = _regulator.Audit, Predictions = _regulator.Predictions,
        };
        _ext.Sleep(sleep, () => _regulator.Sleep(sleep));
        return new AgentRun(Name, goalReached, lastTick, trace, notes);
    }

    /// <summary>
    /// The budget check behind every hand-off: within budget the attempt is spent (and the regulator may note it);
    /// past it the extensions may veto, which forgives the attempts when the regulator says so; otherwise the
    /// regulator renders the hand-off and the loop stops. True when the loop stops.
    /// </summary>
    private bool HandsOff(ControlLayer layer, RegulatorContext ctx, TickContext tctx, HandOff handOff, string detail)
    {
        if (layer.Failures <= layer.Budget)
        {
            _regulator.Spent(ctx, handOff, detail);
            return false;
        }

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
