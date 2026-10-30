import { formatPercent, ordinal } from "./format.js";
import { Stages, type Stage } from "./stages.js";

/** A belief about the agent's own competence, with the evidence that supports it. */
export interface SkillBelief {
  readonly action: string;
  readonly supported: number;
  readonly attempted: number;
}

export const SkillBelief = {
  reliability(belief: SkillBelief): number {
    return belief.attempted === 0 ? 0.5 : belief.supported / belief.attempted;
  },
  with(belief: SkillBelief, held: boolean): SkillBelief {
    return { action: belief.action, supported: belief.supported + (held ? 1 : 0), attempted: belief.attempted + 1 };
  },
} as const;

/** A {@link SelfModel} as data. */
export interface SelfModelSnapshot {
  readonly role: string;
  readonly principal: string;
  readonly stageLevel: number;
  readonly skills: ReadonlyMap<string, SkillBelief>;
  readonly openCommitments: readonly string[];
  readonly recentErrors: readonly string[];
}

/**
 * The agent's self-model: a representation of the agent inside its own world model, read before acting. Load-bearing
 * only because {@link SelfModel.render} is consulted and its beliefs gate action choice. (The C# model also carries
 * the vessel it runs in; that belongs to the cells and is not ported.)
 */
export class SelfModel {
  role = "operator";
  principal = "human";
  stageLevel = 3;
  readonly skills = new Map<string, SkillBelief>();
  readonly openCommitments: string[] = [];
  readonly recentErrors: string[] = [];

  constructor(init?: { role?: string; principal?: string; stageLevel?: number }) {
    if (init?.role !== undefined) this.role = init.role;
    if (init?.principal !== undefined) this.principal = init.principal;
    if (init?.stageLevel !== undefined) this.stageLevel = init.stageLevel;
  }

  get stage(): Stage {
    return Stages.at(this.stageLevel);
  }

  reliability(action: string): number {
    const belief = this.skills.get(action);
    return belief === undefined ? 0.5 : SkillBelief.reliability(belief);
  }

  snapshot(): SelfModelSnapshot {
    return {
      role: this.role,
      principal: this.principal,
      stageLevel: this.stageLevel,
      skills: new Map(this.skills),
      openCommitments: [...this.openCommitments],
      recentErrors: [...this.recentErrors],
    };
  }

  restore(snapshot: SelfModelSnapshot): void {
    this.role = snapshot.role;
    this.principal = snapshot.principal;
    this.stageLevel = snapshot.stageLevel;
    this.skills.clear();
    for (const [k, v] of snapshot.skills) this.skills.set(k, v);
    this.openCommitments.splice(0, this.openCommitments.length, ...snapshot.openCommitments);
    this.recentErrors.splice(0, this.recentErrors.length, ...snapshot.recentErrors);
  }

  recordOutcome(action: string, asPredicted: boolean): void {
    const belief = this.skills.get(action);
    this.skills.set(
      action,
      belief !== undefined ? SkillBelief.with(belief, asPredicted) : { action, supported: asPredicted ? 1 : 0, attempted: 1 },
    );
    if (!asPredicted) {
      this.recentErrors.push(`${action} did not do what I expected`);
      while (this.recentErrors.length > 5) this.recentErrors.shift();
    }
  }

  /** The skills in ordinal order of their action, as C# lists them. */
  orderedSkills(): SkillBelief[] {
    return [...this.skills.values()].sort((a, b) => ordinal(a.action, b.action));
  }

  /** What the agent reads before it acts. Same object, printed: that is the whole trick. Lines end with "\n". */
  render(): string {
    const skills =
      this.skills.size === 0
        ? "  (no evidence yet)"
        : this.orderedSkills()
            .map((s) => `  ${s.action.padEnd(16)} reliable ${formatPercent(SkillBelief.reliability(s))} (${s.supported}/${s.attempted})`)
            .join("\n");
    const errors = this.recentErrors.length === 0 ? "  (none)" : this.recentErrors.map((e) => `  - ${e}`).join("\n");
    const commitments = this.openCommitments.length === 0 ? "  (none)" : this.openCommitments.map((c) => `  - ${c}`).join("\n");
    return [
      `role: ${this.role} for ${this.principal}`,
      `stage: ${this.stage.level} (${this.stage.name})`,
      "skills:",
      skills,
      "open commitments:",
      commitments,
      "recent errors:",
      errors,
    ].join("\n");
  }
}
