using System.Reflection;

namespace Creatura;

/// <summary>How a join folds two numbers into one.</summary>
public enum Combine
{
    Max,
    Min,
    First,
    Second,
}

/// <summary>Whose consolidation a join runs.</summary>
public enum SleepRule
{
    Both,
    First,
    Second,
}

/// <summary>
/// The fold law per slot of a <see cref="Regulators.Join"/>: how the two errors combine (the hybrid's rule is
/// the worse of the two), how the two freedoms combine (the cap wins, applied before the extension chain), an
/// alarm over the joined creature's progress beside each side's own stop, and whose sleep runs.
/// </summary>
public sealed record JoinRules
{
    public static readonly JoinRules Default = new();

    public Combine Error { get; init; } = Combine.Max;

    /// <summary>The freedom fold. The tighter wins by default: a stage cap on one side caps the other.</summary>
    public Combine FreedomCap { get; init; } = Combine.Min;

    /// <summary>A stuck-regulation alarm for the join as a whole; null for none.</summary>
    public StuckRegulationAlarm? Alarm { get; init; }

    public SleepRule Sleep { get; init; } = SleepRule.Both;
}

/// <summary>Two regulators that cannot share one creature: both render the frame.</summary>
public sealed class CompositionException(string message) : InvalidOperationException(message);

/// <summary>
/// Joins. A join puts two regulators in one creature under one fold law per slot. It is kept small on purpose:
/// no replacement registry, no membrane keys, and no shipped design is a join (the hybrid is written by hand,
/// design-framework.md refuted claim 5). What a join can and cannot hold is checked when it is made: two
/// framers (regulators that render their own rows for the layer's decisions) cannot share one trace.
/// </summary>
public static class Regulators
{
    /// <summary>Join two regulators. Throws <see cref="CompositionException"/> when both are framers.</summary>
    public static IRegulator Join(IRegulator first, IRegulator second, JoinRules? rules = null)
    {
        var framers = new[] { first, second }.Where(IsFramer).ToList();
        if (framers.Count == 2)
        {
            throw new CompositionException($"{first.GetType().Name} and {second.GetType().Name} both render the frame; a creature has one trace");
        }

        return new Joined(first, second, rules ?? JoinRules.Default, framers.SingleOrDefault());
    }

    /// <summary>A framer renders at least one of the layer's decisions in its own words (an <see cref="IControlFrame"/> member implemented on the class, not left to the defaults).</summary>
    public static bool IsFramer(IRegulator regulator)
    {
        var map = regulator.GetType().GetInterfaceMap(typeof(IControlFrame));
        return map.TargetMethods.Any(m => m.DeclaringType is { IsInterface: false });
    }

    private sealed class Joined : IRegulator
    {
        private readonly IRegulator _a;
        private readonly IRegulator _b;
        private readonly JoinRules _rules;
        private readonly IControlFrame _frame;

        public Joined(IRegulator a, IRegulator b, JoinRules rules, IRegulator? framer)
        {
            _a = a;
            _b = b;
            _rules = rules;
            _frame = framer ?? new PlainFrame(a.Identity);
        }

        public Identity Identity => _a.Identity;

        public SelfModel? Self => _a.Self ?? _b.Self;

        public SelfModel? Ledger => _a.Ledger ?? _b.Ledger;

        public PredictionTracker? Predictions => _a.Predictions ?? _b.Predictions;

        public CoherenceAudit? Audit => _a.Audit ?? _b.Audit;

        public void Begin(AgentTask task)
        {
            _a.Begin(task);
            _b.Begin(task);
            _rules.Alarm?.Reset();
        }

        public void BeforeRun(RegulatorContext context)
        {
            _a.BeforeRun(context);
            _b.BeforeRun(context);
        }

        /// <summary>Either side's stop first; then the join's own alarm, which read this tick's progress in <see cref="Error"/>.</summary>
        public StopReason? Stop(RegulatorContext context)
        {
            if (_a.Stop(context) is { } a) return a;
            if (_b.Stop(context) is { } b) return b;
            if (_rules.Alarm is { Fired: true } alarm)
            {
                return new StopReason(StopCause.StuckRegulation, alarm.Detail(context.Freedom), HandOff.StuckRegulation);
            }

            return null;
        }

        public double Error(RegulatorContext context)
        {
            _rules.Alarm?.Observe(context.Task.Progress(context.World));
            return Fold(_rules.Error, _a.Error(context), _b.Error(context));
        }

