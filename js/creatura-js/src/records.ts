import { clamp } from "./format.js";

/**
 * Consequence-modelling evidence. An agent only advances when it predicts its own effects, not when it reports
 * confidence.
 */
export class PredictionTracker {
  private readonly _recent: boolean[] = [];
  private _total = 0;
  private _correct = 0;

  get total(): number {
    return this._total;
  }

  get correct(): number {
    return this._correct;
  }

  get accuracy(): number {
    return this._total === 0 ? 0.0 : this._correct / this._total;
  }

  get recentAccuracy(): number {
    return this._recent.length === 0 ? 0.0 : this._recent.filter((x) => x).length / this._recent.length;
  }

  record(predictedCorrectly: boolean): void {
    this._total++;
    if (predictedCorrectly) this._correct++;
    this._recent.push(predictedCorrectly);
    while (this._recent.length > 8) this._recent.shift();
  }

  /** Four recent predictions at 75%+ is enough to earn the next stage. */
  get earnedNextStage(): boolean {
    return this._recent.length >= 4 && this.recentAccuracy >= 0.75;
  }

  /** The last eight outcomes, oldest first. */
  get recent(): readonly boolean[] {
    return [...this._recent];
  }

  /** Start from a carried record instead of from nothing. */
  seed(correct: number, total: number, recent: Iterable<boolean>): void {
    this._correct = correct;
    this._total = total;
    this._recent.length = 0;
    this._recent.push(...[...recent].slice(-8));
  }

  snapshot(): PredictionSnapshot {
    return { correct: this._correct, total: this._total, recent: [...this._recent] };
  }

  restore(snapshot: PredictionSnapshot): void {
    this.seed(snapshot.correct, snapshot.total, snapshot.recent);
  }
}

/** A {@link PredictionTracker} as data. */
export interface PredictionSnapshot {
  readonly correct: number;
  readonly total: number;
  readonly recent: readonly boolean[];
}

export interface Contradiction {
  readonly tick: number;
  readonly claim: string;
  readonly evidence: string;
}

/**
 * The learning filter: a mind takes in only what makes it more coherent. A claim that contradicts the record is
 * quarantined, not merged.
 */
export class CoherenceAudit {
  private readonly _found: Contradiction[] = [];
  private _claims = 0;

  get claims(): number {
    return this._claims;
  }

  get contradictions(): readonly Contradiction[] {
    return this._found;
  }

  get score(): number {
    if (this._claims === 0) return 1.0;
    return clamp(1.0 - this._found.length / this._claims, 0.0, 1.0);
  }

  /** Register that a claim was made, so the score has a denominator. */
  claim(): void {
    this._claims++;
  }

  /** Gate an incoming belief. Returns false when it contradicts what the agent already holds, and records why. */
  accept(claim: string, supported: boolean, evidence: string, tick: number): boolean {
    if (supported) return true;
    this._found.push({ tick, claim, evidence });
    return false;
  }

  /** An expectation that was formed and then broke. */
  contradict(tick: number, claim: string, evidence: string): void {
    this._found.push({ tick, claim, evidence });
  }

  snapshot(): CoherenceSnapshot {
    return { claims: this._claims, contradictions: [...this._found] };
  }

  restore(snapshot: CoherenceSnapshot): void {
    this._claims = snapshot.claims;
    this._found.length = 0;
    this._found.push(...snapshot.contradictions);
  }
}

/** A {@link CoherenceAudit} as data. */
export interface CoherenceSnapshot {
  readonly claims: number;
  readonly contradictions: readonly Contradiction[];
}
