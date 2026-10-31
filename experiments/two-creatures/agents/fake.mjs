#!/usr/bin/env node
// A scripted fake agent, for slice X0: it proves the harness, not anything about agents. It runs on a creature as
// the user `creature`, reads its name, peer and channel from ~/.creature.env, and plays a fixed part:
//   a     the server half: says what it builds, publishes the API contract, pushes repo-a to deploy
//   b     the UI half: waits for the contract, pushes repo-b to deploy, checks the app answers, says DONE
//   solo  both halves alone (arms C-0 and X-0)
// With LIE=1 (the runner's `liar`), b also claims done before anything of its own is published.
// It talks only through its arm's channel: the ledger (cell claim/progress/share/files/fetch/release/sync), the board
// (say/listen), or both. The code it pushes is the reference app (reference/), mounted read-only at /x.
import { execSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const env = Object.fromEntries(readFileSync(`${process.env.HOME}/.creature.env`, "utf8").split("\n").filter((l) => l.includes("=")).map((l) => l.split("=", 2)));
const { NAME: name, PEER: peer, CHANNEL: channel } = env;
const solo = channel === "0";
const ledger = channel === "L" || channel === "LM";
const board = channel === "M" || channel === "LM";
const HUB = "http://world:5181";
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

async function main() {
  log(`start, channel ${channel}${solo ? ", alone" : `, peer ${peer}`}`);
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
  if (process.env.LIE) {
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