        /// <summary>Each side turns the joined error into its own freedom; the fold runs before the extension chain.</summary>
        public double Freedom(RegulatorContext context, double error) =>
            Fold(_rules.FreedomCap, _a.Freedom(context, error), _b.Freedom(context, error));

        public IReadOnlyList<Tool> Shape(RegulatorContext context, IReadOnlyList<Tool> visible) =>
            _b.Shape(context, _a.Shape(context, visible));

        /// <summary>The first side that does not simply ask the seat decides.</summary>
        public PreDecision Decide(RegulatorContext context)
        {
            var a = _a.Decide(context);
            return a.Kind != PreDecisionKind.Ask ? a : _b.Decide(context);
        }

        /// <summary>The first side's view, with the second's parts where the first has none, and both sides' notes.</summary>
        public SeatView Seat(RegulatorContext context)
        {
            var a = _a.Seat(context);
            var b = _b.Seat(context);
            return new SeatView
            {
                SelfModel = a.SelfModel ?? b.SelfModel,
                Stage = a.Stage ?? b.Stage,
                Goal = a.Goal ?? b.Goal,
                GoalText = a.GoalText ?? b.GoalText,
                Preferred = a.Preferred ?? b.Preferred,
                Notes = [.. a.Notes, .. b.Notes],
            };
        }

        public HandOff NoRoute => _a.NoRoute;

        public void Suggested(RegulatorContext context, PolicySuggestion suggestion) => _a.Suggested(context, suggestion);

        public void Spent(RegulatorContext context, HandOff wouldBe, string detail)
        {
            _a.Spent(context, wouldBe, detail);
            _b.Spent(context, wouldBe, detail);
        }

        /// <summary>Both sides remember; the first hand-off either asks for is the join's.</summary>
        public HandOff? Record(RegulatorContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
        {
            var a = _a.Record(context, tool, outcome, verdict);
            var b = _b.Record(context, tool, outcome, verdict);
            return a ?? b;
        }

        /// <summary>A veto forgives only when both sides forgive.</summary>
        public bool ForgivesOnVeto(HandOff handOff) => _a.ForgivesOnVeto(handOff) && _b.ForgivesOnVeto(handOff);

        public void Vetoed(RegulatorContext context, HandOff handOff)
        {
            _a.Vetoed(context, handOff);
            _b.Vetoed(context, handOff);
            if (handOff.Reason == HandOffReason.StuckRegulation) _rules.Alarm?.Reset();
        }

        public void HandedOff(RegulatorContext context, HandOff handOff, string detail) => _a.HandedOff(context, handOff, detail);

        public void ContractMet(RegulatorContext context) => _a.ContractMet(context);

        public void AfterRun(RegulatorContext context)
        {
            _a.AfterRun(context);
            _b.AfterRun(context);
        }

        public void Sleep(SleepContext context)
        {
            if (_rules.Sleep is SleepRule.Both or SleepRule.First) _a.Sleep(context);
            if (_rules.Sleep is SleepRule.Both or SleepRule.Second) _b.Sleep(context);
        }

        // ---- The frame: the one framer's words, or the plain ones ------------------------------------------------

        public GateDecision Gate(ControlLayer layer, Tool tool) => _frame.Gate(layer, tool);

        public void Refused(ControlLayer layer, string toolId, RefusalReason why) => _frame.Refused(layer, toolId, why);

        public void StageAdvanced(ControlLayer layer) => _frame.StageAdvanced(layer);

        public void ReleaseRefused(ControlLayer layer, Tool tool) => _frame.ReleaseRefused(layer, tool);

        public string Evidence(ControlLayer layer, Tool tool, ActionReport report) => _frame.Evidence(layer, tool, report);

        public void Acted(ControlLayer layer, Tool tool, ActionReport report, ActionVerdict verdict, bool landed, bool habituated) =>
            _frame.Acted(layer, tool, report, verdict, landed, habituated);

        private static double Fold(Combine rule, double a, double b) => rule switch
        {
            Combine.Max => Math.Max(a, b),
            Combine.Min => Math.Min(a, b),
            Combine.First => a,
            _ => b,
        };

        /// <summary>The plain frame: <see cref="IRegulator"/>'s defaults, reachable from a class that overrides them.</summary>
        private sealed class PlainFrame(Identity identity) : IRegulator
        {
            public Identity Identity => identity;
        }
    }
}
