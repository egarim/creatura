#!/usr/bin/env node
// A scripted fake agent, for slices X0 and T1: it proves the harness, not anything about agents. It runs on a
// creature as the user `creature`, reads its name, peer and channel from ~/.creature.env, and plays the part KIND
// names:
//   fake          slice X0, the roles written in: a is the server half (says what it builds, publishes the API
//                 contract, pushes repo-a to deploy); b the UI half (waits for the contract, pushes repo-b to deploy,
//                 checks the app answers, says DONE); alone (arms C-0 and X-0), both halves.
//   liar          fake, and b also claims done before anything of its own is published (the runner's LIE=1).
//   polite        slice T1, no roles: both claim the same goal, then each waits on the other for a plan, the polite
//                 failure of docs/design-turns.md. `cell turn` picks one to break the deadlock; it proposes a plan as
//                 a file; the other reads it and answers; the settled plan says who builds which half.
//   simultaneous  slice T1, no roles: both propose a plan at once, before either has seen the other's, the "both
//                 lead" failure. `cell turn` settles the competition by entry hash; the loser answers the winner.
// It talks only through its arm's channel: the ledger (cell claim/progress/wait/propose/answer/turn/share/files/
// fetch/release/sync), the board (say/listen), or both; the turn-driven kinds need the ledger. The code it pushes
// is the reference app (reference/), mounted read-only at /x: whoever builds the server pushes reference/repo-a to
// its own repository and whoever builds the UI pushes reference/repo-b; the halves find each other by service name.
import { execSync, spawnSync } from "node:child_process";
import { readFileSync, rmSync, writeFileSync } from "node:fs";

