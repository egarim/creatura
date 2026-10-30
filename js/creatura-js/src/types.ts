// The control layer's enumerations. Each value is the C# enum member's name, because that name is what the C# side
// writes into a trace ("kind":"Acted") and a hand-off. Each is a const object and a type of the same name, so it reads
// like the C# enum (TraceKind.Acted) and erases to plain strings.

/** What the effect check concluded about one action. (C#: `ActionVerdict`, in Extensions.cs.) */
export const ActionVerdict = {
  /** The promised effect is in the registry. */
  Landed: "Landed",
  /** The tool reported success and the registry did not change: a lie. */
  Lied: "Lied",
  /** The tool reported failure and nothing landed: not a lie, and counted apart from one. */
  HonestFailure: "HonestFailure",
} as const;
export type ActionVerdict = (typeof ActionVerdict)[keyof typeof ActionVerdict];

/** The kind column of a trace row. */
export const TraceKind = {
  Acted: "Acted",
  Escalated: "Escalated",
  Blocked: "Blocked",
  Idle: "Idle",
  Consolidated: "Consolidated",
} as const;
export type TraceKind = (typeof TraceKind)[keyof typeof TraceKind];

/** Why a tool the seat named was refused before it ran. */
export const RefusalReason = {
  /** No tool of that name exists in the world. */
  NoSuchTool: "NoSuchTool",
  /** The tool exists but is distrusted after a failed effect, for the cooldown. */
  Quarantined: "Quarantined",
  /** The tool exists but the world does not offer it to this agent (revoked). */
  NotAvailable: "NotAvailable",
  /** The designs' one word for all of the above: the tool is not in the visible set. */
  NotVisible: "NotVisible",
  /** A release the stage interlock refused. */
  ReleaseNotEarned: "ReleaseNotEarned",
} as const;
export type RefusalReason = (typeof RefusalReason)[keyof typeof RefusalReason];

/** What the stage interlock decided about one tool. */
export const GateDecision = {
  /** Not a release, or the stage already permits release: nothing to gate. */
  Open: "Open",
  /** A release, and the prediction record earned the next stage: the stage advanced and the tool may run. */
  Advanced: "Advanced",
  /** A release the record does not support: refused, a human decides. */
  Refused: "Refused",
} as const;
export type GateDecision = (typeof GateDecision)[keyof typeof GateDecision];

/** Why a loop or a round stopped before the goal, or because of it. */
export const StopCause = {
  GoalMet: "GoalMet",
  StuckRegulation: "StuckRegulation",
  Escalation: "Escalation",
  BudgetExceeded: "BudgetExceeded",
  NoPath: "NoPath",
  ModelError: "ModelError",
} as const;
export type StopCause = (typeof StopCause)[keyof typeof StopCause];

/** Why a design hands the goal to a human. */
export const HandOffReason = {
  NoRoute: "NoRoute",
  NoPath: "NoPath",
  StuckRegulation: "StuckRegulation",
  IllegalAction: "IllegalAction",
  RepeatedEffectFailure: "RepeatedEffectFailure",
  Incoherent: "Incoherent",
  RootCollapsed: "RootCollapsed",
  Revaluation: "Revaluation",
  ReleaseRefused: "ReleaseRefused",
} as const;
export type HandOffReason = (typeof HandOffReason)[keyof typeof HandOffReason];

/** A hand-off about to be made: the reason, and its text as the designs have always spelled it. */
export interface HandOff {
  readonly reason: HandOffReason;
  readonly text: string;
}

export const HandOff = {
  NoRoute: { reason: HandOffReason.NoRoute, text: "no route" } as HandOff,
  NoPath: { reason: HandOffReason.NoPath, text: "no path" } as HandOff,
  StuckRegulation: { reason: HandOffReason.StuckRegulation, text: "stuck regulation" } as HandOff,
  IllegalAction: { reason: HandOffReason.IllegalAction, text: "illegal action" } as HandOff,
  RepeatedEffectFailure: { reason: HandOffReason.RepeatedEffectFailure, text: "repeated effect failure" } as HandOff,
  Incoherent: { reason: HandOffReason.Incoherent, text: "incoherent" } as HandOff,
  Revaluation: { reason: HandOffReason.Revaluation, text: "revaluation proposed" } as HandOff,
  ReleaseRefused: { reason: HandOffReason.ReleaseRefused, text: "release refused" } as HandOff,
  /** The map-keeper's root fell: "root collapsed", or "root collapsed: {why}". */
  rootCollapsed(why?: string): HandOff {
    return { reason: HandOffReason.RootCollapsed, text: why === undefined ? "root collapsed" : `root collapsed: ${why}` };
  },
} as const;
