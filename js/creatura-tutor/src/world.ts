import { ordinal, type ActionOutcome, type IWorld, type Tool } from "creatura-js";

import { Answer } from "./answer.js";
import { Checks } from "./checks.js";
import { Fact, ToolId, tutorTools } from "./facts.js";
import type { StudentRequest } from "./requests.js";
import { FaultyTools, TutorTools, type ToolResult, type TutorState } from "./tools.js";

/** The barriers: tools that lie on purpose. Each swaps one tool's code for its faulty version. */
export const Barrier = {
  /** `check_answer` reports right when the answer is wrong. */
  CheckerLies: "checker-lies",
  /** `next_hint` gives the answer away. */
  HintLeaks: "hint-leaks",
  /** `read_problem` reports success and grants nothing. */
  ReadGrantsNothing: "read-grants-nothing",
} as const;
export type Barrier = (typeof Barrier)[keyof typeof Barrier];

export const barriers: readonly Barrier[] = [Barrier.CheckerLies, Barrier.HintLeaks, Barrier.ReadGrantsNothing];

/** The tools whose text the student sees. `read_problem` is for the model: the app already shows the problem. */
const toStudent: ReadonlySet<string> = new Set([ToolId.CheckAnswer, ToolId.NextHint, ToolId.ConfirmCorrect]);

/** What a tool's act puts back in question: its own grants, and for the checker the verdict that comes with it. */
const retracts: Readonly<Record<string, readonly string[]>> = {
  [ToolId.ReadProblem]: [Fact.ProblemKnown],
  [ToolId.CheckAnswer]: [Fact.AttemptChecked, Fact.AttemptRight, Fact.AttemptWrong],
  [ToolId.NextHint]: [Fact.HintGiven],
  [ToolId.ConfirmCorrect]: [Fact.CorrectConfirmed],
};

/** One line of what was put in front of the student, or would have been. */
export interface ScreenLine {
  readonly tick: number;
  /** The tool that wrote it, or "reply" for the model's own text. */
  readonly source: string;
  readonly text: string;
  /** The tool said it worked. Always true for a reply. */
  readonly reported: boolean;
  /** The app's checks confirmed the tool's effect. Always true for a reply, which no check stands between. */
  readonly landed: boolean;
}

/**
 * The tutor's emulated world for one turn: one student, one problem, one message. The tools are the app's own code
 * (tools.ts); with a barrier switched on, its faulty version runs instead. The registry is written only by the app's
 * checks (checks.ts):
 *
 * - `read_problem` lands `problem-known` when its text is the problem the student is on;
 * - `check_answer` lands `attempt-checked` with `attempt-right` or `attempt-wrong` when it checked the student's own
 *   attempt (the payload reads as the same answer) and its verdict is the exact one; otherwise nothing;
 * - `next_hint` lands `hint-given` when its hint does not contain a number of the answer;
 * - `confirm_correct` lands `correct-confirmed` when the student's attempt is exactly right.
 *
 * A tool that reports success with nothing landed is a lie to the control layer. Each act first retracts the facts
 * the tool would grant, so a fact always speaks for the tool's latest act and the layer's live read of the registry is
 * a verdict on that act, not on an earlier one: a leaky second hint does not ride on a clean first one.
 *
 * The screen keeps everything a tool or the model put in front of the student. The guarded view is what the adapter
 * shows: the student's tools' texts whose effect landed (the layer's verdict Landed), and the model's replies. The
 * unguarded view is what a loop that trusts reports would show. `answer-shown` is raised when a line of the guarded view
 * contains a number of the answer.
 */
export class TutorWorld implements IWorld {
  readonly request: StudentRequest;
  private readonly _barriers: Set<Barrier>;
  private readonly _tools: readonly Tool[];
  private readonly _facts = new Set<string>();
  private readonly _screen: ScreenLine[] = [];
  private _ticks = 0;
  private _hintsGiven = 0;

  constructor(request: StudentRequest, barriers: Iterable<Barrier> = []) {
    this.request = request;
    this._barriers = new Set(barriers);
    this._tools = tutorTools().sort((a, b) => ordinal(a.id, b.id));
    if (request.attempt !== undefined) this._facts.add(Fact.AttemptGiven);
  }

  /** The barriers switched on. */
  get barriers(): ReadonlySet<Barrier> {
    return this._barriers;
  }

  /** Switches a barrier on mid-turn, as the restore world's `makeLiar` does: the tool's next act runs its faulty code. */
  switchOn(barrier: Barrier): void {
    this._barriers.add(barrier);
  }

  get facts(): ReadonlySet<string> {
    return this._facts;
  }

  get ticks(): number {
    return this._ticks;
  }

  get allTools(): readonly Tool[] {
    return this._tools;
  }