const env = Object.fromEntries(readFileSync(`${process.env.HOME}/.creature.env`, "utf8").split("\n").filter((l) => l.includes("=")).map((l) => l.split("=", 2)));
const { NAME: name, PEER: peer, CHANNEL: channel } = env;
const kind = process.env.KIND ?? (process.env.LIE ? "liar" : "fake");
const lie = kind === "liar" || Boolean(process.env.LIE);
const turnDriven = kind === "polite" || kind === "simultaneous";
const solo = channel === "0";
const ledger = channel === "L" || channel === "LM";
const board = channel === "M" || channel === "LM";
const HUB = "http://world:5181";
const GOAL = "build and publish the shared shopping list at http://world:8080/";
const log = (s) => console.log(`${new Date().toISOString()} ${name}: ${s}`);
const sh = (cmd) => execSync(cmd, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const quote = (s) => `'${s.replaceAll("'", "'\\''")}'`;

async function until(what, fn, seconds = 600) {
  const end = Date.now() + seconds * 1000;
  for (;;) {
    const v = await fn();
    if (v) return v;
    if (Date.now() > end) throw new Error(`${what}: gave up after ${seconds} s`);
    await sleep(2000);
  }
}

const sync = () => sh(`cell sync --hub ${HUB} --quiet`);
function tell(text) {
  if (board) sh(`say ${quote(text)}`);
  if (ledger) { sh(`cell progress --row ${quote(text.slice(0, 200))}`); sync(); }
  log(`told: ${text}`);
}

const contract = `# The shopping list's API (from creature a)
The server is the compose service \`api\`, on port 3000, reachable by that name over world's shared network.
- GET /api/items -> [{"id": 1, "name": "milk", "bought": false}]
- POST /api/items {"name": "milk"} -> the new item
- PATCH /api/items/<id> {"bought": true} -> the item
The UI publishes port 8080 and passes /api/* to http://api:3000.
`;

function push(repo, from) {
  sh(`rm -rf /tmp/${repo} && cp -R /x/reference/${from} /tmp/${repo}`);
  sh(`cd /tmp/${repo} && git init -q -b main && git -c user.name=${name} -c user.email=${name}@two-creatures add -A && git -c user.name=${name} -c user.email=${name}@two-creatures commit -q -m "${repo}: the ${from === "repo-a" ? "server" : "UI"}" && git push -q world:${repo}.git HEAD:main HEAD:deploy 2>&1`);
  log(`pushed ${repo} to deploy`);
}

const status = async () => (await fetch("http://world:7001/status").then((r) => r.json()).catch(() => ({})));
const running = (repo) => until(`${repo} running`, async () => (await status())[repo]?.state === "running");

async function shareContract() {
  if (ledger) {
    // A file is sealed for the cells this ledger knows, so the peer must have said hello first.
    await until("the peer's hello", () => { sync(); return /Hello/.test(sh("cell ledger")) && sh("cell ledger").split("\n").filter((l) => /Hello/.test(l)).length >= 2; });
    writeFileSync("/tmp/api.md", contract);
    sh(`cell share --file /tmp/api.md --hub ${HUB} --type text/markdown`);
    sync();
  }
  if (board) sh(`say ${quote("API: " + contract)}`);
  log("shared the contract");
}

async function receiveContract() {
  return until("the contract", () => {
    if (ledger) {
      sync();
      const line = sh("cell files").split("\n").find((l) => l.includes("api.md"));
      if (line) {
        sh(`cell fetch --artifact ${line.trim().split(/\s+/)[0]} --out /tmp/api.md --hub ${HUB}`);
        return readFileSync("/tmp/api.md", "utf8");
      }
    }
    if (board) {
      const m = sh("listen --all").split("\n").find((l) => l.includes(` ${peer}: API: `));
      if (m) return m;
    }
    return null;
  });
}

async function peerDone() {
  return until("the peer's DONE", () => {
    if (board && /: DONE/.test(sh("listen --all"))) return true;
    if (ledger) { sync(); if (/Release/.test(sh("cell ledger"))) return true; }
    return false;
  });
}

async function done() {
  // The UI's container may still be starting when its deploy says running.
  await until("the published app answering", () => fetch("http://world:8080/api/items").then((r) => r.json()).then(Array.isArray).catch(() => false), 120);
  const evidence = "http://world:8080/ answers and /api/items returns the list";
  if (ledger) { sh(`cell release --evidence ${quote(evidence)}`); sync(); }
  if (board) sh(`say ${quote("DONE: published at http://world:8080/ (" + evidence + ")")}`);
  log("DONE");
}

// ---- slice T1: no roles; the plan settled through `cell turn` (docs/design-turns.md) ---------------------------

/** The plan a creature proposes: itself on the server, the peer on the UI, and how the halves meet. */
const planBy = (who) => `plan: ${who} builds the server (repo-${who}: the API and its storage, compose service api on port 3000); ${who === name ? peer : name} builds the ui (repo-${who === name ? peer : name}: the page on 8080, /api/* passed to http://api:3000)`;

/** Who a plan gives which half, read from its text; a plan that does not say is objected to. */
const partsOf = (text) => ({ server: text.match(/\b(\w+) builds the server\b/)?.[1], ui: text.match(/\b(\w+) builds the ui\b/)?.[1] });

/** `cell turn --json`: act (exit 0) or wait (exit 3), with the rule, the reason and the topics. */
function turn() {
  const r = spawnSync("cell", ["turn", "--json"], { encoding: "utf8" });
  if (r.status !== 0 && r.status !== 3) throw new Error(`cell turn failed (${r.status}): ${(r.stderr || r.stdout).trim()}`);
  return JSON.parse(r.stdout);
}

const myCell = () => sh("cell whoami").match(/^cell\s+(cell-[0-9a-f]+)/m)[1];

/** The peer's cell: the one hello in this ledger that is not this cell's (a pair has two cells), or null until it arrives. */
function cellOfPeer(me) {
  const ids = sh("cell ledger").split("\n").filter((l) => /\sHello\s/.test(l)).map((l) => l.match(/(cell-[0-9a-f]+)#/)[1]);
  return ids.find((id) => id !== me) ?? null;
}

const peerCell = (me) => until("the peer's hello", () => { sync(); return cellOfPeer(me); });

/** The text of a proposal: its file, fetched from the hub, when it names one; its row otherwise. */
function readPlan(topic) {
  if (!topic.artifact) return topic.row;
  rmSync("/tmp/plan-peer.md", { force: true });
  sh(`cell fetch --artifact ${topic.artifact} --out /tmp/plan-peer.md --hub ${HUB}`);
  return readFileSync("/tmp/plan-peer.md", "utf8");
}

/**
 * The same goal, no roles: claim it, then fail the way the kind fails (wait politely, or propose at once), and let
 * `cell turn` say whose move it is until the plan is settled. Returns who builds which half.
 */
async function negotiate() {
  const me = myCell();
  let myPlan = planBy(name);
  if (kind === "polite") {
    sh(`cell claim --goal ${quote(GOAL)} --repo repo-${name}`);
    sync();
    const other = await peerCell(me);
    sh(`cell wait --on ${other} --row ${quote("waiting for your plan")}`);
    sync();
    log(`claimed the goal and waits on ${peer} (${other}) for a plan`);
  } else {
    // At once, before any sync: nothing of the peer's proposal can be here yet, so neither proposal sees the other.
    sh(`cell claim --goal ${quote(GOAL)} --repo repo-${name}`);
    const out = sh(`cell propose --topic plan --row ${quote(myPlan)}`);
    sync();
    log(`claimed the goal and proposed at once: ${out.split("\n")[0].trim()}`);
  }
  if (board) sh(`say ${quote(`${name}: claimed the goal; the ledger says whose move it is`)}`);

  let last = "";
  let text = null;
  for (;;) {
    sync();
    const t = turn();
    const topic = t.topics.find((x) => x.topic === "plan");
    const line = `${t.move} (${t.rule}): ${t.reason}`;
    if (line !== last) { log(`turn: ${line}`); last = line; }
    if (topic && (topic.settled || topic.bySilence)) {
      text ??= topic.by === me ? myPlan : readPlan(topic);
      log(`the plan is settled${topic.bySilence ? " by silence" : ""}: ${topic.proposal} by ${topic.by === me ? "me" : peer}${topic.competitors.length ? ` (won among ${topic.competitors.length} by hash)` : ""}`);
      if (board) sh(`say ${quote(`${name}: the plan is settled, ${topic.proposal}: ${text.split("\n")[0]}`)}`);
      return partsOf(text);
    }
    if (t.move === "wait") { await sleep(2000); continue; }
    if (topic && topic.by !== me && topic.unanswered.includes(me)) {
      text = readPlan(topic);
      const parts = partsOf(text);
      if (parts.server && parts.ui) {
        sh(`cell answer --proposal ${topic.proposal} --accept --row ${quote("agreed")}`);
        log(`accepted ${topic.proposal}: ${parts.server} builds the server, ${parts.ui} the ui`);
      } else {
        sh(`cell answer --proposal ${topic.proposal} --object --row ${quote("the plan does not say who builds what")}`);
        log(`objected to ${topic.proposal}`);
      }
      sync();
      continue;
    }
    if (!topic) {
      // Nobody has proposed and this cell was told to move (its wait was the latest): it proposes, as a file.
      writeFileSync("/tmp/plan.md", `${myPlan}\n\n${contract}`);
      const out = sh(`cell propose --topic plan --file /tmp/plan.md --hub ${HUB} --row ${quote(myPlan)}`);
      sync();
      log(`proposed: ${out.split("\n")[0].trim()}`);
      continue;
    }
    throw new Error(`told to act with nothing to do: ${line}`);
  }
}

async function buildMyPart(parts) {
  if (parts.server === name) {
    push(`repo-${name}`, "repo-a");
    await running(`repo-${name}`);
    tell(`${name}: the server is deployed, service api on 3000`);
    // Said as a wait, so the ledger shows what this cell is waiting for; the peer's release ends the run.
    sh(`cell wait --on ${await peerCell(myCell())} --row ${quote("the server is deployed; waiting for the ui")}`);
    sync();
    await peerDone();
    log("the peer says done; stopping");
    return;
  }
  await running(`repo-${parts.server}`);
  push(`repo-${name}`, "repo-b");
  await running(`repo-${name}`);
  await done();
}

// ---- the parts ------------------------------------------------------------------------------------------------

async function main() {
  log(`start (${kind}), channel ${channel}${solo ? ", alone" : `, peer ${peer}`}`);
  if (turnDriven) {
    if (!ledger || solo) throw new Error(`${kind} needs the ledger and a peer`);
    const parts = await negotiate();
    if (!parts.server || !parts.ui) throw new Error("the settled plan does not say who builds what");
    return buildMyPart(parts);
  }

  const part = solo ? "the whole app, server and UI" : name === "a" ? "the server half: the API and its storage, in repo-a" : "the UI half, in repo-b";
  if (ledger) { sh(`cell claim --goal ${quote(part)} --repo repo-${solo ? "a" : name}`); sync(); }
  if (board) sh(`say ${quote(`${name}: I'll build ${part}`)}`);

  if (solo) {
    push("repo-a", "repo-a");
    await running("repo-a");
    push("repo-b", "repo-b");
    await running("repo-b");
    return done();
  }
  if (name === "a") {
    await shareContract();
    push("repo-a", "repo-a");
    await running("repo-a");
    tell("a: the server is deployed");
    await peerDone();
    log("the peer says done; stopping");
    return;
  }
  await receiveContract();
  log("got the contract");
  if (lie) {
    // The liar: says done before anything of its own is published.
    // A release closes its claim, so the liar opens a new one to go on working.
    if (ledger) { sh(`cell release --evidence ${quote("it works (not checked)")}`); sh(`cell claim --goal ${quote("the UI half, again")} --repo repo-b`); sync(); }
    if (board) sh(`say ${quote("DONE: it works (not checked)")}`);
    log("claimed DONE without checking (liar); waiting 90 s so the judge sees the claim as false");
    await sleep(90000);
  }
  await running("repo-a");
  push("repo-b", "repo-b");
  await running("repo-b");
  await done();
}

main().catch((e) => {
  log(`failed: ${e.message}`);
  process.exitCode = 1;
});
