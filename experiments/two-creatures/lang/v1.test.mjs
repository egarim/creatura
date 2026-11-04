// node --test experiments/two-creatures/lang/v1.test.mjs   (cellspeak v1: the words log #96 was missing, and the tool)
import { test } from "node:test";
import assert from "node:assert/strict";
import { LANGS, TOOL, TYPES, admit, checksFor, claimsApp, decode, digest, encode, format, parse, refusalText } from "./v1.mjs";

const EXAMPLES = [
  "claim server",
  'claim "shared shopping list" until 40m',
  "propose plan server=a ui=b",
  "accept #3",
  "object #3 because the ui needs the port first",
  "offer api at repo-a:API.md",
  "done server check deployed repo-a",
  "done app check deployed repo-b and array http://world:8080/api/items and http http://world:8080/ 200",
  "verified app of b check array http://world:8080/api/items",
  "need api from a",
  "need api from a because deployed repo-a",
  "ask restart of b",
  "ask restart of b because to test durability",
  "block ui on server",
  'block ui on "the api contract"',
  "change port 3000 -> 3001",
  "note the build is slow on this machine",
];

test("every example parses, and its canonical text parses back to the same message", () => {
  for (const line of EXAMPLES) {
    const m = parse(line);
    assert.equal(format(m), line, line);
    assert.deepEqual(parse(format(m)), m, line);
  }
  assert.deepEqual([...new Set(EXAMPLES.map((l) => parse(l).type))].sort(), [...TYPES].sort(), "the examples cover every type");
});

test("v1 takes the names log #96 refused, in quotes; v0's grammar still refuses prose", () => {
  assert.equal(parse('claim "shared shopping list end-to-end" until 40m').task, "shared shopping list end-to-end");
  for (const [line, why] of Object.entries({
    "claim shared shopping list": /unexpected/,
    'claim "two  spaces"': /not a name/,
    'claim ""': /not a name/,
    "DONE: it works": /not a message type/,
    "done app": /carries its proof/,
    "verified app of c check deployed repo-b": /takes a or b/,
    "ask restart": /ask <action> of/,
    [`ask restart of b because ${"x".repeat(121)}`]: /120 characters/,
  })) {
    const r = parse.safe(line);
    assert.equal(r.ok, false, line);
    assert.match(r.error, why, line);
  }
});

const SAME = [
  [{ type: "claim", task: "shared shopping list", until: 40 }, 'claim "shared shopping list" until 40m'],
  [{ type: "propose", topic: "plan", terms: { server: "a", ui: "b" } }, "propose plan server=a ui=b"],
  [{ type: "accept", ref: 3 }, "accept #3"],
  [{ type: "offer", name: "api", repo: "repo-a", path: "API.md" }, "offer api at repo-a:API.md"],
  [{ type: "done", task: "app", checks: [{ kind: "deployed", repo: "repo-b" }, { kind: "array", url: "http://world:8080/api/items" }] }, "done app check deployed repo-b and array http://world:8080/api/items"],
  [{ type: "verified", task: "app", who: "b", checks: [{ kind: "http", url: "http://world:8080/", status: 200 }] }, "verified app of b check http http://world:8080/ 200"],
  [{ type: "need", thing: "api", who: "a", checks: [{ kind: "deployed", repo: "repo-a" }] }, "need api from a because deployed repo-a"],
  [{ type: "ask", action: "restart", who: "b", because: "  to test   durability " }, "ask restart of b because to test durability"],
  [{ type: "block", task: "ui", on: "the api contract" }, 'block ui on "the api contract"'],
  [{ type: "change", field: "port", old: "3000", new: "3001" }, "change port 3000 -> 3001"],
  [{ type: "note", text: "hello  there" }, "note hello there"],
  [{ type: "claim", task: "Server" }, "claim server"],
];

test("the tool: the same fields always write the same text, and it is what parse reads (determinism)", () => {
  for (const [fields, text] of SAME) {
    const a = encode(fields), b = encode(structuredClone(fields));
    assert.equal(a.text, text, JSON.stringify(fields));
    assert.equal(b.text, a.text);
    assert.deepEqual(a.message, parse(a.text));
    for (const lang of LANGS) assert.equal(decode(a.message, "a", lang), decode(parse(text), "a", lang));
  }
});

