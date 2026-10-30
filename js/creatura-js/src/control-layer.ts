import { HostFrame, type IControlFrame } from "./control-frame.js";
import { formatPercent, ordinal } from "./format.js";
import { Quarantine, StageInterlock, type IDistrust } from "./quarantine.js";
import { CoherenceAudit, PredictionTracker } from "./records.js";
import type { SelfModel } from "./self-model.js";
import { SkillBelief } from "./self-model.js";
import { Budget, Plasticity } from "./stages.js";
import type { AgentTask } from "./task.js";
import type { TraceEvent } from "./trace.js";
import { TraceText } from "./trace-text.js";
import { ActionVerdict, GateDecision, HandOff, RefusalReason, StopCause, TraceKind } from "./types.js";
import {
  Admission,
  type ActionReport,
  closure,
  type ActionObservation,
  type Closure,
  type EffectJudgement,
  type StopReason,
} from "./verbs.js";
import { worldVerdict, type ActionOutcome, type IWorldView, type Tool } from "./world.js";

/** How the effect check seeds its verdict before any extension re-judges it. */
export const JudgeRule = {
  /** Landed or lied: the designs' documented default. A tool that reports failure with no effect counts as a lie. */
  TwoWay: "TwoWay",
  /** Landed, lied or honest failure. Landed is read first: a tool that delivered is never a liar whatever it said. */
  ThreeWay: "ThreeWay",
  /** A report of failure is an honest failure whatever else moved; only a report of success with nothing landed is a lie. */
  ReportFirst: "ReportFirst",
} as const;
export type JudgeRule = (typeof JudgeRule)[keyof typeof JudgeRule];

/** What the layer does with an honest failure (reported failure, nothing landed). */
export const HonestFailureRule = {
  /** The designs' rule: quarantine the tool for the cooldown, record nothing, spend nothing. */
  Habituate: "Habituate",
  /** The host's rule: prediction and reliability are recorded, budget is spent, the tool stays visible. */
  StaysVisible: "StaysVisible",
} as const;
export type HonestFailureRule = (typeof HonestFailureRule)[keyof typeof HonestFailureRule];

/** When the layer's failure count goes back to zero. */
export const ResetFailures = {
  /** Failures accumulate across ticks and clear only on a landed effect or a forgiven veto. */
  Never: "Never",
  /** Every round starts with a clean count. */
  PerRound: "PerRound",
} as const;
export type ResetFailures = (typeof ResetFailures)[keyof typeof ResetFailures];

/** The layer's switches. The defaults are the host's. */
export interface ControlLayerOptions {
  readonly judgeRule: JudgeRule;
  readonly honestFailureRule: HonestFailureRule;
  /** When the seat sees no route, distrust lapses at once instead of waiting out the cooldown. */
  readonly clearQuarantineOnNoRoute: boolean;
  readonly resetFailures: ResetFailures;
  readonly cooldownTicks: number;
}

export const ControlLayerOptions = {
  /** How the control layer on the host has always run: three-way judge, honest failures stay visible, failures reset per round. */
  Host: {
    judgeRule: JudgeRule.ThreeWay,
    honestFailureRule: HonestFailureRule.StaysVisible,
    clearQuarantineOnNoRoute: false,
    resetFailures: ResetFailures.PerRound,
    cooldownTicks: 3,
  } as ControlLayerOptions,
  /** How the documented designs run: two-way judge, honest failures habituate, failures accumulate. */
  Design: {
    judgeRule: JudgeRule.TwoWay,
    honestFailureRule: HonestFailureRule.Habituate,
    clearQuarantineOnNoRoute: false,
    resetFailures: ResetFailures.Never,
    cooldownTicks: 3,
  } as ControlLayerOptions,
  /** The host's options with some switches changed, as C#'s `ControlLayerOptions.Host with { ... }`. */
  with(changes: Partial<ControlLayerOptions>, base?: ControlLayerOptions): ControlLayerOptions {
    return { ...(base ?? ControlLayerOptions.Host), ...changes };
  },
} as const;

/** What the layer may be handed besides the world, the task and the self-model. Everything has the C# default. */
export interface ControlLayerInit {
  readonly options?: ControlLayerOptions;
  readonly frame?: IControlFrame;
  readonly predictions?: PredictionTracker;
  readonly audit?: CoherenceAudit;
  readonly trace?: TraceEvent[];
  readonly notes?: string[];
  /** The self-model the effect check records outcomes into, when the regulator keeps two. */
  readonly ledger?: SelfModel;
  readonly distrust?: IDistrust;
}

