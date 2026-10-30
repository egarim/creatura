// The control layer one component at a time, without a host: Visibility (offer), Gatekeeper (admit), EffectCheck
// (judge) and Ledger (failures, predictions, trace, notes). A port of tests/Creatura.Tests/ControlLayerComponentTests.cs:
// the goldens pin the host's rows; these pin the rules behind them where the host cannot reach.

import assert from "node:assert/strict";
import { test } from "node:test";

import {
  ActionReport,
  ActionVerdict,
  ControlLayer,
  ControlLayerOptions,
  GateDecision,
  HandOffReason,
  HonestFailureRule,
  JudgeRule,
  RefusalReason,
  ResetFailures,
  RestoreServiceDomain,
  SelfModel,
  StopCause,
  TraceKind,
} from "../src/index.js";

function fresh(options?: ControlLayerOptions) {
  const world = RestoreServiceDomain.newWorld();
  const self = new SelfModel({ role: "test operator", principal: "tests", stageLevel: 3 });
  const layer = new ControlLayer(world, RestoreServiceDomain.task, self, options === undefined ? {} : { options });
  layer.beginTick();
  layer.beginRound();
  return { world, layer };
}

const ids = (layer: ControlLayer) => layer.offer().map((t) => t.id);

test("a hidden tool is not offered and is refused on call", () => {
  const { world, layer } = fresh();
  world.revoke("apply-config");
  layer.distrust.judged("apply-patch", ActionVerdict.Lied, layer.tick, false);

  const offered = ids(layer);
  assert.ok(!offered.includes("apply-patch"));
  assert.ok(!offered.includes("apply-config"));
  assert.ok(offered.includes("inspect-logs"));

  const quarantined = layer.admit("apply-patch");
  assert.equal(quarantined.admitted, false);
  assert.equal(quarantined.refusal, RefusalReason.Quarantined);
  assert.match(quarantined.feedback ?? "", /REFUSED/);

  assert.equal(layer.admit("apply-config").refusal, RefusalReason.NotAvailable);

  const unknown = layer.admit("teleport");
  assert.equal(unknown.refusal, RefusalReason.NoSuchTool);
  assert.equal(unknown.tool, undefined);

  // Every refusal spends budget and leaves a blocked row; the round ends once the budget is exceeded.
  assert.equal(layer.failures, 3);
  assert.equal(layer.trace.filter((e) => e.kind === TraceKind.Blocked && e.detail.startsWith("refused: ")).length, 3);
  assert.equal(quarantined.terminate, false);
  assert.equal(unknown.terminate, layer.failures > layer.budget);

  const admitted = layer.admit("inspect-logs");
  assert.equal(admitted.admitted, true);
  assert.equal(admitted.gate, GateDecision.Open);
  assert.equal(admitted.tool!.id, "inspect-logs");
});

test("a release is refused until the record earns it, then the stage advances", () => {
  const { world, layer } = fresh();

  const refused = layer.admit("deploy");
  assert.equal(refused.admitted, false);
  assert.equal(refused.refusal, RefusalReason.ReleaseNotEarned);
  assert.equal(refused.terminate, true);
  assert.notEqual(layer.escalation, undefined);
  assert.ok(layer.trace.some((e) => e.action === "deploy" && e.kind === TraceKind.Escalated && e.detail.startsWith("release requires human approval")));
  assert.equal(layer.failures, 0);
  assert.equal(layer.self.stageLevel, 3);

  // Four correct predictions earn stage 4, and the same release is admitted with the stage advanced.
  for (const id of ["inspect-logs", "identify-component", "apply-patch", "run-tests"]) {
    const outcome = world.act(id);
    const judgement = layer.judge(world.tool(id)!, new ActionReport(outcome.observation ?? "ok", outcome));
    assert.equal(judgement.verdict, ActionVerdict.Landed);
  }

  const admitted = layer.admit("deploy");
  assert.equal(admitted.admitted, true);
  assert.equal(admitted.gate, GateDecision.Advanced);
  assert.equal(layer.self.stageLevel, 4);
  assert.ok(layer.notes.some((n) => n.startsWith("stage advance: 4")));
  assert.equal(admitted.tool!.id, "deploy");
});

test("an honest failure is not a lie under the host's rules", () => {
  const { world, layer } = fresh();
  const patch = world.tool("apply-patch")!;

  // The tool said it failed and nothing landed: budget and reliability are spent, the tool stays visible.
  const honest = layer.judge(patch, new ActionReport("failed", { tool: "apply-patch", succeeded: false, granted: [], observation: "preconditions unmet" }));
  assert.equal(honest.verdict, ActionVerdict.HonestFailure);
  assert.equal(honest.landed, false);
  assert.deepEqual(honest.missing, ["patch.applied"]);
  assert.equal(layer.failures, 1);
  assert.equal(layer.predictions.total, 1);
  assert.equal(layer.self.reliability("apply-patch"), 0.0);
  assert.ok(ids(layer).includes("apply-patch"));
  assert.ok(layer.trace.some((e) => e.action === "apply-patch" && e.kind === TraceKind.Blocked && e.detail.startsWith("failed honestly")));
  assert.ok(!layer.notes.some((n) => n.includes("distrusted")));

  // The tool said it succeeded and nothing landed: a lie, quarantined, with the warning the seat reads.
  const lie = layer.judge(patch, new ActionReport("ok", { tool: "apply-patch", succeeded: true, granted: [], observation: "reported success" }));
  assert.equal(lie.verdict, ActionVerdict.Lied);
  assert.equal(layer.failures, 2);
  assert.ok(!ids(layer).includes("apply-patch"));
  assert.ok(layer.notes.some((n) => n.includes("apply-patch: claimed 'ok', effect missing — distrusted")));
  assert.ok(lie.feedback.startsWith("WARNING"));
  assert.equal(layer.audit.contradictions.length, 1);

  // A report the host never saw counts as a claim of success.
  const unseen = layer.judge(world.tool("run-tests")!, new ActionReport(""));
  assert.equal(unseen.verdict, ActionVerdict.Lied);
});

