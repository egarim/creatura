import { clamp, roundHalfEven } from "./format.js";

/**
 * A Kegan-derived stage, used as a safety interlock rather than a score: the level only caps what an agent is allowed
 * to do on its own.
 */
export interface Stage {
  readonly level: number;
  readonly name: string;
  readonly freedomCap: number;
  readonly mayDeploy: boolean;
  readonly mayEditSelfModel: boolean;
}

export const Stages = {
  all: [
    { level: 1, name: "reactive survival", freedomCap: 0.35, mayDeploy: false, mayEditSelfModel: false },
    { level: 2, name: "personal self", freedomCap: 0.5, mayDeploy: false, mayEditSelfModel: false },
    { level: 3, name: "social self", freedomCap: 0.65, mayDeploy: false, mayEditSelfModel: false },
    { level: 4, name: "rational agency", freedomCap: 0.8, mayDeploy: true, mayEditSelfModel: false },
    { level: 5, name: "self-authoring", freedomCap: 1.0, mayDeploy: true, mayEditSelfModel: true },
  ] as readonly Stage[],

  at(level: number): Stage {
    return Stages.all[clamp(level, 1, Stages.all.length) - 1]!;
  },

  /** The ceiling on freedom: plasticity may ask for more, the stage refuses. */
  cap(level: number): number {
    return Stages.at(level).freedomCap;
  },
} as const;

export const Plasticity = {
  /** Error buys freedom, success freezes it; even a perfectly calm agent keeps a little room to improvise. */
  freedom(stress: number, floor = 0.2, gain = 0.8): number {
    return clamp(floor + gain * stress, 0.0, 1.0);
  },
} as const;

/** What error buys: the retry budget. */
export const Budget = {
  /** 1 to 4 attempts: `clamp(1 + round(freedom * 3), 1, 4)`, with C#'s rounding (ties to even). */
  attempts(freedom: number): number {
    return clamp(1 + roundHalfEven(freedom * 3), 1, 4);
  },
} as const;
