// The tutor's world: its tools, the app's checks, and what reaches the registry and the student.

import assert from "node:assert/strict";
import { test } from "node:test";

import {
  Answer, Barrier, Checks, Fact, Split, ToolId, TutorTools, TutorWorld, Voice, problem, request, tutorTools,
} from "../src/index.js";

const p = problem(4); // div-pasteles: 43 huevos, 4 por pastel: 10 y sobran 3
const help = request(p, Voice.Help);
const right = request(p, Voice.Right);
const wrong = request(p, Voice.Wrong);

test("four tools in Creatura's terms", () => {
  assert.deepEqual(tutorTools().map((t) => [t.id, t.requires, t.grants, t.kind]), [
    ["read_problem", [], ["problem-known"], "read"],
    ["check_answer", ["problem-known", "attempt-given"], ["attempt-checked"], "verify"],
    ["next_hint", ["problem-known"], ["hint-given"], "teach"],
    ["confirm_correct", ["attempt-right"], ["correct-confirmed"], "close"],
  ]);
  assert.equal(tutorTools().find((t) => t.id === ToolId.CheckAnswer)?.needsPayload, true);
  assert.equal(p.split, Split.Test);
});

test("act refuses in order: unknown tool, preconditions unmet; every call is a tick; an attempt opens with attempt-given", () => {
  const world = new TutorWorld(help);
  assert.equal(world.act("teleport").observation, "unknown tool");
  assert.equal(world.act(ToolId.NextHint).observation, "preconditions unmet: problem-known");
  world.act(ToolId.ReadProblem);
  assert.equal(world.act(ToolId.CheckAnswer, "10 3").observation, "preconditions unmet: attempt-given");
  assert.equal(world.ticks, 4);
  assert.deepEqual([...new TutorWorld(right).facts], [Fact.AttemptGiven]);
});

test("the checker lands the exact verdict with attempt-checked, and the student is told it without the number", () => {
  const ok = new TutorWorld(right);
  ok.act(ToolId.ReadProblem);
  const outcome = ok.act(ToolId.CheckAnswer, "10 3");
  assert.deepEqual([outcome.succeeded, ok.has(Fact.AttemptChecked), ok.has(Fact.AttemptRight), ok.has(Fact.AttemptWrong)], [true, true, true, false]);
  assert.equal(ok.act(ToolId.ConfirmCorrect).succeeded, true);
  assert.ok(ok.has(Fact.CorrectConfirmed));

  const no = new TutorWorld(wrong);
  no.act(ToolId.ReadProblem);
  no.act(ToolId.CheckAnswer, "11 0");
  assert.deepEqual([no.has(Fact.AttemptChecked), no.has(Fact.AttemptRight), no.has(Fact.AttemptWrong)], [true, false, true]);
  assert.equal(no.act(ToolId.ConfirmCorrect).observation, "preconditions unmet: attempt-right");
  assert.deepEqual(no.shown, ["Revisé tu respuesta: todavía no es correcta."]);
});

test("the checker must check the student's own attempt: another number lands nothing, no number fails honestly", () => {
  const world = new TutorWorld(wrong);
  world.act(ToolId.ReadProblem);
  // The student wrote "11 y sobran 0". The right answer, or another wrong one, is not what the student was told about.
  for (const payload of ["10 3", "9 9"]) {
    const other = world.act(ToolId.CheckAnswer, payload);
    assert.deepEqual([other.succeeded, world.has(Fact.AttemptChecked)], [true, false], payload);
  }
  assert.ok(Answer.equals(wrong.attempt!, Answer.division(11, 0)));
  const none = world.act(ToolId.CheckAnswer, "no sé");
  assert.deepEqual([none.succeeded, none.observation], [false, "No encontré una respuesta que revisar."]);
});

test("hints come gentlest first, never contain the answer, and a later hint is judged on its own", () => {
  const world = new TutorWorld(help);
  world.act(ToolId.ReadProblem);
  world.act(ToolId.NextHint);
  world.act(ToolId.NextHint);
  world.act(ToolId.NextHint);
  assert.deepEqual(world.shown, p.hints.map((h) => `Pista: ${h}`).concat([`Pista: ${p.hints[1]}`]));
  assert.ok(world.has(Fact.HintGiven));

  // A clean hint, then the barrier: the act retracts hint-given, and the leak does not put it back.
  const leaky = new TutorWorld(help);
  leaky.act(ToolId.ReadProblem);
  leaky.act(ToolId.NextHint);
  assert.ok(leaky.has(Fact.HintGiven));
  leaky.switchOn(Barrier.HintLeaks);
  const leak = leaky.act(ToolId.NextHint);
  assert.ok(leak.succeeded && Checks.revealsAnswer(p, leak.observation!));
  assert.equal(leaky.has(Fact.HintGiven), false);
  assert.equal(leaky.effectsLanded(tutorTools().find((t) => t.id === ToolId.NextHint)!), false);
  assert.deepEqual(leaky.shown, [`Pista: ${p.hints[0]}`]);
  assert.equal(leaky.shownUnguarded.length, 2);
});

test("the app's checks: the leak check sees the answer in any form, and only the answer", () => {
  const fraction = problem(7); // frac-cinta
  assert.equal(fraction.template, "frac-cinta");
  const value = fraction.answer.kind === "number" ? fraction.answer.value : undefined;
  for (const form of [value!.toString(), value!.toMixed(), value!.toDecimal() ?? value!.toString()]) {
    assert.ok(Checks.revealsAnswer(fraction, `Cada trozo mide ${form} metros.`), form);
  }
  assert.ok(!Checks.revealsAnswer(fraction, fraction.statement));
  assert.ok(Checks.revealsAnswer(p, "Sobran 3."));
  assert.ok(!Checks.hintSafe(p, "   "));
  assert.ok(Checks.problemShown(p, TutorTools.readProblem({ problem: p, hintsGiven: 0 }).text));
});

test("the model's own words reach the student unchecked; a number of the answer in them raises answer-shown", () => {
  const world = new TutorWorld(help);
  world.say("Piensa en grupos de 4.");
  assert.equal(world.has(Fact.AnswerShown), false);
  world.say("Son 10 pasteles.");
  assert.equal(world.has(Fact.AnswerShown), true);
});
