// Test M1 (docs/design-mobile.md): creatura-js replays the C# host's four controlled goldens byte for byte. The files
// are read from the repository (tests/Creatura.Hosts.AgentFramework.Tests/golden), never from a copy, so a change in
// C# that moves a golden breaks this build too until both languages agree. Re-recording a golden is the owner's
// decision (tests/Creatura.Tests/golden/README.md); this test never writes one.

import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { ControlledHost, RestoreServiceDomain, formatJsonNumber, readJsonl, sameEvent, writeJsonl } from "../src/index.js";
import type { AgentRun, TraceEvent } from "../src/index.js";

const barriers = ["clean", "patchrevoked", "patchlies", "deployrevoked"];

/** The repository root, found from Creatura.slnx above this file, as the C# tests find it. */
function repositoryRoot(): string {
  let dir = dirname(fileURLToPath(import.meta.url));
  while (!existsSync(join(dir, "Creatura.slnx"))) {
    const parent = dirname(dir);
    if (parent === dir) throw new Error("Creatura.slnx not found above " + fileURLToPath(import.meta.url));
    dir = parent;
  }

  return dir;
}

const goldens = resolve(repositoryRoot(), "tests", "Creatura.Hosts.AgentFramework.Tests", "golden");

/** The C# host describes itself in two strings, the agent's name and the framework in the first note; the run hands them over, as C#'s FakeHostTests does. */
const asTheCSharpHost = { name: "Controlled (AF)", framework: "Microsoft.Agents.AI" };

async function replay(barrier: string, host = ControlledHost.offline(asTheCSharpHost)): Promise<AgentRun> {
  return host.run(RestoreServiceDomain.withBarrier(barrier), 30);
}

function show(e: TraceEvent | undefined): string {
  return e === undefined
    ? "<none>"
    : `${e.tick} ${e.level} ${e.setPoint} stress=${formatJsonNumber(e.stress)} freedom=${formatJsonNumber(e.freedom)} ${e.action} ${e.kind} ${e.detail.replace(/\n/g, " / ")}`;
}

/** Row for row and note for note; the first difference is the message. The same comparison as C#'s `AssertSame`. */
function assertSame(expected: AgentRun, actual: AgentRun, name: string): void {
  assert.equal(actual.agent, expected.agent, `${name}: agent '${expected.agent}' became '${actual.agent}'`);
  for (let i = 0; i < Math.max(expected.trace.length, actual.trace.length); i++) {
    const e = expected.trace[i];
    const a = actual.trace[i];
    if (sameEvent(e, a)) continue;
    assert.fail(`${name}: trace row ${i + 1} of ${expected.trace.length} (golden) / ${actual.trace.length} (now) differs\n  golden: ${show(e)}\n  now:    ${show(a)}`);
  }

  for (let i = 0; i < Math.max(expected.notes.length, actual.notes.length); i++) {
    const e = expected.notes[i];
    const a = actual.notes[i];
    if (e === a) continue;
    assert.fail(`${name}: note ${i + 1} of ${expected.notes.length} (golden) / ${actual.notes.length} (now) differs\n  golden: ${e ?? "<none>"}\n  now:    ${a ?? "<none>"}`);
  }

  assert.equal(actual.goalReached, expected.goalReached, `${name}: goalReached ${expected.goalReached} became ${actual.goalReached}`);
  assert.equal(actual.ticks, expected.ticks, `${name}: ticks ${expected.ticks} became ${actual.ticks}`);
}

for (const barrier of barriers) {
  const name = `restore-${barrier}-controlled`;

  test(`${name}: the run matches its golden row for row`, async () => {
    const path = join(goldens, name + ".jsonl");
    assert.ok(existsSync(path), `${path} is missing: the host's goldens live in tests/Creatura.Hosts.AgentFramework.Tests/golden`);
    assertSame(readJsonl(readFileSync(path, "utf8")), await replay(barrier), name);
  });

  test(`${name}: the run is its golden byte for byte`, async () => {
    const golden = readFileSync(join(goldens, name + ".jsonl"));
    const written = Buffer.from(writeJsonl(await replay(barrier)), "utf8");
    if (written.equals(golden)) return;
    // Name the first line that differs, then fail on the bytes.
    const g = golden.toString("utf8").split("\n");
    const w = written.toString("utf8").split("\n");
    const line = g.findIndex((l, i) => l !== w[i]);
    assert.fail(`${name}: line ${line + 1} differs\n  golden: ${g[line]}\n  now:    ${w[line] ?? "<none>"}`);
  });
}

test("every golden in the folder is one this test replays", () => {
  // A file nobody can rebuild is a failure too, not a silent orphan (as in C#'s ControlledGoldenTests).
  const files = readdirSync(goldens).filter((f) => f.endsWith(".jsonl")).sort();
  assert.deepEqual(files, barriers.map((b) => `restore-${b}-controlled.jsonl`).sort());
});

test("with its own name, only the two lines where the host describes itself differ", async () => {
  for (const barrier of barriers) {
    const own = writeJsonl(await replay(barrier, ControlledHost.offline())).split("\n");
    const golden = readFileSync(join(goldens, `restore-${barrier}-controlled.jsonl`), "utf8").split("\n");
    assert.equal(own.length, golden.length);
    const differing = own.flatMap((l, i) => (l === golden[i] ? [] : [i]));
    // Line 0 is the header (the agent's name); the first note follows the header and the trace rows.
    const firstNote = golden.findIndex((l) => l.startsWith('{"note":'));
    assert.deepEqual(differing, [0, firstNote], `restore-${barrier}-controlled`);
    assert.equal(own[0], golden[0]!.replace('"Controlled (AF)"', '"Controlled (JS)"'));
    assert.equal(own[firstNote], '{"note":"policy bfs; framework creatura-js"}');
  }
});

test("a run read back from its JSON lines is the same run", async () => {
  for (const barrier of barriers) {
    const run = await replay(barrier);
    const text = writeJsonl(run);
    assert.equal(writeJsonl(readJsonl(text)), text);
  }
});
