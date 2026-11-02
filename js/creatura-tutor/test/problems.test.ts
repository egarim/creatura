// The problems and the requests: fixed by their seeds, split by seed before anything else, each answer exact.

import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { test } from "node:test";

import {
  Answer, Rational, Split, Voice, mentions, numberTokens, problem, problemSeeds, problems, request, requests, splitOf, templates,
} from "../src/index.js";

const json = (v: unknown) => JSON.stringify(v, (_k, x) => (typeof x === "bigint" ? x.toString() : x));

test("100 problems, split by seed: 67 train, 9 valid, 24 test, and the test seeds are pinned", () => {
  assert.equal(problemSeeds.length, 100);
  assert.deepEqual([problems(Split.Train).length, problems(Split.Valid).length, problems(Split.Test).length], [67, 9, 24]);
  assert.deepEqual(problems(Split.Test).map((p) => p.seed),
    [4, 5, 9, 11, 13, 17, 22, 24, 25, 34, 49, 51, 59, 61, 62, 63, 75, 78, 79, 80, 86, 89, 90, 92]);
  for (const p of problems()) assert.equal(p.split, splitOf(p.seed));
});

test("the split is a hash of the seed alone, 7 in 10 train, 1 valid, 2 test over many seeds", () => {
  // The 100 problems fall 67/9/24; the rule itself is 70/10/20, and adding seeds moves none that exist.
  const counts = { train: 0, valid: 0, test: 0 };
  for (let s = 0; s < 10000; s++) counts[splitOf(s)]++;
  assert.ok(Math.abs(counts.train - 7000) < 200 && Math.abs(counts.valid - 1000) < 100 && Math.abs(counts.test - 2000) < 150,
    JSON.stringify(counts));
});

test("the problem set is fixed by its seeds: generated twice, the same; its digest is pinned", () => {
  assert.equal(json(problems()), json(problems()));
  const digest = createHash("sha256").update(json(requests())).digest("hex").slice(0, 16);
  // A template or generator change moves this on purpose; update it in the same commit and say why.
  assert.equal(digest, PINNED_DIGEST);
});

test("every topic and template is in the test split", () => {
  const test = problems(Split.Test);
  assert.deepEqual([...new Set(test.map((p) => p.topic))].sort(), ["division", "fractions", "multiplication", "units"]);
  assert.equal(new Set(test.map((p) => p.template)).size, templates.length);
});

test("each problem is sound: the wrong answer is wrong, and no number of the answer is in the statement or a hint", () => {
  for (const p of problems()) {
    assert.ok(!Answer.equals(p.answer, p.wrong), p.id);
    assert.ok(!mentions(p.statement, p.answer), `${p.id}: ${p.statement}`);
    for (const h of p.hints) assert.ok(!mentions(h, p.answer), `${p.id}: ${h}`);
    assert.ok(p.hints.length >= 2, p.id);
    if (p.answer.kind === "division") assert.ok(p.answer.remainder > 0n, `${p.id}: a division with remainder`);
  }
});

test("each answer is the exact solution of its statement", () => {
  // Recomputed from the statement's own numbers, independently of the templates' code.
  for (const p of problems()) {
    const nums = numberTokens(p.statement).map((t) => t.value);
    const [x, y] = [nums[0]!, nums[1]!];
    const expected = ((): Answer => {
      switch (p.template) {
        case "mult-cajas": case "mult-autobus": case "mult-album": return Answer.number(x.mul(y));
        case "div-caramelos": case "div-camionetas": return Answer.division(x.num / y.num, x.num % y.num);
        case "div-pasteles": return Answer.division(y.num / x.num, y.num % x.num);
        case "frac-gallinas": return Answer.number(y.mul(x));
        case "frac-cinta": return Answer.number(x.div(y));
        case "frac-canicas": return Answer.number(x.sub(y.mul(x)));
        case "unid-km": case "unid-kg": return Answer.number(x.mul(Rational.of(1000)).add(y));
        case "unid-horas": return Answer.number(x.mul(Rational.of(60)).add(y));
        default: throw new Error(p.template);
      }
    })();
    assert.ok(Answer.equals(p.answer, expected), `${p.id}: ${p.statement} ${json(p.answer)} vs ${json(expected)}`);
  }
});

test("300 requests: every problem in three voices, each in its problem's split", () => {
  assert.equal(requests().length, 300);
  assert.equal(requests(Split.Test).length, 72);
  assert.ok(requests(Split.Test).every((r) => r.problem.split === Split.Test));
  assert.ok(requests(Split.Train).every((r) => r.problem.split === Split.Train));
  assert.equal(new Set(requests().map((r) => r.seed)).size, 300);
  assert.equal(json(request(problem(4), Voice.Wrong)), json(requests(Split.Test)[2]));
});

test("a request for help has no numbers; an attempt reads back as the answer it gives, right or wrong", () => {
  for (const r of requests()) {
    if (r.voice === Voice.Help) {
      assert.equal(numberTokens(r.message).length, 0, r.message);
      assert.equal(r.attempt, undefined);
      continue;
    }
    const read = Answer.parse(r.problem.answer.kind, r.message);
    assert.ok(read !== undefined && Answer.equals(read, r.attempt!), `${r.id}: ${r.message}`);
    assert.equal(Answer.equals(r.attempt!, r.problem.answer), r.voice === Voice.Right, r.id);
  }
});

const PINNED_DIGEST = "88a849cb250aa732";
