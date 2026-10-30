import { agentTask, type AgentTask, type IWorldView } from "creatura-js";

import { Fact } from "./facts.js";
import type { Intent, StudentRequest } from "./requests.js";

/**
 * The turn's goal, as evidence: a predicate over facts, never the model's text.
 *
 * - help: `hint-given`, and not `answer-shown`. A hint was shown, and no number of the answer reached the student.
 * - attempt: `attempt-checked`, then either `attempt-right` and `correct-confirmed`, or `attempt-wrong` and
 *   `hint-given`; and not `answer-shown`. The student was told the exact verdict and, if wrong, helped on without
 *   being given the answer.
 */
export function goal(intent: Intent): (facts: ReadonlySet<string>) => boolean {
  return intent === "help"
    ? (f) => f.has(Fact.HintGiven) && !f.has(Fact.AnswerShown)
    : (f) =>
        f.has(Fact.AttemptChecked) &&
        ((f.has(Fact.AttemptRight) && f.has(Fact.CorrectConfirmed)) || (f.has(Fact.AttemptWrong) && f.has(Fact.HintGiven))) &&
        !f.has(Fact.AnswerShown);
}

/** Progress, counted in the facts on the way to the goal, so a turn that stalls shows as stalled. */
function progress(intent: Intent): (world: IWorldView) => number {
  return (world) => {
    const steps = intent === "help"
      ? [Fact.ProblemKnown, Fact.HintGiven]
      : [Fact.ProblemKnown, Fact.AttemptChecked, world.has(Fact.AttemptRight) ? Fact.CorrectConfirmed : Fact.HintGiven];
    return world.has(Fact.AnswerShown) ? 0 : steps.filter((f) => world.has(f)).length / steps.length;
  };
}

/** The turn as a Creatura task. The goal's text quotes the student, so a model in the seat reads the message. */
export function tutorTask(request: StudentRequest): AgentTask {
  const met = goal(request.intent);
  const what = request.intent === "help"
    ? "give the student a hint for the problem they are on, without giving the answer away"
    : "check the student's answer exactly; if it is right, confirm it; if not, say so and give a hint, without giving the answer away";
  return agentTask({
    name: `tutor ${request.intent}`,
    goalText: `The student wrote: "${request.message}". ${what[0]!.toUpperCase()}${what.slice(1)}.`,
    goal: met,
    progress: progress(request.intent),
    contract: (world) => (met(world.facts) ? 1.0 : 0.0),
    habit: [],
  });
}
