import { World } from "./catalogue-world.js";
import { agentTask, type AgentTask } from "./task.js";
import { tool, type IWorldView, type Tool } from "./world.js";

const required = ["logs.inspected", "component.identified", "patch.applied", "tests.green", "deployed", "alert.resolved"];

/**
 * The demo domain: bring a broken service back to health. Small enough that a deterministic planner can play it,
 * because the point of the code is the control architecture, not search. A port of `Creatura.RestoreServiceDomain`.
 *
 * The four barriers the goldens run it with:
 *   - clean                          → nothing changed
 *   - revoke "apply-patch"           → forces the same goal by different means (`world.revoke("apply-patch")`)
 *   - make "apply-patch" a liar      → reports success once, grants nothing (`world.makeLiar("apply-patch")`)
 *   - revoke "deploy"                → makes the goal unregulatable (`world.revoke("deploy")`)
 */
export const RestoreServiceDomain = {
  initial: ["permission.deploy"] as readonly string[],

  /** Human-readable goal, for policies that read text instead of predicates. */
  goalText: "Alert resolved: logs inspected, broken component identified, a fix applied, tests green, deployed.",

  /** The habit: the tool the agent reaches for first. */
  habit: ["apply-patch"] as readonly string[],

  tools(): Tool[] {
    return [
      tool("inspect-logs", [], ["logs.inspected"], "read"),
      tool("identify-component", ["logs.inspected"], ["component.identified"], "read"),
      tool("apply-patch", ["component.identified"], ["patch.applied"], "write"),
      tool("apply-config", ["component.identified"], ["patch.applied"], "write"),
      tool("run-tests", ["patch.applied"], ["tests.green"], "verify"),
      tool("deploy", ["tests.green", "permission.deploy"], ["deployed"], "release"),
      tool("resolve-alert", ["deployed"], ["alert.resolved"], "close"),
    ];
  },

  newWorld(): World {
    return new World(RestoreServiceDomain.tools(), RestoreServiceDomain.initial);
  },

  goal(facts: ReadonlySet<string>): boolean {
    return facts.has("alert.resolved");
  },

  contractProgress(world: IWorldView): number {
    return world.has("alert.resolved") ? 1.0 : 0.0;
  },

  /** Progress toward the contract, counted in facts so partial work is visible as partial stress. */
  progress(world: IWorldView): number {
    return required.filter((f) => world.has(f)).length / required.length;
  },

  /** The toy domain packaged as a task, so the agents take it as data. */
  get task(): AgentTask {
    return restoreTask;
  },

  /** The restore world with one of the four barriers: "clean", "patchrevoked", "patchlies" or "deployrevoked". */
  withBarrier(barrier: string): World {
    const world = RestoreServiceDomain.newWorld();
    switch (barrier) {
      case "clean": break;
      case "patchrevoked": world.revoke("apply-patch"); break;
      case "patchlies": world.makeLiar("apply-patch"); break;
      case "deployrevoked": world.revoke("deploy"); break;
      default: throw new Error(`'${barrier}': restore barriers are clean, patchrevoked, patchlies, deployrevoked`);
    }

    return world;
  },
};

const restoreTask: AgentTask = agentTask({
  name: "restore the service",
  goalText: RestoreServiceDomain.goalText,
  goal: RestoreServiceDomain.goal,
  progress: RestoreServiceDomain.progress,
  contract: RestoreServiceDomain.contractProgress,
  habit: RestoreServiceDomain.habit,
});
