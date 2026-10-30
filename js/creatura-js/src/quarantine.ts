import { ActionVerdict, GateDecision } from "./types.js";
import type { PredictionTracker } from "./records.js";
import type { SelfModel } from "./self-model.js";
import { Stages } from "./stages.js";
import type { IWorldView, Tool } from "./world.js";

/**
 * Visibility's first pass: which tools the seat may see this tick, and what the layer's verdict on a tool does to
 * that. The default is the {@link Quarantine}. Whatever shapes the offer after this may only narrow it.
 */
export interface IDistrust {
  /** The tools the seat may see this tick. */
  visible(world: IWorldView, tick: number): readonly Tool[];
  /** True when this policy keeps the tool out of sight this tick. */
  hides(toolId: string, tick: number): boolean;
  /** The tools kept out of sight this tick. */
  hidden(tick: number): readonly string[];
  /** The layer judged a tool. `habituated`: an honest failure under the habituate rule. */
  judged(toolId: string, verdict: ActionVerdict, tick: number, habituated: boolean): void;
  /** Distrust lapses at once. */
  clear(): void;
}

/**
 * A quarantine with decay. A tool whose effect did not land is distrusted — but distrust is a hypothesis, not a life
 * sentence: it lapses after a cooldown, so a one-off lie does not permanently remove the only oracle the agent has.
 */
export class Quarantine implements IDistrust {
  private readonly _since = new Map<string, number>();
  readonly cooldownTicks: number;

  constructor(cooldownTicks = 3) {
    this.cooldownTicks = cooldownTicks;
  }

  quarantined(tick: number): string[] {
    const out: string[] = [];
    for (const [id, since] of this._since) if (tick - since < this.cooldownTicks) out.push(id);
    return out;
  }

  add(toolId: string, tick: number): void {
    this._since.set(toolId, tick);
  }

  clear(): void {
    this._since.clear();
  }

  isQuarantined(toolId: string, tick: number): boolean {
    const since = this._since.get(toolId);
    return since !== undefined && tick - since < this.cooldownTicks;
  }

  /** The tools the policy may see this tick. */
  visible(world: IWorldView, tick: number): readonly Tool[] {
    return world.availableTools.filter((t) => !this.isQuarantined(t.id, tick));
  }

  hides(toolId: string, tick: number): boolean {
    return this.isQuarantined(toolId, tick);
  }

  hidden(tick: number): readonly string[] {
    return this.quarantined(tick);
  }

  /** A liar is distrusted for the cooldown; so is an honest failure the layer habituates. */
  judged(toolId: string, verdict: ActionVerdict, tick: number, habituated: boolean): void {
    if (verdict === ActionVerdict.Lied || (verdict === ActionVerdict.HonestFailure && habituated)) this.add(toolId, tick);
  }
}

/**
 * The stage interlock: release is a capability, not a step in a plan. A release-kind tool runs only when the
 * self-model's stage permits it; a stage is earned by a prediction record, never by a claim.
 */
export class StageInterlock {
  readonly self: SelfModel;
  readonly predictions: PredictionTracker;
  private readonly releaseKind: string;

  constructor(self: SelfModel, predictions: PredictionTracker, releaseKind: string) {
    this.self = self;
    this.predictions = predictions;
    this.releaseKind = releaseKind;
  }

  /** Gate one tool. Advances the stage as a side effect when the record has earned it. */
  gate(tool: Tool): GateDecision {
    if (tool.kind !== this.releaseKind || this.self.stage.mayDeploy) return GateDecision.Open;
    if (this.predictions.earnedNextStage) {
      this.self.stageLevel = Math.min(Stages.all.length, this.self.stageLevel + 1);
      return GateDecision.Advanced;
    }

    return GateDecision.Refused;
  }
}
