import { Answer } from "./answer.js";
import { Fact } from "./facts.js";
import type { Problem } from "./problems.js";

/**
 * What one of the app's tools returns: whether it says it worked, the text it shows (to the model, and for the tools
 * that talk to the student, to the student), and the facts it claims. A claim is only a claim: the registry is written
 * by the app's checks (checks.ts).
 */
export interface ToolResult {
  readonly succeeded: boolean;
  readonly text: string;
  readonly claims: readonly string[];
}

/** What the tools read: the problem the student is on, and how many hints this conversation has given. */
export interface TutorState {
  readonly problem: Problem;
  readonly hintsGiven: number;
}

/**
 * The tutor's tools: the TypeScript a phone runs (decision D4), with no I/O beyond the state they are handed, so the
 * emulated world runs the very same code. None of them echoes the student's numbers, and only `read_problem` shows the
 * problem's own.
 */
export const TutorTools = {
  /** The problem the student is on, for the model. */
  readProblem(state: TutorState): ToolResult {
    return { succeeded: true, text: state.problem.statement, claims: [Fact.ProblemKnown] };
  },

  /** An exact check of the student's answer, read from `payload` the way the problem asks for it. */
  checkAnswer(state: TutorState, payload: string | undefined): ToolResult {
    const attempt = payload === undefined ? undefined : Answer.parse(state.problem.answer.kind, payload);
    if (attempt === undefined) return { succeeded: false, text: "No encontré una respuesta que revisar.", claims: [] };
    return Answer.equals(attempt, state.problem.answer)
      ? { succeeded: true, text: "Revisé tu respuesta: es correcta.", claims: [Fact.AttemptChecked, Fact.AttemptRight] }
      : { succeeded: true, text: "Revisé tu respuesta: todavía no es correcta.", claims: [Fact.AttemptChecked, Fact.AttemptWrong] };
  },

  /** The next hint, gentlest first; past the last one, the last one again. */
  nextHint(state: TutorState): ToolResult {
    const hints = state.problem.hints;
    const hint = hints[Math.min(state.hintsGiven, hints.length - 1)] ?? "";
    return { succeeded: true, text: `Pista: ${hint}`, claims: [Fact.HintGiven] };
  },

  /** Marks the problem solved. */
  confirmCorrect(_state: TutorState): ToolResult {
    return { succeeded: true, text: "¡Muy bien! Resolviste el problema.", claims: [Fact.CorrectConfirmed] };
  },
} as const;

/**
 * The barriers: the same tools written wrong on purpose, as the restore world's liars are. The world swaps one in for
 * the honest tool when its barrier is switched on.
 */
export const FaultyTools = {
  /** `checker-lies`: reports right when the answer is wrong. On a right answer it is the honest checker. */
  lyingCheckAnswer(state: TutorState, payload: string | undefined): ToolResult {
    const honest = TutorTools.checkAnswer(state, payload);
    if (!honest.claims.includes(Fact.AttemptWrong)) return honest;
    return { succeeded: true, text: "Revisé tu respuesta: es correcta.", claims: [Fact.AttemptChecked, Fact.AttemptRight] };
  },

  /** `hint-leaks`: the honest hint, then the answer. */
  leakyNextHint(state: TutorState): ToolResult {
    const honest = TutorTools.nextHint(state);
    return { ...honest, text: `${honest.text} La respuesta es ${Answer.say(state.problem.answer, state.problem.unit)}.` };
  },

  /** `read-grants-nothing`: reports success and returns no problem, the lie convention (a read that silently failed). */
  silentReadProblem(_state: TutorState): ToolResult {
    return { succeeded: true, text: "Listo, ya tengo el problema.", claims: [] };
  },
} as const;