  get availableTools(): readonly Tool[] {
    return this._tools;
  }

  has(fact: string): boolean {
    return this._facts.has(fact);
  }

  tool(id: string): Tool | undefined {
    return this._tools.find((t) => t.id === id);
  }

  /** The registry holds everything the tool promised, read live. */
  effectsLanded(tool: Tool): boolean {
    return tool.grants.every((g) => this._facts.has(g));
  }

  /** Everything put in front of the student, or that would have been, in order. */
  get screen(): readonly ScreenLine[] {
    return this._screen;
  }

  /** What the student saw through the guarded adapter. */
  get shown(): string[] {
    return this._screen.filter((l) => l.source === "reply" || (toStudent.has(l.source) && l.landed)).map((l) => l.text);
  }

  /** What the student would have seen through a loop that trusts every report. */
  get shownUnguarded(): string[] {
    return this._screen.filter((l) => l.source === "reply" || (toStudent.has(l.source) && l.reported)).map((l) => l.text);
  }

  /** The model's own words to the student: no check stands between them and the screen. */
  say(text: string): void {
    this._screen.push({ tick: this._ticks, source: "reply", text, reported: true, landed: true });
    if (Checks.revealsAnswer(this.request.problem, text)) this._facts.add(Fact.AnswerShown);
  }

  /** The refusal order of the catalogue worlds: unknown tool, then preconditions unmet, then the tool's code. */
  act(toolId: string, payload?: string): ActionOutcome {
    this._ticks++;
    const tool = this.tool(toolId);
    if (tool === undefined) return { tool: toolId, succeeded: false, granted: [], observation: "unknown tool" };

    const missing = tool.requires.filter((r) => !this._facts.has(r));
    if (missing.length > 0) {
      return { tool: toolId, succeeded: false, granted: [], observation: `preconditions unmet: ${missing.join(", ")}` };
    }

    for (const fact of retracts[toolId] ?? []) this._facts.delete(fact);
    const result = this.run(toolId, payload);
    const landed = result.succeeded ? this.check(toolId, payload, result) : [];
    for (const fact of landed) this._facts.add(fact);
    if (toolId === ToolId.NextHint) this._hintsGiven++;

    this._screen.push({ tick: this._ticks, source: toolId, text: result.text, reported: result.succeeded, landed: landed.length > 0 });
    if (toStudent.has(toolId) && landed.length > 0 && Checks.revealsAnswer(this.request.problem, result.text)) {
      this._facts.add(Fact.AnswerShown);
    }

    return { tool: toolId, succeeded: result.succeeded, granted: result.claims, observation: result.text };
  }

  /** The tool's code: the app's, or its faulty version when that barrier is on. */
  private run(toolId: string, payload: string | undefined): ToolResult {
    const state: TutorState = { problem: this.request.problem, hintsGiven: this._hintsGiven };
    switch (toolId) {
      case ToolId.ReadProblem:
        return this._barriers.has(Barrier.ReadGrantsNothing) ? FaultyTools.silentReadProblem(state) : TutorTools.readProblem(state);
      case ToolId.CheckAnswer:
        return this._barriers.has(Barrier.CheckerLies) ? FaultyTools.lyingCheckAnswer(state, payload) : TutorTools.checkAnswer(state, payload);
      case ToolId.NextHint:
        return this._barriers.has(Barrier.HintLeaks) ? FaultyTools.leakyNextHint(state) : TutorTools.nextHint(state);
      case ToolId.ConfirmCorrect:
        return TutorTools.confirmCorrect(state);
      default:
        throw new Error(`no code for tool ${toolId}`);
    }
  }

  /** The app's checks: the facts the tool's act really established. */
  private check(toolId: string, payload: string | undefined, result: ToolResult): string[] {
    const problem = this.request.problem;
    const attempt = this.request.attempt;
    switch (toolId) {
      case ToolId.ReadProblem:
        return Checks.problemShown(problem, result.text) ? [Fact.ProblemKnown] : [];
      case ToolId.CheckAnswer: {
        if (attempt === undefined || payload === undefined) return [];
        const read = Answer.parse(problem.answer.kind, payload);
        if (read === undefined || !Answer.equals(read, attempt)) return [];
        const verdict = Checks.isRight(problem, attempt) ? Fact.AttemptRight : Fact.AttemptWrong;
        return result.claims.includes(verdict) ? [Fact.AttemptChecked, verdict] : [];
      }
      case ToolId.NextHint:
        return Checks.hintSafe(problem, result.text) ? [Fact.HintGiven] : [];
      case ToolId.ConfirmCorrect:
        return attempt !== undefined && Checks.isRight(problem, attempt) ? [Fact.CorrectConfirmed] : [];
      default:
        return [];
    }
  }
}