/**
 * The control layer, host-free: the five principles between the seat and the tools.
 *
 *   1. judge by effect, not by report   — after every action, ask the registry whether the promised facts hold
 *   2. quarantine with decay            — a tool whose effect did not land is hidden for a few ticks
 *   3. the agent decides what is seen   — tools are filtered from the offer and refused on call
 *   4. gates earned by track record     — a release-kind tool needs a prediction record, not a claim
 *   5. goals written as evidence        — "done" is a predicate over facts, never the seat's text
 *
 * Four components: Visibility (`offer`, over the quarantine), Gatekeeper (`admit`: legality against the visible set,
 * then the frame's gate, by default the stage interlock), EffectCheck (`judge`: the registry, the judge rule, the
 * re-judge chain, the honest-failure rule) and Ledger (the self-model, predictions, audit, failures, trace and notes).
 * It reads the world through {@link IWorldView} only and renders through an {@link IControlFrame}. The round verbs
 * (`beginRound`, `stop`, `assemble`, `close`, `sleep`) drive a host whose tick is one model call.
 *
 * A port of `Creatura.ControlLayer` (src/Creatura/Core/ControlLayer.cs), rule for rule and string for string.
 */
export class ControlLayer {
  private readonly _world: IWorldView;
  private readonly _task: AgentTask;
  private readonly _options: ControlLayerOptions;
  private readonly _frame: IControlFrame;
  private readonly _trace: TraceEvent[];
  private readonly _notes: string[];
  private readonly _observers = new Set<(observation: ActionObservation) => void>();
  private _lastProgress = -1.0;
  private _visible: readonly Tool[] | undefined;

  readonly self: SelfModel;
  /** The self-model the effect check records outcomes into: `self` unless the regulator keeps two. */
  readonly ledger: SelfModel;
  readonly predictions: PredictionTracker;
  readonly audit: CoherenceAudit;
  /** Visibility's first pass: the quarantine by default, or the regulator's own policy. */
  readonly distrust: IDistrust;
  readonly interlock: StageInterlock;

  private _tick = 0;
  private _lastOutcome: ActionOutcome | undefined;
  private _failures = 0;
  private _escalation: string | undefined;
  private _freedom: number;
  private _budget = 1;
  private _progress = 0;
  private _staleRounds = 0;

  constructor(world: IWorldView, task: AgentTask, self: SelfModel, init: ControlLayerInit = {}) {
    this._world = world;
    this._task = task;
    this._options = init.options ?? ControlLayerOptions.Host;
    this._frame = init.frame ?? HostFrame.instance;
    this._trace = init.trace ?? [];
    this._notes = init.notes ?? [];
    this.self = self;
    this.ledger = init.ledger ?? self;
    this.predictions = init.predictions ?? new PredictionTracker();
    this.audit = init.audit ?? new CoherenceAudit();
    this.distrust = init.distrust ?? new Quarantine(this._options.cooldownTicks);
    this.interlock = new StageInterlock(this.self, this.predictions, task.releaseKind);
    this._freedom = this.self.stage.freedomCap;
  }

  get options(): ControlLayerOptions {
    return this._options;
  }

  get world(): IWorldView {
    return this._world;
  }

  get task(): AgentTask {
    return this._task;
  }

  // ---- Ledger -------------------------------------------------------------------------------------------

  /** One tick per seat call (the host's rule) or per loop iteration; the loop that owns the seat calls `beginTick`. */
  get tick(): number {
    return this._tick;
  }

  /** The outcome the world reported for the action under way, when the tool wrapper handed it in. */
  get lastOutcome(): ActionOutcome | undefined {
    return this._lastOutcome;
  }

  get failures(): number {
    return this._failures;
  }

  /** Set when a release was refused: the decision is a human's now. */
  get escalation(): string | undefined {
    return this._escalation;
  }

  get trace(): readonly TraceEvent[] {
    return this._trace;
  }

  get notes(): readonly string[] {
    return this._notes;
  }

  get incoherence(): number {
    return 1.0 - this.audit.score;
  }

  get freedom(): number {
    return this._freedom;
  }

  get budget(): number {
    return this._budget;
  }

  /** The task's progress as read at `beginRound`. */
  get progress(): number {
    return this._progress;
  }

  /** Rounds in a row that ended no further along than they started. */
  get staleRounds(): number {
    return this._staleRounds;
  }