test("the designs' rules habituate an honest failure, and the two-way judge calls it a lie", () => {
  const habituate = fresh(ControlLayerOptions.with({ honestFailureRule: HonestFailureRule.Habituate }));
  const honest = habituate.layer.judge(habituate.world.tool("apply-patch")!,
    new ActionReport("failed", { tool: "apply-patch", succeeded: false, granted: [], observation: "preconditions unmet" }));
  assert.equal(honest.verdict, ActionVerdict.HonestFailure);
  assert.equal(honest.terminate, false);
  assert.equal(habituate.layer.failures, 0);
  assert.equal(habituate.layer.predictions.total, 0);
  assert.ok(!ids(habituate.layer).includes("apply-patch"));
  assert.ok(habituate.layer.trace.some((e) => e.action === "apply-patch" && e.detail === "reported failure, nothing landed (honest)"));

  const twoWay = fresh(ControlLayerOptions.with({ judgeRule: JudgeRule.TwoWay }));
  const lie = twoWay.layer.judge(twoWay.world.tool("apply-patch")!,
    new ActionReport("failed", { tool: "apply-patch", succeeded: false, granted: [], observation: "preconditions unmet" }));
  assert.equal(lie.verdict, ActionVerdict.Lied);
  assert.ok(!ids(twoWay.layer).includes("apply-patch"));
});

test("failures reset per round for the host, and never for the designs", () => {
  const perRound = fresh();
  perRound.layer.admit("teleport");
  perRound.layer.admit("teleport");
  assert.equal(perRound.layer.failures, 2);
  perRound.layer.beginRound();
  assert.equal(perRound.layer.failures, 0);

  const never = fresh(ControlLayerOptions.with({ resetFailures: ResetFailures.Never }));
  never.layer.admit("teleport");
  never.layer.admit("teleport");
  never.layer.beginRound();
  assert.equal(never.layer.failures, 2);
});

test("close reads the goal from the registry, and hands off on no route only when nothing is quarantined", () => {
  const { world, layer } = fresh();

  // Something quarantined: no route is a wait, not a hand-off.
  layer.distrust.judged("apply-patch", ActionVerdict.Lied, layer.tick, false);
  const waiting = layer.close("NO PATH", true);
  assert.equal(waiting.continue, true);
  assert.ok(layer.notes.some((n) => n.startsWith("no path right now")));

  // Nothing quarantined: the goal is unregulatable with these tools.
  layer.distrust.clear();
  const handedOff = layer.close("NO PATH", true);
  assert.equal(handedOff.continue, false);
  assert.equal(handedOff.stop!.cause, StopCause.NoPath);
  assert.equal(handedOff.stop!.handOff!.reason, HandOffReason.NoPath);
  assert.ok(layer.trace.some((e) => e.action === "no path" && e.kind === TraceKind.Escalated));

  // The colony's option: no route lifts the quarantine instead of waiting it out.
  const clearing = fresh(ControlLayerOptions.with({ clearQuarantineOnNoRoute: true }));
  clearing.layer.distrust.judged("apply-patch", ActionVerdict.Lied, clearing.layer.tick, false);
  assert.equal(clearing.layer.close("NO PATH", true).continue, true);
  assert.ok(ids(clearing.layer).includes("apply-patch"));

  // Success is a predicate over the world, never the seat's text.
  for (const id of ["inspect-logs", "identify-component", "apply-patch", "run-tests", "deploy", "resolve-alert"]) world.act(id);
  const met = layer.close("I am done", false);
  assert.equal(met.goalReached, true);
  assert.equal(met.stop!.cause, StopCause.GoalMet);
  assert.ok(layer.trace.some((e) => e.action === "contract met" && e.kind === TraceKind.Acted));
});

test("the quarantine lapses after its cooldown", () => {
  const { layer } = fresh();
  layer.distrust.judged("apply-patch", ActionVerdict.Lied, layer.tick, false);
  for (let i = 0; i < 2; i++) {
    layer.beginTick();
    assert.ok(!ids(layer).includes("apply-patch"), `tick ${layer.tick}`);
  }

  layer.beginTick();
  assert.ok(ids(layer).includes("apply-patch"));
});

test("rounds that do not move the gap are handed to a human", () => {
  const { layer } = fresh();
  assert.equal(layer.stop(), undefined);
  layer.beginRound();
  layer.beginRound();
  const stop = layer.stop();
  assert.equal(stop?.cause, StopCause.StuckRegulation);
  assert.equal(stop?.handOff?.reason, HandOffReason.StuckRegulation);
  assert.ok(layer.trace.some((e) => e.action === "stuck regulation" && e.detail === "2 rounds without progress at 65% freedom"));
});

test("the action observers hear every judged action, after the record", () => {
  const { world, layer } = fresh();
  const heard: string[] = [];
  const stop = layer.onActionObserved((o) => heard.push(`${o.tick} ${o.tool.id} ${o.verdict} ${o.landed} ${layer.predictions.total}`));
  const outcome = world.act("inspect-logs");
  layer.judge(world.tool("inspect-logs")!, ActionReport.of(outcome));
  stop();
  layer.judge(world.tool("identify-component")!, new ActionReport(""));
  assert.deepEqual(heard, ["1 inspect-logs Landed true 1"]);
});
