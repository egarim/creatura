import { NoPathReply, type Seat, type SeatReply } from "creatura-js";

import { Fact, ToolId } from "./facts.js";
import { numberTokens } from "./rational.js";

/** What the scripted driver says when the turn's work is done. No numbers, so it never gives an answer away. */
export const DoneReply = "¿Quieres intentarlo de nuevo con esta pista?";

/**
 * The scripted driver: a deterministic policy in the model's seat, as the planner is in the restore world. It reads
 * only what a model is given: the student's message (quoted in the goal), the facts and the offered tools. It does
 * not know which voice wrote the message or what the answer is.
 *
 * A message with numbers in it is an attempt, and those numbers, in order, are `check_answer`'s payload. Then:
 * read the problem; check an attempt; confirm a right one; give a hint for a wrong one or a request for help; then say
 * {@link DoneReply}. When the tool it needs is not offered (distrusted after a lie), it answers "NO PATH".
 */
export function scriptedDriver(message: string): Seat {
  const numbers = numberTokens(message).map((t) => t.text);
  const payload = numbers.length > 0 ? numbers.join(" ") : undefined;

  return (call): SeatReply => {
    const facts = call.facts;
    const offered = new Set(call.offered.map((t) => t.id));
    const use = (tool: string, content?: string): SeatReply => (offered.has(tool) ? { tool, content } : { text: NoPathReply });

    if (!facts.has(Fact.ProblemKnown)) return use(ToolId.ReadProblem);
    if (payload !== undefined) {
      if (!facts.has(Fact.AttemptChecked)) return use(ToolId.CheckAnswer, payload);
      if (facts.has(Fact.AttemptRight)) return facts.has(Fact.CorrectConfirmed) ? { text: DoneReply } : use(ToolId.ConfirmCorrect);
    }
    return facts.has(Fact.HintGiven) ? { text: DoneReply } : use(ToolId.NextHint);
  };
}