  /** The visible set last offered, which `admit` judges legality against. */
  get visible(): readonly Tool[] {
    return this._visible ?? this.offer();
  }

  note(note: string): void {
    this._notes.push(note);
  }

  /** A row at the layer's tick and freedom; pass `freedom` for a row with its own freedom column. */
  record(level: string, setPoint: string, stress: number, action: string, kind: TraceKind, detail: string, freedom?: number): void {
    this._trace.push({ tick: this._tick, level, setPoint, stress, freedom: freedom ?? this._freedom, action, kind, detail });
  }

  escalate(escalation: string): void {
    this._escalation = escalation;
  }

  /** A failure spent outside the layer's own verbs (the loop's no-route tick). */
  spend(): void {
    this._failures++;
  }

  /** A vetoed hand-off forgives the attempts. */
  forgive(): void {
    this._failures = 0;
  }

  // ---- Verbs ---------------------------------------------------------------------------------------------

  beginTick(): void {
    this._tick++;
  }

  /**
   * Open a round. With no arguments, the host's rule: stress is the worse of the contract gap and the incoherence of
   * the record, plasticity turns it into freedom, the stage caps it, and the budget follows. With a freedom and a
   * budget, the regulator's numbers. Failures reset when the options say so.
   */
  beginRound(freedom?: number, budget?: number): void {
    if (freedom !== undefined && budget !== undefined) {
      this.openRound(freedom, budget);
      return;
    }

    const contract = this._task.contract(this._world);
    const stress = Math.max(contract < 1.0 ? 1.0 - contract : 0.0, this.incoherence);
    const f = Math.min(this.self.stage.freedomCap, Plasticity.freedom(stress));
    this.openRound(f, Budget.attempts(f));

    this._progress = this._task.progress(this._world);
    this._staleRounds = this._progress <= this._lastProgress ? this._staleRounds + 1 : 0;
    this._lastProgress = this._progress;
  }

  private openRound(freedom: number, budget: number): void {
    this._freedom = freedom;
    this._budget = budget;
    if (this._options.resetFailures === ResetFailures.PerRound) this._failures = 0;
  }

  /** The host's stop check before the seat acts: rounds that act without moving the gap are handed to a human. */
  stop(): StopReason | undefined {
    if (this._staleRounds < 2) return undefined;
    const detail = `${this._staleRounds} rounds without progress at ${formatPercent(this._freedom)} freedom`;
    this.record("organism", "contract", 1.0 - this._progress, TraceText.StuckRegulation, TraceKind.Escalated, detail);
    this.note("stuck-regulation alarm: acting without moving the gap, handed to a human");
    return { cause: StopCause.StuckRegulation, detail, handOff: HandOff.StuckRegulation };
  }

  /**
   * Visibility: the tools the seat may see this tick. Distrusted tools are simply not offered. `shape` narrows the
   * option space after the quarantine; the result is what `admit` judges against.
   */
  offer(shape?: (visible: readonly Tool[]) => readonly Tool[]): readonly Tool[] {
    const visible = this.distrust.visible(this._world, this._tick);
    this._visible = shape === undefined ? visible : shape(visible);
    return this._visible;
  }

  isVisible(toolId: string): boolean {
    return this._world.availableTools.some((t) => t.id === toolId) && !this.distrust.hides(toolId, this._tick);
  }

  /** What the seat reads before it acts on the host: its own record, and the row that says the round began. */
  assemble(round: number): string {
    this.record("organism", "contract", 1.0 - this._progress, TraceText.Round, TraceKind.Idle,
      `round ${round}: budget ${this._budget}, stage ${this.self.stage.level}`);
    return this.self.render();
  }

