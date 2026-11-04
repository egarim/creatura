// node --test experiments/two-creatures/lang/   (the checks A1–A4 and H1–H4 of issue #47 that need no run)
import { test } from "node:test";
import assert from "node:assert/strict";
import { LANGS, TYPES, admit, checksFor, claimsApp, decode, digest, format, parse, refusalText } from "./v0.mjs";

const EXAMPLES = [
  "claim server",
  "claim ui until 30m",
  "propose plan server=a ui=b",
  "propose api items=/api/items port=3000 service=api",
  "accept #3",
  "object #3 because the ui needs the port first",
  "offer api at repo-a:API.md",
  "done server check deployed repo-a",
  "done app check deployed repo-b and array http://world:8080/api/items and http http://world:8080/ 200",
  "need api from a",
  "need api from a because deployed repo-a",
  "block ui on server",
  "change port 3000 -> 3001",
  "note the build is slow on this machine",
];

test("every example parses, and its canonical text parses back to the same message (A1)", () => {
  for (const line of EXAMPLES) {
    const m = parse(line);
    assert.equal(format(m), line, line);
    assert.deepEqual(parse(format(m)), m, line);
  }
  assert.deepEqual([...new Set(EXAMPLES.map((l) => parse(l).type))].sort(), [...TYPES].sort(), "the examples cover every type");
});

test("prose and malformed messages are refused with the reason", () => {
  const refusals = {
    "DONE: it works": /not a message type/,
    "I have a complete app ready": /not a message type/,
    "done app": /carries its proof/,
    "done app check works": /unknown check/,
    "done app check http http://example.com/ 200": /not a URL on world/,
    "done app check file repo-a:../etc/passwd": /not a plain path/,
    "accept 3": /message number/,
    "change port 3000 -> 3000": /must change something/,
    "claim Server": /not a name/,
    "propose plan": /at least one/,
    "need api from c": /from takes a or b/,
    [`note ${"x".repeat(121)}`]: /120 characters/,
    "claim a\nclaim b": /one message per line/,
  };
  for (const [line, why] of Object.entries(refusals)) {
    const r = parse.safe(line);
    assert.equal(r.ok, false, line);
    assert.match(r.error, why, line);
  }
});

test("the world's checks: a done and an offer must pass theirs, a need's must still fail", () => {
  assert.deepEqual(checksFor(parse("done server check deployed repo-a")), { checks: [{ kind: "deployed", repo: "repo-a" }], mustPass: true });
  assert.deepEqual(checksFor(parse("offer api at repo-a:API.md")), { checks: [{ kind: "file", repo: "repo-a", path: "API.md" }], mustPass: true });
  assert.equal(checksFor(parse("need api from a because deployed repo-a")).mustPass, false);
  assert.deepEqual(checksFor(parse("claim server")).checks, []);
});

