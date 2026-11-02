import { Answer } from "./answer.js";
import { mentions, type Problem } from "./problems.js";

/**
 * The app's checks: small functions that turn what a tool did into facts, the effect check's registry on the phone.
 * They read the problem and the turn, never a tool's claim about itself, and they are exact: answers are compared as
 * rationals, and a text gives the answer away when any number in it has the value of one of the answer's numbers.
 */
export const Checks = {
  /** `problem-known`: the text the model was handed is the problem the student is on. */
  problemShown(problem: Problem, text: string): boolean {
    return text.includes(problem.statement);
  },

  /** The exact verdict on the student's attempt. */
  isRight(problem: Problem, attempt: Answer): boolean {
    return Answer.equals(attempt, problem.answer);
  },

  /** True when the text contains a number of the answer, in any form. */
  revealsAnswer(problem: Problem, text: string): boolean {
    return mentions(text, problem.answer);
  },

  /** `hint-given`: a hint was shown and it does not give the answer away. */
  hintSafe(problem: Problem, text: string): boolean {
    return text.trim().length > 0 && !Checks.revealsAnswer(problem, text);
  },
} as const;
