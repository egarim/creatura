import { replaceLineEndings } from "./format.js";
import { GateDecision, type ActionVerdict, type HandOff, type RefusalReason, type StopCause } from "./types.js";
import type { ActionOutcome, Tool } from "./world.js";

/**
 * What the control layer answers when a tool is admitted or refused before it runs: the Gatekeeper's verb.
 * `feedback` is what the seat reads; `terminate` is whether the round ends here.
 */
export interface Admission {
  readonly tool: Tool | undefined;
  readonly refusal: RefusalReason | undefined;
  readonly gate: GateDecision;
  readonly terminate: boolean;
  readonly feedback: string | undefined;
  /** True when nothing refused the tool. */
  readonly admitted: boolean;
}

export const Admission = {
  admit(tool: Tool, gate: GateDecision): Admission {
    return { tool, refusal: undefined, gate, terminate: false, feedback: undefined, admitted: true };
  },
  refuse(tool: Tool | undefined, reason: RefusalReason, terminate: boolean, feedback: string): Admission {
    return { tool, refusal: reason, gate: GateDecision.Open, terminate, feedback, admitted: false };
  },
} as const;

/** What a host hands back after a tool ran: the text the tool reported and, when the host saw it, the outcome itself. */
export class ActionReport {
  readonly reported: string;
  readonly outcome: ActionOutcome | undefined;

  constructor(reported: string, outcome?: ActionOutcome) {
    this.reported = reported;
    this.outcome = outcome;
  }

  /** What the tool claimed. Undefined when the host did not see the outcome: the layer then takes it as a claim of success. */
  get reportedSuccess(): boolean | undefined {
    return this.outcome?.succeeded;
  }

  /** The report on one line, cut at 160 characters: what the rows and notes quote. */
  get summary(): string {
    return this.reported.length > 160
      ? replaceLineEndings(this.reported.slice(0, 160), " ") + "…"
      : replaceLineEndings(this.reported, " ");
  }

  /** A report built from the world's own outcome, as the in-process loop sees it. */
  static of(outcome: ActionOutcome): ActionReport {
    return new ActionReport(outcome.observation ?? "", outcome);
  }
}

/** The EffectCheck's verb: the verdict on one action, read from the registry, with what the seat is told. */
export interface EffectJudgement {
  readonly verdict: ActionVerdict;
  readonly landed: boolean;
  readonly missing: readonly string[];
  readonly terminate: boolean;
  readonly feedback: string;
}

/** One judged action, as the layer's action observers see it. */
export interface ActionObservation {
  readonly tick: number;
  readonly tool: Tool;
  readonly outcome: ActionOutcome;
  readonly verdict: ActionVerdict;
  readonly landed: boolean;
}

/** A stop the layer asked for, with the hand-off behind it when there is one. */
export interface StopReason {
  readonly cause: StopCause;
  readonly detail: string;
  readonly handOff?: HandOff | undefined;
}

/** What `close` returns: whether the contract is met, and the stop the layer asked for, if any. */
export interface Closure {
  readonly goalReached: boolean;
  readonly stop: StopReason | undefined;
  /** True when the loop may open another round. */
  readonly continue: boolean;
}

export function closure(goalReached: boolean, stop: StopReason | undefined): Closure {
  return { goalReached, stop, continue: !goalReached && stop === undefined };
}
