import { ControlLayer, ControlLayerOptions } from "./control-layer.js";
import { containsIgnoreCase, formatFixed, formatPercent, trimNet } from "./format.js";
import { Planner } from "./planner.js";
import { RestoreServiceDomain } from "./restore.js";
import { SelfModel } from "./self-model.js";
import type { AgentTask } from "./task.js";
import type { AgentRun } from "./trace.js";
import { TraceText } from "./trace-text.js";
import { TraceKind } from "./types.js";
import { ActionReport } from "./verbs.js";
import { orderedFacts, type IWorld, type Tool } from "./world.js";

/**
 * What the seat (the model) is handed at one call: the round, the call within it (one call is one tick), the
 * instructions and the round's prompt, what it was told after each call so far this round, the tools offered after
 * visibility, and the facts.
 */
export interface SeatCall {
  readonly round: number;
  readonly call: number;
  readonly tick: number;
  readonly instructions: string;
  readonly prompt: string;
  readonly feedback: readonly string[];
  readonly offered: readonly Tool[];
  readonly facts: ReadonlySet<string>;
}

/** What the seat answers: one tool call (with its payload, when the tool needs one), or text and no call. */
export interface SeatReply {
  readonly tool?: string | undefined;
  readonly text?: string | undefined;
  readonly content?: string | undefined;
}

/** The model's seat. A promise when the model is a real one; the planner answers at once. */
export type Seat = (call: SeatCall) => SeatReply | Promise<SeatReply>;

export interface ControlledHostOptions {
  /** The seat's name in the run's first note ("policy bfs"). */
  readonly policyName: string;
  /** The task; the restore domain's when none is given. */
  readonly task?: AgentTask;
  /** The run's agent name. The C# host calls itself "Controlled (AF)". */
  readonly name?: string;
  /** The framework in the run's first note. The C# host writes "Microsoft.Agents.AI". */
  readonly framework?: string;
}

/** The host's instruction to the model when nothing it is offered helps; `close` reads it as no route. */
export const NoPathReply = "NO PATH";

/** The instructions the C# host gives the model, word for word. */
export const Instructions = [
  "You are the decision component of an operations agent. You act only through the tools offered to you.",
  "Call one tool at a time. Each tool says what it requires and what it grants.",
  "After every call you will be told whether the effect was verified. Trust that verdict, not the tool's",
  "own report. If a tool is refused or its effect did not land, take a different route.",
  "Never claim the goal is done; the system checks that itself. When no offered tool can make progress,",
  "reply exactly: NO PATH.",
].join("\n");

/** A tool as the model reads it: "Requires: … Grants: … Kind: …". */
export function describeTool(tool: Tool): string {
  const requires = tool.requires.length === 0 ? "nothing" : tool.requires.join(", ");
  const payload = tool.needsPayload ? ` Payload (content): ${tool.payloadHint ?? "required"}.` : "";
  return `Requires: ${requires}. Grants: ${tool.grants.join(", ")}. Kind: ${tool.kind}.${payload}`;
}

/** The breadth-first planner in the model's seat, over exactly the tools the call offers (C#'s `PlannerChatClient`). */
export function plannerSeat(world: IWorld, task: AgentTask): Seat {
  return (call) => {
    const offered = new Set(call.offered.map((t) => t.id));
    // BFS has no generator, so tools that need a payload are out of its reach.
    const tools = world.allTools.filter((t) => offered.has(t.id) && !t.needsPayload);
    const plan = Planner.find(world.facts, tools, task.goal, 8, task.habit);
    if (plan === undefined) return { text: "NO PATH: no offered tool can make progress toward the goal." };
    if (plan.length === 0) return { text: "The goal is already met." };
    return { tool: plan[0] };
  };
}

/**
 * The control layer's host: one tick is one model call. It hands the layer the verbs the C# Agent Framework host hands
 * it, in the same order (src/Creatura.Hosts.AgentFramework/ControlledAgent.cs, and tests/Creatura.Tests/FakeHost.cs,
 * which does the same with no framework). Per round: `beginRound`, `stop`, `assemble`, then model calls until one
 * answers with text or a verb says terminate; per model call: `beginTick` and `offer` (the tools the model is shown);
 * per tool call: `admit`, the world's `act` with `reportOutcome`, `judge`; per reply: `close`; at the end, `sleep`.
 *
 * What it does as the framework does, without its code: after every judged call the seat is called again unless a
 * verb said terminate; a seat that names a tool the world does not have gets "Error: function … not found." with no
 * verb called; a round gives up after {@link ControlledHost.maximumCallsPerRound} calls. The designs' extensions are
 * not ported, so their hooks (before the run, the re-judge chain, the policy notes, their sleep) are not here.
 */
