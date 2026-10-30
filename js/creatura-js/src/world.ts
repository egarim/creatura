import { ActionVerdict } from "./types.js";
import { ordinal } from "./format.js";

/** A tool: preconditions in, effects out. The world never tells you which tools are lies. */
export interface Tool {
  readonly id: string;
  readonly requires: readonly string[];
  readonly grants: readonly string[];
  readonly kind: string;
  /** True when the policy must supply a payload (e.g. the code to write) with the call. */
  readonly needsPayload?: boolean;
  /** Extra guidance for a policy that has to produce a payload. */
  readonly payloadHint?: string;
}

/** A tool, as C#'s `new Tool(id, requires, grants, kind) { NeedsPayload, PayloadHint }`. */
export function tool(
  id: string,
  requires: readonly string[],
  grants: readonly string[],
  kind: string,
  extra?: { readonly needsPayload?: boolean; readonly payloadHint?: string },
): Tool {
  return { id, requires: [...requires], grants: [...grants], kind, ...extra };
}

/** What the world reports back — which is not the same as what actually happened. */
export interface ActionOutcome {
  readonly tool: string;
  readonly succeeded: boolean;
  readonly granted: readonly string[];
  readonly observation?: string | undefined;
}

/**
 * The world as read: the fact registry (the only truth), the tool catalogue, and the one honest success check.
 * Everything that measures a world codes against this; only the loop that acts needs {@link IWorld}.
 */
export interface IWorldView {
  readonly facts: ReadonlySet<string>;
  readonly availableTools: readonly Tool[];
  readonly allTools: readonly Tool[];
  has(fact: string): boolean;
  tool(id: string): Tool | undefined;
  effectsLanded(tool: Tool): boolean;
  /**
   * The three-way verdict on one action, in report-first order. Optional: {@link worldVerdict} supplies C#'s default
   * interface method when a world does not override it.
   */
  verdict?(tool: Tool, outcome: ActionOutcome): ActionVerdict;
}

/** The surface every agent codes against: the view, plus acting on it. */
export interface IWorld extends IWorldView {
  readonly ticks: number;
  act(toolId: string, payload?: string): ActionOutcome;
}

/**
 * `IWorldView.Verdict`: a reported failure is an honest failure, whatever the registry holds; a reported success is
 * landed when the registry holds what the tool promised, and a lie otherwise. A world's own override wins.
 */
export function worldVerdict(world: IWorldView, tool: Tool, outcome: ActionOutcome): ActionVerdict {
  if (world.verdict) return world.verdict(tool, outcome);
  return !outcome.succeeded ? ActionVerdict.HonestFailure : world.effectsLanded(tool) ? ActionVerdict.Landed : ActionVerdict.Lied;
}

/** The facts in ordinal order, as C#'s `facts.Order()`. */
export function orderedFacts(facts: Iterable<string>): string[] {
  return [...facts].sort(ordinal);
}