test("the tool refuses what the grammar refuses, with a reason, and never writes a malformed message", () => {
  for (const [fields, why] of [
    [{ type: "shout" }, /type is one of/],
    [{ type: "claim" }, /claim needs task/],
    [{ type: "done", task: "app" }, /needs checks/],
    [{ type: "done", task: "app", checks: [{ kind: "works" }] }, /kind is one of/],
    [{ type: "done", task: "app", checks: [{ kind: "http", url: "http://example.com/" }] }, /not a URL on world/],
    [{ type: "ask", action: "restart" }, /ask needs who/],
    [{ type: "note", text: "x".repeat(121) }, /120 characters/],
    [{ type: "claim", task: "a name that is far too long to be any kind of task name" }, /not a name/],
  ]) {
    const r = encode.safe(fields);
    assert.equal(r.ok, false, JSON.stringify(fields));
    assert.match(r.error, why, JSON.stringify(fields));
  }
});

test("the tool's schema offers exactly the language's types", () => {
  assert.equal(TOOL.name, "message");
  assert.deepEqual(TOOL.parameters.properties.type.enum, TYPES);
  assert.deepEqual(TOOL.parameters.properties.checks.items.properties.kind.enum, ["deployed", "file", "http", "array"]);
});

test("the board's v1 rules: a done is about your own work, verified and ask about your peer's", () => {
  const messages = [{ n: 1, from: "a", message: parse("propose plan server=a ui=b") }];
  assert.match(admit(parse("done app check array http://world:8080/api/items"), { from: "a", messages }), /own work: add a check on repo-a.*say verified/);
  assert.equal(admit(parse("done app check deployed repo-a and array http://world:8080/api/items"), { from: "a", messages }), null);
  assert.equal(admit(parse("verified app of b check array http://world:8080/api/items"), { from: "a", messages }), null);
  assert.match(admit(parse("verified app of a check array http://world:8080/api/items"), { from: "a", messages }), /for your own, say done/);
  assert.match(admit(parse("ask restart of a"), { from: "a", messages }), /of yourself/);
  assert.equal(admit(parse("ask restart of b"), { from: "a", messages }), null);
  assert.deepEqual(checksFor(parse("verified app of b check deployed repo-b")), { checks: [{ kind: "deployed", repo: "repo-b" }], mustPass: true });
  assert.equal(claimsApp(parse("verified app of b check array http://world:8080/api/items")), false, "a verified is not a claim of done");
});

test("every message decodes in every language, and different messages never read the same", () => {
  for (const lang of LANGS) {
    const seen = new Map();
    for (const line of EXAMPLES) {
      const s = decode(parse(line), "a", lang);
      assert.ok(s.length > 10 && !/undefined/.test(s), `${lang}: ${line} -> ${s}`);
      assert.ok(!seen.has(s), `${lang}: '${line}' and '${seen.get(s)}' read the same`);
      seen.set(s, line);
    }
  }
  assert.equal(decode(parse("verified app of b check deployed repo-b"), "a", "es"), "creature-a comprobó app de creature-b: repo-b está desplegado.");
  assert.equal(decode(parse("ask restart of b"), "a", "en"), "creature-a asks creature-b to do restart.");
});

const run = (lines) => lines.map(([from, text], i) => ({ n: i + 1, from, text, message: parse(text) }));

test("the digest: verified and open asks show; an ask is answered by the asked one's done or verified; typos are not lies", () => {
  const landed = run([
    ["b", 'claim "shared shopping list"'],
    ["b", "done app check deployed repo-b and array http://world:8080/api/items"],
    ["a", "verified app of b check array http://world:8080/api/items"],
    ["a", "ask restart of b because to test durability"],
  ]);
  const refused = [
    { from: "b", text: "done app check deployed repo-b", reason: "world", check: { kind: "deployed", repo: "repo-b" } },
    { from: "a", text: "claim shared shopping list", reason: "language" },
  ];
  const d = digest(landed, refused, { lang: "en" });
  assert.match(d[0], /4 messages, 2 refused/);
  assert.equal(d[1], "Refused by the world: creature-b said app was done (does not hold: repo-b is deployed)");
  assert.ok(d.includes("Verified by the peer: app (creature-b ← creature-a)"));
  assert.ok(d.includes("Asked: creature-a → creature-b: restart"));
  const after = digest([...landed, { n: 5, from: "b", message: parse("done restart check deployed repo-b") }], [], { lang: "en" });
  assert.ok(!after.some((l) => l.startsWith("Asked")), "b's later done answers the ask");
  for (const lang of LANGS) assert.ok(digest(landed, refused, { lang }).every((l) => !/undefined/.test(l)), lang);
  assert.equal(refusalText(refused[0], "ru"), "creature-b сообщил(а), что app готово (не выполнено: repo-b развёрнут)");
});

test("the digest stays inside its limit whatever the number of messages", () => {
  const lines = [];
  for (let i = 0; i < 200; i++) lines.push([i % 2 ? "b" : "a", `ask t${i} of ${i % 2 ? "a" : "b"}`]);
  assert.equal(digest(run(lines), [], { limit: 8 }).length, 8);
});