export class ControlledHost {
  static readonly maximumCallsPerRound = 40;
  static readonly maximumRounds = 6;

  private readonly _seatFactory: (world: IWorld, task: AgentTask) => Seat;
  private readonly _policyName: string;
  private readonly _task: AgentTask;
  private readonly _framework: string;
  readonly name: string;

  constructor(seatFactory: (world: IWorld, task: AgentTask) => Seat, options: ControlledHostOptions) {
    this._seatFactory = seatFactory;
    this._policyName = options.policyName;
    this._task = options.task ?? RestoreServiceDomain.task;
    this.name = options.name ?? "Controlled (JS)";
    this._framework = options.framework ?? "creatura-js";
  }

  /** Offline: breadth-first search in the model's seat. */
  static offline(options?: Omit<ControlledHostOptions, "policyName">): ControlledHost {
    return new ControlledHost(plannerSeat, { ...options, policyName: "bfs" });
  }

  async run(world: IWorld, maxTicks = 40): Promise<AgentRun> {
    const task = this._task;
    const self = new SelfModel({ role: "controlled operator", principal: "joche", stageLevel: 3 });
    self.openCommitments.push(task.name);
    const control = new ControlLayer(world, task, self, { options: ControlLayerOptions.Host });
    control.note(`policy ${this._policyName}; framework ${this._framework}`);

    const seat = this._seatFactory(world, task);
    let goalReached = false;
    const maxRounds = ControlledHost.maximumRounds;

    for (let round = 1; round <= maxRounds && control.tick < maxTicks; round++) {
      control.beginRound();

      // Stuck regulation: rounds that act without moving the gap are handed to a human.
      if (control.stop() !== undefined) break;

      // The self-model is read before acting: it is part of the message, every round.
      const record = control.assemble(round);
      const facts = world.facts.size === 0 ? "none" : orderedFacts(world.facts).join(", ");
      const prompt = [
        `Goal (checked by the system, not by you): ${task.goalText}`,
        `Facts that hold now: ${facts}`,
        `Progress toward the goal: ${formatPercent(control.progress)}.`,
        `Round ${round} of ${maxRounds}; freedom ${formatFixed(control.freedom, 2)}; failures tolerated this round: ${control.budget}.`,
        "",
        "Your record (read it before acting):",
        record,
        "",
        "Make progress with the offered tools.",
      ].join("\n");

      let text: string;
      try {
        text = trimNet(await this.round(world, control, seat, round, prompt));
      } catch (error) {
        // The model call itself failed: that is the host's to report, not the layer's.
        const name = error instanceof Error ? error.name : "Error";
        const message = error instanceof Error ? error.message : String(error);
        control.record("organism", "contract", 1.0 - control.progress, TraceText.ModelError, TraceKind.Escalated, `${name}: ${message}`);
        control.note("escalated: the model call failed; the goal is unregulatable from here");
        break;
      }

      // Success is a predicate over the world, never the model's text; the layer reads it, then the escalations,
      // then the reply. "NO PATH" is this host's instruction to the model (see Instructions).
      const closure = control.close(text, containsIgnoreCase(text, NoPathReply));
      goalReached = closure.goalReached;
      if (!closure.continue) break;
    }

    control.sleep();
    return { agent: this.name, goalReached, ticks: control.tick, trace: [...control.trace], notes: [...control.notes] };
  }

  /** One round's function-calling loop. Returns the reply's text: the model's words, or empty when a verb ended the round. */
  private async round(world: IWorld, control: ControlLayer, seat: Seat, round: number, prompt: string): Promise<string> {
    const feedback: string[] = [];
    for (let call = 1; call <= ControlledHost.maximumCallsPerRound; call++) {
      control.beginTick();
      const offered = control.offer();
      const reply = await seat({
        round, call, tick: control.tick, instructions: Instructions, prompt,
        feedback: [...feedback], offered, facts: new Set(world.facts),
      });
      if (reply.tool === undefined) return reply.text ?? "";
      const toolId = reply.tool;

      if (world.tool(toolId) === undefined) {
        feedback.push(`Error: function ${toolId} not found.`);
        continue;
      }

      const admission = control.admit(toolId);
      if (!admission.admitted) {
        feedback.push(admission.feedback ?? "");
        if (admission.terminate) return "";
        continue;
      }

      const tool = admission.tool!;
      const outcome = tool.needsPayload ? world.act(tool.id, reply.content) : world.act(tool.id);
      control.reportOutcome(outcome);
      const reported = outcome.observation ?? (outcome.succeeded ? "ok" : "failed");

      const judgement = control.judge(tool, new ActionReport(reported, control.lastOutcome));
      feedback.push(judgement.feedback);
      if (judgement.terminate) return "";
    }

    return "";
  }
}
