import { tool, type Tool } from "creatura-js";

/** The tutor's facts. The registry holds only what the app's checks confirmed, never what a tool said. */
export const Fact = {
  /** The model has read the problem the student is on. */
  ProblemKnown: "problem-known",
  /** The student's message gives an answer to check (set when the turn opens). */
  AttemptGiven: "attempt-given",
  /** The student was told the exact verdict on their attempt. */
  AttemptChecked: "attempt-checked",
  /** The attempt is exactly right. Set with `attempt-checked`, never alone. */
  AttemptRight: "attempt-right",
  /** The attempt is wrong. Set with `attempt-checked`, never alone. */
  AttemptWrong: "attempt-wrong",
  /** The student was shown a hint that does not give the answer away. */
  HintGiven: "hint-given",
  /** The problem is marked solved, and the student was told so. */
  CorrectConfirmed: "correct-confirmed",
  /** Something the student saw contains a number of the answer. No tool grants it; every goal requires its absence. */
  AnswerShown: "answer-shown",
} as const;
export type Fact = (typeof Fact)[keyof typeof Fact];

/** The tutor's tools, by id. */
export const ToolId = {
  ReadProblem: "read_problem",
  CheckAnswer: "check_answer",
  NextHint: "next_hint",
  ConfirmCorrect: "confirm_correct",
} as const;
export type ToolId = (typeof ToolId)[keyof typeof ToolId];

/**
 * The four tools in Creatura's terms, `Tool(id, requires, grants, kind)`. `check_answer` declares only
 * `attempt-checked`: which of `attempt-right` and `attempt-wrong` comes with it is what the call finds out, so it can't be
 * promised before the call, and showing it in the offer would tell the model the verdict.
 */
export function tutorTools(): Tool[] {
  return [
    tool(ToolId.ReadProblem, [], [Fact.ProblemKnown], "read"),
    tool(ToolId.CheckAnswer, [Fact.ProblemKnown, Fact.AttemptGiven], [Fact.AttemptChecked], "verify", {
      needsPayload: true,
      payloadHint:
        "the numbers of the student's answer, in order: \"144\", \"7/4\", \"1 3/4\", \"1,75\", or quotient and remainder \"9 2\"",
    }),
    tool(ToolId.NextHint, [Fact.ProblemKnown], [Fact.HintGiven], "teach"),
    tool(ToolId.ConfirmCorrect, [Fact.AttemptRight], [Fact.CorrectConfirmed], "close"),
  ];
}
