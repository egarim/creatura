import { formatPercent } from "./format.js";
import type { PredictionTracker } from "./records.js";
import type { Stage } from "./stages.js";
import { RefusalReason } from "./types.js";

/**
 * The pinned trace strings the control layer and the host frame write: the action names, detail fragments and note
 * prefixes that readers parse. Every string is exactly what C#'s `TraceText` writes, numbers in its culture
 * ("86%"). The designs' own strings (the Bach agent's, the hybrid's, the colony's) are not ported with them.
 */
export const TraceText = {
  // Action column.
  ContractMet: "contract met",
  HandOff: "hand off",
  NoPath: "no path",
  NoAction: "no action",
  StuckRegulation: "stuck regulation",
  Sleep: "sleep",
  Round: "round",
  Reply: "reply",
  ModelError: "model error",

  // Detail column.
  complete(taskName: string): string {
    return taskName + " complete";
  },

  /** The honest-failure row every design writes. */
  HonestFailure: "reported failure, nothing landed (honest)",

  /** The host's refusal row: "refused: {why}". */
  refused(reason: RefusalReason): string {
    return `refused: ${TraceText.why(reason)}`;
  },

  /** The host's words for a refusal, as the model reads them. */
  why(reason: RefusalReason): string {
    switch (reason) {
      case RefusalReason.NoSuchTool: return "no such tool";
      case RefusalReason.Quarantined: return "distrusted after a failed effect, not available yet";
      case RefusalReason.NotAvailable: return "not available to this agent";
      case RefusalReason.NotVisible: return "not visible";
      case RefusalReason.ReleaseNotEarned: return "a release action this agent has not earned yet";
      default: return String(reason);
    }
  },

  /** The host's landed row. */
  effectVerified(recentAccuracy: number): string {
    return `effect verified (${formatPercent(recentAccuracy)} recent)`;
  },

  /** The host's honest-failure row. */
  failedHonestly(summary: string): string {
    return `failed honestly: ${summary}`;
  },

  /** The host's lie row. */
  reportedButMissing(summary: string): string {
    return `reported '${summary}' but effect missing — distrusted`;
  },

  // Notes.
  /** The host's stage note. */
  stageAdvance(stage: Stage, recentAccuracy: number): string {
    return `stage advance: ${stage.level} (${stage.name}) after ${formatPercent(recentAccuracy)} recent prediction accuracy`;
  },

  /** The host's refusal of a release, note and row alike. */
  releaseRequiresApproval(stage: Stage, predictions: PredictionTracker): string {
    return (
      `release requires human approval: stage ${stage.level} (${stage.name}), ` +
      `recent predictions ${formatPercent(predictions.recentAccuracy)} over ${predictions.total}`
    );
  },

  /** The host's note when a tool lied. */
  distrusted(toolId: string, summary: string, cooldownTicks: number): string {
    return `${toolId}: claimed '${summary}', effect missing — distrusted for ${cooldownTicks} ticks`;
  },
} as const;
