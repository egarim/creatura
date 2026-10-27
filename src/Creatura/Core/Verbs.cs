namespace Creatura;

/// <summary>
/// What the control layer answers when a tool is admitted or refused before it runs: the Gatekeeper's verb.
/// <see cref="Feedback"/> is what the seat reads; <see cref="Terminate"/> is whether the round ends here.
/// </summary>
public sealed record Admission(Tool? Tool, RefusalReason? Refusal, GateDecision Gate, bool Terminate, string? Feedback)
{
    public bool Admitted => Refusal is null;

    public static Admission Admit(Tool tool, GateDecision gate) => new(tool, null, gate, false, null);

    public static Admission Refuse(Tool? tool, RefusalReason reason, bool terminate, string feedback) => new(tool, reason, GateDecision.Open, terminate, feedback);
}

/// <summary>What a host hands back after a tool ran: the text the tool reported and, when the host saw it, the outcome itself.</summary>
public sealed record ActionReport(string Reported, ActionOutcome? Outcome)
{
    /// <summary>What the tool claimed. Null when the host did not see the outcome: the layer then takes the report as a claim of success.</summary>
    public bool? ReportedSuccess => Outcome?.Succeeded;

    /// <summary>The report on one line, cut at 160 characters: what the rows and notes quote.</summary>
    public string Summary => Reported.Length > 160 ? Reported[..160].ReplaceLineEndings(" ") + "…" : Reported.ReplaceLineEndings(" ");

    /// <summary>A report built from the world's own outcome, as the in-process loop sees it.</summary>
    public static ActionReport Of(ActionOutcome outcome) => new(outcome.Observation ?? "", outcome);
}

/// <summary>The EffectCheck's verb: the verdict on one action, read from the registry, with what the seat is told.</summary>
public sealed record EffectJudgement(ActionVerdict Verdict, bool Landed, IReadOnlyList<string> Missing, bool Terminate, string Feedback);

/// <summary>
/// One judged action as <see cref="ControlLayer.ActionObserved"/> reports it: the tool, what the world answered
/// (a claim of success when the host saw no outcome), the verdict after the re-judge chain, whether the promised
/// effect is in the registry, and the layer's tick (the loop's tick, or the host's model call) when it was judged.
/// </summary>
public sealed record ActionObservation(int Tick, Tool Tool, ActionOutcome Outcome, ActionVerdict Verdict, bool Landed);

/// <summary>Why a loop or a round stopped before the goal, or because of it.</summary>
public enum StopCause
{
    GoalMet,
    StuckRegulation,
    Escalation,
    BudgetExceeded,
    NoPath,
    ModelError,
}

/// <summary>A stop the layer or the regulator asked for, with the hand-off behind it when there is one.</summary>
public sealed record StopReason(StopCause Cause, string Detail, HandOff? HandOff = null);

/// <summary>What <c>Close</c> returns: whether the contract is met, and the stop the layer asked for, if any.</summary>
public sealed record Closure(bool GoalReached, StopReason? Stop)
{
    /// <summary>True when the loop may open another round.</summary>
    public bool Continue => !GoalReached && Stop is null;
}

/// <summary>What a regulator decides before the seat is asked: ask the seat, run a fixed tool, or skip the tick.</summary>
public enum PreDecisionKind
{
    Ask,
    Fixed,
    Skip,
}

/// <summary>The regulator's before-decide answer. <see cref="Ask"/> is the default; a fixed step carries its own rationale.</summary>
public sealed record PreDecision(PreDecisionKind Kind, string? ToolId = null, string? Rationale = null)
{
    public static readonly PreDecision Ask = new(PreDecisionKind.Ask);

    public static PreDecision Fixed(string toolId, string rationale) => new(PreDecisionKind.Fixed, toolId, rationale);

    public static PreDecision Skip(string rationale) => new(PreDecisionKind.Skip, null, rationale);
}