test("the board's own rules: answers name the peer's proposal, offers come from your repository, no repeats", () => {
  const messages = [
    { n: 1, from: "a", message: parse("claim goal") },
    { n: 2, from: "a", message: parse("propose plan server=a ui=b") },
  ];
  assert.equal(admit(parse("accept #2"), { from: "b", messages }), null);
  assert.match(admit(parse("accept #2"), { from: "a", messages }), /your own proposal/);
  assert.match(admit(parse("accept #1"), { from: "b", messages }), /not a proposal/);
  assert.match(admit(parse("accept #9"), { from: "b", messages }), /no message #9/);
  assert.match(admit(parse("offer api at repo-a:API.md"), { from: "b", messages }), /own repository, repo-b/);
  assert.match(admit(parse("need api from b"), { from: "b", messages }), /from yourself/);
  assert.match(admit(parse("propose plan server=a ui=b"), { from: "a", messages }), /no change/);
  assert.equal(admit(parse("propose plan server=a ui=b"), { from: "b", messages }), null);
});

test("every message decodes in every language, and different messages never read the same (A2)", () => {
  for (const lang of LANGS) {
    const seen = new Map();
    for (const line of EXAMPLES) {
      const sentence = decode(parse(line), "a", lang);
      assert.ok(sentence.length > 10 && !/undefined/.test(sentence), `${lang}: ${line} -> ${sentence}`);
      assert.ok(!seen.has(sentence), `${lang}: '${line}' and '${seen.get(sentence)}' read the same`);
      seen.set(sentence, line);
    }
  }
  assert.equal(decode(parse("done server check deployed repo-a"), "b", "en"), "creature-b has finished server; checked: repo-a is deployed.");
  assert.equal(decode(parse("block ui on server"), "b", "es"), "creature-b está bloqueado en ui, esperando server.");
  assert.equal(decode(parse("accept #2"), "b", "ru"), "creature-b принимает предложение #2.");
});

test("a note is marked as prose the decoder did not translate (A3)", () => {
  for (const lang of LANGS) assert.match(decode(parse("note hello"), "a", lang), /hello/);
  assert.match(decode(parse("note hello"), "a", "en"), /not translated/);
});

const run = (lines) => lines.map(([from, text], i) => ({ n: i + 1, from, text, message: parse(text) }));

test("the digest shows the state, masks what a done resolved, and leads with refused claims (H1, H2)", () => {
  const landed = run([
    ["a", "claim goal"], ["b", "claim goal"],
    ["a", "propose plan server=a ui=b"], ["b", "accept #3"],
    ["b", "block ui on server"],
    ["a", "offer api at repo-a:API.md"],
    ["a", "done server check deployed repo-a and file repo-a:API.md"],
    ["b", "need review from a"],
  ]);
  const refused = [{ from: "b", text: "done app check array http://world:8080/api/items", error: "the world does not agree: no answer", reason: "world", check: { kind: "array", url: "http://world:8080/api/items" } }];
  const d = digest(landed, refused, { lang: "en" });
  assert.match(d[0], /8 messages, 1 refused/);
  assert.equal(d[1], "Refused by the world: creature-b said app was done (does not hold: http://world:8080/api/items returns a JSON list)");
  assert.ok(d.some((l) => l === "Contract: api: repo-a/API.md (creature-a)"), "an offer is state, so it shows");
  assert.ok(d.some((l) => /^Agreed: plan: server → a, ui → b/.test(l)));
  assert.ok(d.some((l) => /^Done \(checked by the world\): server/.test(l)));
  assert.ok(!d.some((l) => /^Blocked/.test(l)), "the done of server resolved b's block on it");
  assert.ok(d.some((l) => /^Needs: creature-b: review ← creature-a/.test(l)));
  assert.ok(d.some((l) => /hidden/.test(l)), "claims are counted, not shown");
});

test("the digest stays inside its limit whatever the number of messages (H3)", () => {
  const lines = [];
  for (let i = 0; i < 200; i++) lines.push([i % 2 ? "b" : "a", `block t${i} on x${i}`]);
  const d = digest(run(lines), [], { lang: "es", limit: 8 });
  assert.equal(d.length, 8);
  assert.match(d[0], /^Cómo van las cosas \(200 mensajes, 0 rechazados\)/);
  assert.match(d[7], /más de estado/);
});

test("the digest reads in every language (H4)", () => {
  for (const lang of LANGS) {
    const d = digest(run([["a", "propose plan server=a ui=b"], ["b", "accept #1"]]), [], { lang });
    assert.ok(d.length >= 2 && d.every((l) => !/undefined/.test(l)), lang);
  }
  assert.deepEqual(digest([], [], { lang: "ru" }), ["Как идут дела (сообщений: 0, отклонено: 0)", "Пока ничего."]);
});

test("a refusal reads in the owner's language, built from the failed check, never the board's English", () => {
  const world = { from: "b", text: "done app check deployed repo-b", error: "the world does not agree: repo-b is never deployed", reason: "world", check: { kind: "deployed", repo: "repo-b" } };
  assert.equal(refusalText(world, "es"), "creature-b dijo que app estaba terminado (no se cumple: repo-b está desplegado)");
  assert.equal(refusalText(world, "ru"), "creature-b сообщил(а), что app готово (не выполнено: repo-b развёрнут)");
  assert.equal(refusalText({ from: "b", text: "DONE: it works", reason: "language" }, "es"), "creature-b “DONE: it works” (no es cellspeak)");
  assert.equal(refusalText({ from: "a", text: "need api from b because deployed repo-b", reason: "needless", check: { kind: "deployed", repo: "repo-b" } }, "en"), "creature-a “need api from b because deployed repo-b” (already holds: repo-b is deployed)");
  for (const lang of LANGS) assert.doesNotMatch(digest([], [world], { lang }).join(" "), lang === "en" ? /world does not agree/ : /does not|never/);
});

test("only a done whose checks look at the published app is a claim that the app is done", () => {
  assert.equal(claimsApp(parse("done server check deployed repo-a and file repo-a:API.md")), false);
  assert.equal(claimsApp(parse("done app check deployed repo-b and array http://world:8080/api/items")), true);
  assert.equal(claimsApp(parse("done ui check http http://world:8080/ 200")), true);
  assert.equal(claimsApp(parse("done deploy check http http://world:7001/status 200")), false);
  assert.equal(claimsApp(parse("claim app")), false);
  assert.equal(claimsApp(undefined), false);
});