  /**
   * Gatekeeper: may this tool run? Legality against the visible set first (a seat that names a tool it was not
   * offered is refused, never obeyed), then the gate for a release. A refusal spends budget; a refused release ends
   * the round and leaves the decision to a human.
   */
  admit(toolId: string): Admission {
    const tool = this._world.tool(toolId);
    const visible = this.visible;
    const refusal =
      tool === undefined ? RefusalReason.NoSuchTool
      : this.distrust.hides(toolId, this._tick) ? RefusalReason.Quarantined
      : !this.isVisible(toolId) ? RefusalReason.NotAvailable
      : visible.every((t) => t.id !== toolId) ? RefusalReason.NotVisible
      : undefined;

    if (refusal !== undefined) {
      this._failures++;
      this._frame.refused(this, toolId, refusal);
      // A decision layer that keeps naming refused tools has run out of budget: end this round.
      return Admission.refuse(tool, refusal, this._failures > this._budget,
        `REFUSED: ${toolId} is not available (${TraceText.why(refusal)}). Choose among the tools offered.`);
    }

    const admitted = tool!;
    const gate = this._frame.gate ? this._frame.gate(this, admitted) : this.interlock.gate(admitted);
    if (gate === GateDecision.Advanced) {
      this._frame.stageAdvanced(this);
    } else if (gate === GateDecision.Refused) {
      this._frame.releaseRefused(this, admitted);
      // The decision is a human's now; the function-calling loop ends here and the host reports.
      return Admission.refuse(admitted, RefusalReason.ReleaseNotEarned, true,
        `REFUSED: ${toolId} is a release action and this agent has not earned it yet. A human must approve it. Stop and report.`);
    }

    this._lastOutcome = undefined;
    return Admission.admit(admitted, gate);
  }

  /** The tool wrapper reports what the world answered, so the effect check can tell an honest failure from a lie. */
  reportOutcome(outcome: ActionOutcome): void {
    this._lastOutcome = outcome;
  }

  /**
   * Subscribe to every judged action, raised once at the end of `judge`, after the verdict, the re-judge chain, the
   * observer, the record and the frame's row. What a subscriber does cannot reach the verdict or the feedback; an
   * exception it throws does. Returns the unsubscribe function. (C#: the `ActionObserved` event.)
   */
  onActionObserved(listener: (observation: ActionObservation) => void): () => void {
    this._observers.add(listener);
    return () => this._observers.delete(listener);
  }

  /**
   * EffectCheck: judge by the registry, never by the report. Landed is read first; then the report decides between
   * a lie and an honest failure under the three-way rule; then the re-judge chain and the observer run. A report the
   * host did not see counts as a claim of success. The record is updated for everything but a habituated honest
   * failure; the feedback is what the seat reads next; the action observers are told last.
   */
  judge(
    tool: Tool,
    report: ActionReport,
    rejudge?: (verdict: ActionVerdict) => ActionVerdict,
    observed?: (verdict: ActionVerdict) => void,
  ): EffectJudgement {
    // A report the host did not see is a claim of success.
    const outcome: ActionOutcome = report.outcome ?? { tool: tool.id, succeeded: true, granted: [], observation: undefined };
    const judgement = this.judgeAndRecord(tool, report, outcome, rejudge, observed);
    for (const listener of [...this._observers]) {
      listener({ tick: this._tick, tool, outcome, verdict: judgement.verdict, landed: judgement.landed });
    }

    return judgement;
  }

  private judgeAndRecord(
    tool: Tool,
    report: ActionReport,
    outcome: ActionOutcome,
    rejudge: ((verdict: ActionVerdict) => ActionVerdict) | undefined,
    observed: ((verdict: ActionVerdict) => void) | undefined,
  ): EffectJudgement {
    const landed = this._world.effectsLanded(tool);
    let verdict: ActionVerdict =
      // The world's own three-way verdict, report first (the map-keeper's order).
      this._options.judgeRule === JudgeRule.ReportFirst ? worldVerdict(this._world, tool, outcome)
      // Landed first, as the host has always judged; the world's verdict tells the honest failure from the lie.
      : this._options.judgeRule === JudgeRule.ThreeWay ? (landed ? ActionVerdict.Landed : worldVerdict(this._world, tool, outcome))
      : landed ? ActionVerdict.Landed : ActionVerdict.Lied;
    if (rejudge !== undefined) verdict = rejudge(verdict);
    observed?.(verdict);
    const missing = tool.grants.filter((g) => !this._world.has(g));

    if (verdict === ActionVerdict.HonestFailure && this._options.honestFailureRule === HonestFailureRule.Habituate) {
      // Not a lie and not a wrong prediction about the tool: habituate, say so, spend nothing.
      this.distrust.judged(tool.id, verdict, this._tick, true);
      this._frame.acted(this, tool, report, verdict, landed, true);
      return {
        verdict, landed: false, missing, terminate: false,
        feedback: `${report.reported}\n\n[not verified: ${missing.join(", ")} still false; the tool reported failure]`,
      };
    }

    this.predictions.record(landed);
    this.ledger.recordOutcome(tool.id, landed);
    this.audit.claim();
    if (verdict === ActionVerdict.Lied) {
      this.audit.accept(`${tool.id} produces ${tool.grants.join(",")}`, false, this._frame.evidence(this, tool, report), this._tick);
    }

    this.distrust.judged(tool.id, verdict, this._tick, false);
    if (landed) {
      if (this._options.resetFailures === ResetFailures.Never) this._failures = 0;
      this._frame.acted(this, tool, report, verdict, landed, false);
      return {
        verdict, landed: true, missing, terminate: false,
        feedback: `${report.reported}\n\n[verified: ${tool.grants.join(", ")} now hold]`,
      };
    }

    this._failures++;
    const terminate = this._failures > this._budget;

    if (verdict === ActionVerdict.HonestFailure) {
      // An honest failure: the tool said it failed and it did. That costs budget and reliability, but it is not a
      // lie, so the tool stays visible — the oracle that reports red is still the oracle.
      this._frame.acted(this, tool, report, verdict, landed, false);
      return {
        verdict, landed: false, missing, terminate,
        feedback: `${report.reported}\n\n[not verified: ${missing.join(", ")} still false; the tool reported failure]`,
      };
    }

    // Reported success, effect missing: a contradiction. Distrust has hidden the tool; tell the seat why.
    this._frame.acted(this, tool, report, verdict, landed, false);
    return {
      verdict, landed: false, missing, terminate,
      feedback:
        `WARNING: ${tool.id} reported '${report.summary}' but ${missing.join(", ")} did not become true. ` +
        "Its effect did not land. It is distrusted for now; use a different route.",
    };
  }

