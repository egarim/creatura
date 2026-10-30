import type { ControlLayer } from "./control-layer.js";
import { TraceText } from "./trace-text.js";
import { ActionVerdict, type GateDecision, type RefusalReason, TraceKind } from "./types.js";
import type { ActionReport } from "./verbs.js";
import type { Tool } from "./world.js";

/**
 * How the control layer's verbs render what they decide: the rows, the notes and the audit evidence that `admit` and
 * `judge` write. The rules are the layer's; the words are the frame's. {@link HostFrame} is how the host has always
 * written them.
 */
export interface IControlFrame {
  /** The stage gate for a tool. When absent, the layer's own stage interlock decides (C#'s default method). */
  gate?(layer: ControlLayer, tool: Tool): GateDecision;
  /** A tool the seat named was not in the visible set. */
  refused(layer: ControlLayer, toolId: string, why: RefusalReason): void;
  /** The record earned the next stage on the way to a release. */
  stageAdvanced(layer: ControlLayer): void;
  /** A release the record does not support. */
  releaseRefused(layer: ControlLayer, tool: Tool): void;
  /** What the coherence audit records as evidence against a tool that reported success without effect. */
  evidence(layer: ControlLayer, tool: Tool, report: ActionReport): string;
  /**
   * An action was judged. `landed` is the registry's answer, `verdict` the verdict after the re-judge chain;
   * `habituated`: an honest failure under the habituate rule, quarantined and not recorded.
   */
  acted(layer: ControlLayer, tool: Tool, report: ActionReport, verdict: ActionVerdict, landed: boolean, habituated: boolean): void;
}

/** The host's rendering: the rows and notes the control layer on the host has always written. */
export class HostFrame implements IControlFrame {
  static readonly instance = new HostFrame();

  refused(layer: ControlLayer, toolId: string, why: RefusalReason): void {
    layer.record("organism", "visibility", 1.0, toolId, TraceKind.Blocked, TraceText.refused(why));
  }

  stageAdvanced(layer: ControlLayer): void {
    layer.note(TraceText.stageAdvance(layer.self.stage, layer.predictions.recentAccuracy));
  }

  releaseRefused(layer: ControlLayer, tool: Tool): void {
    layer.escalate(TraceText.releaseRequiresApproval(layer.self.stage, layer.predictions));
    layer.record("organism", "stage", 1.0, tool.id, TraceKind.Escalated, layer.escalation!);
  }

  evidence(_layer: ControlLayer, _tool: Tool, report: ActionReport): string {
    return `reported '${report.summary}' with no effect`;
  }

  acted(layer: ControlLayer, tool: Tool, report: ActionReport, verdict: ActionVerdict, _landed: boolean, habituated: boolean): void {
    if (verdict === ActionVerdict.Landed) {
      layer.record("cell", "effect", 0.0, tool.id, TraceKind.Acted, TraceText.effectVerified(layer.predictions.recentAccuracy));
    } else if (verdict === ActionVerdict.HonestFailure && habituated) {
      layer.record("cell", "effect", 0.0, tool.id, TraceKind.Blocked, TraceText.HonestFailure);
    } else if (verdict === ActionVerdict.HonestFailure) {
      layer.record("cell", "effect", 1.0, tool.id, TraceKind.Blocked, TraceText.failedHonestly(report.summary));
    } else {
      layer.record("cell", "effect", 1.0, tool.id, TraceKind.Blocked, TraceText.reportedButMissing(report.summary));
      layer.note(TraceText.distrusted(tool.id, report.summary, layer.options.cooldownTicks));
    }
  }
}
