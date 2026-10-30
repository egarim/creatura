// creatura-js: Creatura's control layer in TypeScript. A port of Creatura.Core's control layer, the restore world and
// its planner, kept identical to the C# by replaying the C# host's golden traces (test/golden.test.ts).

export { ActionVerdict, GateDecision, HandOff, HandOffReason, RefusalReason, StopCause, TraceKind } from "./types.js";
export { tool, worldVerdict, orderedFacts, type ActionOutcome, type IWorld, type IWorldView, type Tool } from "./world.js";
export { CatalogueWorld, FaultKind, FaultRule, World } from "./catalogue-world.js";
export { Budget, Plasticity, Stages, type Stage } from "./stages.js";
export { SelfModel, SkillBelief, type SelfModelSnapshot } from "./self-model.js";
export {
  CoherenceAudit,
  PredictionTracker,
  type CoherenceSnapshot,
  type Contradiction,
  type PredictionSnapshot,
} from "./records.js";
export { Quarantine, StageInterlock, type IDistrust } from "./quarantine.js";
export { agentTask, type AgentTask } from "./task.js";
export {
  ActionReport,
  Admission,
  closure,
  type ActionObservation,
  type Closure,
  type EffectJudgement,
  type StopReason,
} from "./verbs.js";
export { readJsonl, sameEvent, writeJsonl, jsonString, type AgentRun, type TraceEvent } from "./trace.js";
export { TraceText } from "./trace-text.js";
export { HostFrame, type IControlFrame } from "./control-frame.js";
export {
  ControlLayer,
  ControlLayerOptions,
  HonestFailureRule,
  JudgeRule,
  ResetFailures,
  type ControlLayerInit,
} from "./control-layer.js";
export { Planner } from "./planner.js";
export { RestoreServiceDomain } from "./restore.js";
export {
  ControlledHost,
  Instructions,
  NoPathReply,
  describeTool,
  plannerSeat,
  type ControlledHostOptions,
  type Seat,
  type SeatCall,
  type SeatReply,
} from "./host.js";
export {
  clamp,
  containsIgnoreCase,
  formatFixed,
  formatJsonNumber,
  formatPercent,
  ordinal,
  replaceLineEndings,
  roundHalfEven,
  trimNet,
} from "./format.js";
