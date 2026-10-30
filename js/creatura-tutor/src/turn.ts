import { ActionVerdict, ControlledHost, type AgentRun, type Seat, type TraceEvent } from "creatura-js";

import { scriptedDriver } from "./driver.js";
import { goal, tutorTask } from "./goal.js";
import type { StudentRequest } from "./requests.js";
import { TutorWorld, type Barrier } from "./world.js";

/** One judged action, read back from the run's trace. */
export interface Judged {
  readonly tick: number;
  readonly tool: string;
  readonly verdict: ActionVerdict;
}

/** A guarded turn: the run the control layer recorded, the world it ran in, and the actions as the layer judged them. */
export interface TurnResult {
  readonly request: StudentRequest;
  readonly world: TutorWorld;
  readonly run: AgentRun;
  readonly judged: readonly Judged[];
}

export interface TurnOptions {
  readonly barriers?: Iterable<Barrier>;
  /** The seat; the scripted driver when none is given. */
  readonly seat?: (request: StudentRequest) => Seat;
  readonly maxTicks?: number;
}

/**
 * The layer's verdicts, from the effect rows of a host run ("cell"/"effect"): Acted is Landed; Blocked is a lie when
 * it reads "reported '…' but effect missing", an honest failure otherwise.
 */
export function judgedIn(trace: readonly TraceEvent[]): Judged[] {
  return trace
    .filter((e) => e.level === "cell" && e.setPoint === "effect")
    .map((e) => ({
      tick: e.tick,
      tool: e.action,
      verdict: e.kind === "Acted" ? ActionVerdict.Landed
        : e.detail.includes("but effect missing") ? ActionVerdict.Lied
        : ActionVerdict.HonestFailure,
    }));
}

/**
 * One student turn through creatura-js's control layer, on its host (one tick per model call). The seat's text replies
 * go on the student's screen as they are given.
 */
export async function runTurn(request: StudentRequest, options: TurnOptions = {}): Promise<TurnResult> {
  const world = new TutorWorld(request, options.barriers);
  const inner = (options.seat ?? ((r: StudentRequest) => scriptedDriver(r.message)))(request);
  const seat: Seat = async (call) => {
    const reply = await inner(call);
    if (reply.tool === undefined && reply.text !== undefined) world.say(reply.text);
    return reply;
  };

  const host = new ControlledHost(() => seat, { policyName: "scripted tutor", task: tutorTask(request) });
  const run = await host.run(world, options.maxTicks ?? 40);
  return { request, world, run, judged: judgedIn(run.trace) };
}

/** A turn with no control layer, for comparison: every report is believed. */
export interface UnguardedResult {
  readonly request: StudentRequest;
  readonly world: TutorWorld;
  /** What the loop believed: the facts the tools claimed. */
  readonly believed: ReadonlySet<string>;
  /** The goal, read from what the loop believed. */
  readonly believedGoal: boolean;
}

/**
 * The same seat with nothing between it and the tools: every tool is offered, every claimed fact is believed, and the
 * student sees every text a tool reported as a success (the world's unguarded view). This is the baseline a barrier is
 * measured against, not something a phone would run.
 */
export async function runUnguarded(request: StudentRequest, options: TurnOptions = {}): Promise<UnguardedResult> {
  const world = new TutorWorld(request, options.barriers);
  const seat = (options.seat ?? ((r: StudentRequest) => scriptedDriver(r.message)))(request);
  const believed = new Set(world.facts);
  const task = tutorTask(request);
  const feedback: string[] = [];
  for (let call = 1; call <= (options.maxTicks ?? 12); call++) {
    const reply = await seat({
      round: 1, call, tick: call, instructions: "", prompt: task.goalText, feedback: [...feedback],
      offered: world.availableTools, facts: new Set(believed),
    });
    if (reply.tool === undefined) {
      if (reply.text !== undefined) world.say(reply.text);
      break;
    }
    const outcome = world.act(reply.tool, reply.content);
    if (outcome.succeeded) for (const fact of outcome.granted) believed.add(fact);
    feedback.push(outcome.observation ?? "");
  }

  return { request, world, believed, believedGoal: goal(request.intent)(believed) };
}
