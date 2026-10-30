// The restore world, its barriers and the host's paths the goldens never take.

import assert from "node:assert/strict";
import { test } from "node:test";

import { ControlledHost, FaultRule, Planner, RestoreServiceDomain, TraceKind, World, tool } from "../src/index.js";
import type { SeatReply } from "../src/index.js";

test("act refuses in order: unknown tool, revoked, preconditions unmet; every call is a tick", () => {
  const world = RestoreServiceDomain.newWorld();
  world.revoke("apply-config");
  assert.equal(world.act("teleport").observation, "unknown tool");
  assert.equal(world.act("apply-config").observation, "tool unavailable");
  assert.equal(world.act("deploy").observation, "preconditions unmet: tests.green");
  assert.equal(world.ticks, 3);
  assert.ok(world.allTools.some((t) => t.id === "apply-config"));
  assert.ok(!world.availableTools.some((t) => t.id === "apply-config"));
});

test("a liar lies once: success reported, nothing granted, then it lands", () => {
  const world = RestoreServiceDomain.newWorld();
  world.makeLiar("apply-patch");
  world.makeLiar("apply-patch");
  assert.equal(world.faults.length, 1);
  world.act("inspect-logs");
  world.act("identify-component");
  const lie = world.act("apply-patch");
  assert.deepEqual([lie.succeeded, lie.granted, lie.observation, world.has("patch.applied")], [true, [], "reported success", false]);
  const second = world.act("apply-patch");
  assert.deepEqual([second.succeeded, second.granted, world.has("patch.applied")], [true, ["patch.applied"], true]);
});

test("an honest failure holds until its fact, and a clone is independent", () => {
  const world = new World([tool("b", [], ["done"], "write"), tool("a", [], ["fixed"], "write")], []);
  assert.deepEqual(world.allTools.map((t) => t.id), ["a", "b"]);
  world.inject(FaultRule.honestFailure("b", "broken", "fixed"));
  assert.deepEqual([world.act("b").succeeded, world.act("b").observation], [false, "broken"]);
  const copy = world.clone();
  copy.act("a");
  assert.equal(copy.act("b").succeeded, true);
  assert.equal(world.has("fixed"), false);
  assert.equal(world.act("b").succeeded, false);
});

test("the planner prefers the habit, then goes by id", () => {
  const world = RestoreServiceDomain.newWorld();
  const task = RestoreServiceDomain.task;
  assert.deepEqual(Planner.findIn(world, task.goal, 8, undefined, task.habit),
    ["inspect-logs", "identify-component", "apply-patch", "run-tests", "deploy", "resolve-alert"]);
  assert.deepEqual(Planner.findIn(world, task.goal, 8, new Set(["apply-patch"]))?.[2], "apply-config");
  assert.equal(Planner.findIn(world, task.goal, 5), undefined);
});

test("the host: an unknown tool is answered without a verb, and a seat that throws is a model error", async () => {
  const calls: string[][] = [];
  const replies: SeatReply[] = [{ tool: "teleport" }, { text: "  NO PATH  " }];
  const host = new ControlledHost(() => (call) => {
    calls.push([...call.feedback]);
    return replies.shift() ?? { text: "" };
  }, { policyName: "script" });
  const run = await host.run(RestoreServiceDomain.newWorld(), 30);
  assert.deepEqual(calls, [[], ["Error: function teleport not found."]]);
  assert.equal(run.ticks, 2);
  assert.ok(run.trace.some((e) => e.action === "reply" && e.detail === "NO PATH"));
  assert.ok(run.trace.some((e) => e.action === "no path" && e.kind === TraceKind.Escalated));

  const failing = new ControlledHost(() => () => {
    throw new TypeError("the model is gone");
  }, { policyName: "broken" });
  const failed = await failing.run(RestoreServiceDomain.newWorld(), 30);
  assert.ok(failed.trace.some((e) => e.action === "model error" && e.detail === "TypeError: the model is gone"));
  assert.equal(failed.notes[1], "escalated: the model call failed; the goal is unregulatable from here");
});

test("the host: a refused release ends the round and leaves it to a human", async () => {
  const replies: SeatReply[] = [{ tool: "inspect-logs" }, { tool: "identify-component" }, { tool: "apply-patch" },
    { tool: "run-tests" }, { tool: "deploy" }];
  const host = new ControlledHost(() => () => replies.shift() ?? { text: "" }, { policyName: "script" });
  // With three landed calls the record has not earned stage 4 (four are needed), so take one away.
  replies.splice(0, 1);
  const world = RestoreServiceDomain.newWorld();
  world.act("inspect-logs");
  const run = await host.run(world, 30);
  assert.equal(run.goalReached, false);
  assert.ok(run.trace.some((e) => e.action === "deploy" && e.kind === TraceKind.Escalated));
  assert.ok(run.notes.some((n) => n.startsWith("escalated: release requires human approval: stage 3 (social self), recent predictions 100% over 3")));
});
