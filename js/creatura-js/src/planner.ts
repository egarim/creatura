import { ordinal } from "./format.js";
import type { IWorld, Tool } from "./world.js";

function key(facts: Iterable<string>): string {
  return [...facts].sort(ordinal).join("|");
}

/**
 * Breadth-first search over tool effects. Deliberately dumb: the designs differ in how they control search, not in how
 * clever their planner is. In the host it sits in the model's seat; the interface is the same, so the rest of the
 * architecture does not notice. A port of `Creatura.Planner` (src/Creatura/Core/Planner.cs), expansion order included:
 * the preferred tools first, then by id in ordinal order.
 */
export const Planner = {
  /** The shortest plan from `facts` to the goal, `[]` when the goal already holds, undefined when there is none within `maxDepth`. */
  find(
    facts: ReadonlySet<string>,
    tools: readonly Tool[],
    goal: (facts: ReadonlySet<string>) => boolean,
    maxDepth = 8,
    prefer?: readonly string[],
  ): string[] | undefined {
    if (goal(facts)) return [];

    const rank = (t: Tool) => (prefer !== undefined && prefer.includes(t.id) ? 0 : 1);
    const ordered = [...tools].sort((a, b) => rank(a) - rank(b) || ordinal(a.id, b.id));

    const seen = new Set<string>([key(facts)]);
    const queue: { facts: Set<string>; plan: string[] }[] = [{ facts: new Set(facts), plan: [] }];
    for (let head = 0; head < queue.length; head++) {
      const { facts: current, plan } = queue[head]!;
      if (plan.length >= maxDepth) continue;

      for (const tool of ordered) {
        if (!tool.requires.every((r) => current.has(r))) continue;

        const next = new Set(current);
        for (const g of tool.grants) next.add(g);
        const k = key(next);
        if (seen.has(k)) continue;
        seen.add(k);

        const nextPlan = [...plan, tool.id];
        if (goal(next)) return nextPlan;
        queue.push({ facts: next, plan: nextPlan });
      }
    }

    return undefined;
  },

  /** Over a world's available tools, without the excluded ones. */
  findIn(
    world: IWorld,
    goal: (facts: ReadonlySet<string>) => boolean,
    maxDepth = 8,
    exclude?: ReadonlySet<string>,
    prefer?: readonly string[],
  ): string[] | undefined {
    const tools = world.availableTools.filter((t) => exclude === undefined || !exclude.has(t.id));
    return Planner.find(world.facts, tools, goal, maxDepth, prefer);
  },
} as const;
