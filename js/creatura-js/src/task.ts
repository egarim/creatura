import type { IWorldView } from "./world.js";

/**
 * A task the agents can be pointed at without changing the architecture: the goal, how progress is measured, the
 * habit, and which tool kind needs a stage interlock before it may run. (The C# task also carries a valence table for
 * the orienting regulator, a design, which is not ported.)
 */
export interface AgentTask {
  readonly name: string;
  readonly goalText: string;
  readonly goal: (facts: ReadonlySet<string>) => boolean;
  readonly progress: (world: IWorldView) => number;
  readonly contract: (world: IWorldView) => number;
  readonly habit: readonly string[];
  /** The tool kind gated by the stage interlock. */
  readonly releaseKind: string;
}

/** A task, with C#'s default release kind ("release") when none is given. */
export function agentTask(task: Omit<AgentTask, "releaseKind"> & { readonly releaseKind?: string }): AgentTask {
  return { ...task, releaseKind: task.releaseKind ?? "release" };
}