  /**
   * Close a round on the host: success is a predicate over the world, never the seat's text; then the escalations in
   * the host's order (a refused release, a spent budget), the seat's reply as a row, and no route, which is a wait
   * while something is quarantined and a hand-off when nothing is.
   */
  close(reply: string, noRoute: boolean): Closure {
    if (this._task.goal(this._world.facts)) {
      this.record("organism", "contract", 0.0, TraceText.ContractMet, TraceKind.Acted, TraceText.complete(this._task.name));
      return closure(true, { cause: StopCause.GoalMet, detail: TraceText.complete(this._task.name) });
    }

    if (this._escalation !== undefined) {
      this.note(`escalated: ${this._escalation}`);
      return closure(false, { cause: StopCause.Escalation, detail: this._escalation });
    }

    if (this._failures > this._budget) {
      const detail = `${this._failures} failed calls exceeded the budget of ${this._budget}`;
      this.record("organism", "contract", 1.0 - this._task.progress(this._world), TraceText.HandOff, TraceKind.Escalated, detail);
      this.note("escalated: failures exceeded the freedom budget");
      return closure(false, { cause: StopCause.BudgetExceeded, detail });
    }

    this.record("model", "reply", 1.0 - this._task.progress(this._world), TraceText.Reply, TraceKind.Idle,
      reply.length > 90 ? reply.slice(0, 90) + "…" : reply);

    if (noRoute) {
      // The seat sees no route with the trusted tools. If something is quarantined, waiting is a route; otherwise the
      // goal is unregulatable with what this agent has.
      if (this.distrust.hidden(this._tick).length === 0) {
        this.record("organism", "contract", 1.0 - this._task.progress(this._world), TraceText.NoPath, TraceKind.Escalated,
          "no route with the available tools");
        this.note("escalated: no path with the available tools; the goal is unregulatable");
        return closure(false, { cause: StopCause.NoPath, detail: "no route with the available tools", handOff: HandOff.NoPath });
      }

      if (this._options.clearQuarantineOnNoRoute) this.distrust.clear();
      this.note("no path right now; a distrusted tool may return after its cooldown");
    }

    return closure(false, undefined);
  }

  /** Consolidation on the host: the record the run leaves, as a row and a note. */
  sleep(): void {
    const skills = [...this.self.skills.values()]
      .sort((a, b) => ordinal(a.action, b.action))
      .map((s) => `${s.action} ${formatPercent(SkillBelief.reliability(s))}`);
    this.record("organism", "consolidation", 0.0, TraceText.Sleep, TraceKind.Consolidated,
      `coherence ${formatPercent(this.audit.score)}, predictions ${formatPercent(this.predictions.accuracy)} over ${this.predictions.total}, stage ${this.self.stage.level}`);
    this.note(`coherence ${formatPercent(this.audit.score)}; reliabilities: ${skills.length > 0 ? skills.join(", ") : "none"}`);
  }
}
