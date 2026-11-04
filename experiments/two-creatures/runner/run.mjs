#!/usr/bin/env node
// The runner: one run of one arm, from fresh machines to a scored record (docs/design-two-creatures.md).
//
//   node runner/run.mjs --arm CC-L --agents fake,fake [--minutes 120] [--check-every 600] [--keep]
//   node runner/run.mjs --reference [label|live|check|persist|down]     the reference app, no agents
//
// Arms: <pairing>-<channel>, channel L (ledger only), LM (ledger + messages), M (messages only), or C-0 / X-0 for one
// agent alone. --agents names what drives each creature: fake (agents/fake.mjs, scripted, the roles written in),
// liar (a fake that also claims done before anything is published, to test the false-claim path), polite and
// simultaneous (slice T1 of docs/design-turns.md: no roles, the plan settled through `cell turn`, so they need the
// ledger), deepseek (agents/model.mjs: a model given the goal of agents/goal.md and a shell, DeepSeek through its API,
// DEEPSEEK_API_KEY on this host), or the real agents of slice X1 (claude, codex, small), which this runner refuses
// until they are wired.
// It talks to whatever Docker host `docker` points at; the sandbox is one network with fixed addresses inside
// 100.64.0.0/10, which the ledger hub accepts as mesh addresses.
import { execFileSync, spawnSync } from "node:child_process";
import { appendFileSync, copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { LANGS, claimsApp, decode, digest, parse } from "../lang/v0.mjs";

const HERE = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const NET = "tc-sandbox";
// The sandbox is inside the mesh's range on purpose: the ledger hub answers only loopback and 100.64.0.0/10 (C18). A
// judge on this host cannot reach those addresses when Tailscale runs here (it drops input from that range on any
// interface but its own), so it goes through the door: a forwarder container on Docker's default bridge, joined to the
// sandbox too, publishing world's 8080 and 7001 on this host's loopback (HOST below; runner/door.mjs).
const SUBNET = "100.127.200.0/24";
const IP = { world: "100.127.200.10", a: "100.127.200.21", b: "100.127.200.22" };
const HOST = { app: Number(process.env.TC_APP_PORT ?? 18080), deployer: Number(process.env.TC_DEPLOYER_PORT ?? 17001) };
const HUB = `http://world:5181`;

// ---- arguments ----------------------------------------------------------------------------------------------

const argv = process.argv.slice(2);
const opt = (name, fallback) => (argv.includes(name) ? argv[argv.indexOf(name) + 1] : fallback);
const reference = argv.includes("--reference");
const breakWhat = reference ? (argv[argv.indexOf("--reference") + 1] ?? "").replace(/^--.*/, "") : "";
const arm = reference ? `REF${breakWhat ? "-" + breakWhat : ""}` : opt("--arm");
const minutes = Number(opt("--minutes", reference ? 5 : 120));
const checkEvery = Number(opt("--check-every", 600));
const keep = argv.includes("--keep");
const roles = argv.includes("--roles");
// The judge is a web world (docs/design-web-worlds.md, "The two-creatures judge as a web world") run on this host with
// `creatura ux run` over judge/shopping-list.adapter.json, two cells per check (--judge ux, the default since 2026-11-02);
// --judge script runs the browser test in its container instead. TC_CREATURA names the creatura command (default:
// dotnet run on this repository's CLI); CREATURA_CHROMIUM names the browser when Playwright's own is not installed.
const uxJudge = (opt("--judge", "ux") === "ux");
if (!["script", "ux"].includes(opt("--judge", "ux"))) usage("--judge takes ux (the web world on this host, the default) or script (the container)"); // the parts written in: a the server, b the UI (goal.md)
if (!arm) usage("--arm is required");
const channel = reference ? "none" : arm.split("-")[1];
const solo = channel === "0";
if (!reference && !["L", "LM", "M", "MT", "0"].includes(channel)) usage(`unknown channel in ${arm}`);
// The owner's language, for the decoded log and the digest (issue #47): by default the language of the goal, English.
const lang = opt("--lang", "en");
if (!LANGS.includes(lang)) usage(`--lang takes ${LANGS.join(", ")}`);
const agentKinds = reference ? [] : (opt("--agents") ?? usage("--agents is required")).split(",");
if (!reference && agentKinds.length !== (solo ? 1 : 2)) usage(solo ? "an arm alone takes one agent" : "a pair takes two agents, a,b");
const TURN_KINDS = ["polite", "simultaneous"];
const MODEL_KINDS = ["deepseek"];
const TYPED_KINDS = ["typed", "typed-liar"];
for (const k of agentKinds) if (!["fake", "liar", ...TURN_KINDS, ...TYPED_KINDS, ...MODEL_KINDS].includes(k)) usage(`agent '${k}' is wired in slice X1; this runner takes fake, liar, polite, simultaneous, typed, typed-liar and deepseek`);
const ledger = channel === "L" || channel === "LM";
const board = channel === "M" || channel === "LM" || channel === "MT";
// MT: the board takes only cellspeak v0 and runs the checks its claims carry (lang/v0.mjs, issue #47)
const typedBoard = channel === "MT";
if (agentKinds.some((k) => TYPED_KINDS.includes(k)) !== (typedBoard && !agentKinds.some((k) => MODEL_KINDS.includes(k))) && !reference) {
  usage(typedBoard ? "an MT arm takes typed, typed-liar or deepseek agents" : "typed and typed-liar speak on a typed board: an arm with channel MT");
}
if (agentKinds.some((k) => TURN_KINDS.includes(k))) {
  if (!ledger) usage(`${agentKinds.join(",")} settle the plan through the ledger: an arm with channel L or LM`);
  if (solo) usage("polite and simultaneous are pairs");
  if (!agentKinds.every((k) => TURN_KINDS.includes(k))) usage("a turn-driven pair takes two of polite, simultaneous; fake and liar have their roles written in");
}
if (agentKinds.some((k) => MODEL_KINDS.includes(k))) {
  if (!agentKinds.every((k) => MODEL_KINDS.includes(k))) usage("a model-driven run takes deepseek for every creature; the scripted kinds have their parts written in");
  if (!(process.env.DEEPSEEK_API_KEY || process.env.deepseek)) usage("deepseek needs DEEPSEEK_API_KEY (or deepseek) in this shell's environment; it is passed to the creatures and never written to the record");
}
if (roles && (reference || solo || !agentKinds.every((k) => MODEL_KINDS.includes(k)))) usage("--roles splits the goal for a model-driven pair: a builds the server, b the UI");
/** What a model-driven creature gets from this host: the key and model, its limits, and a CA bundle when this host's egress is inspected (TC_CA_FILE). */
const agentEnv = () => ({
  DEEPSEEK_API_KEY: process.env.DEEPSEEK_API_KEY || process.env.deepseek || "",
  DEEPSEEK_MODEL: process.env.DEEPSEEK_MODEL || "deepseek-chat",
  DEEPSEEK_BASE_URL: process.env.DEEPSEEK_BASE_URL || "https://api.deepseek.com",
  AGENT_MINUTES: String(Number(opt("--minutes", 120))),
  AGENT_STEPS: opt("--steps", "200"),
  AGENT_BUDGET: opt("--budget", "2"),
  ...(roles ? { AGENT_ROLES: "1" } : {}),
  ...(process.env.TC_CA_FILE ? { NODE_EXTRA_CA_CERTS: "/etc/tc-ca.crt" } : {}),
});
const names = reference ? [] : solo ? ["a"] : ["a", "b"];

function usage(why) {
  console.error(`run: ${why}\nusage: run.mjs --arm <CC-L|...|C-0> --agents <kind>[,<kind>] [--minutes n] [--check-every s] [--keep] [--steps n] [--budget usd] [--roles] [--judge ux|script] [--lang en|es|ru]\n       run.mjs --reference [label|live|check|persist|down]`);
  process.exit(2);
}

// ---- helpers ------------------------------------------------------------------------------------------------

const runId = `${new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19)}-${arm}${roles ? "-R" : ""}`;
const state = join(HERE, "state", runId);
const out = join(HERE, "results", runId);
mkdirSync(state, { recursive: true });
mkdirSync(out, { recursive: true });
const t0 = Date.now();
const say = (s) => console.log(`[${((Date.now() - t0) / 1000).toFixed(0).padStart(4)}s] ${s}`);

function docker(args, { input, allowFail = false } = {}) {
  const r = spawnSync("docker", args, { input, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  if (r.status !== 0 && !allowFail) throw new Error(`docker ${args.slice(0, 3).join(" ")} failed: ${(r.stderr || r.stdout).trim().slice(0, 800)}`);
  return r.status === 0 ? r.stdout : null;
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function until(what, fn, seconds) {
  const end = Date.now() + seconds * 1000;
  while (Date.now() < end) {
    if (fn()) return;
    await sleep(500);
  }
  throw new Error(`${what}: not after ${seconds} s`);
}

/** A one-shot shell in the sandbox, on a creature image, with extra mounts. */
function inSandbox(script, mounts = []) {
  return docker(["run", "--rm", "--network", NET, "--add-host", `world:${IP.world}`, ...mounts.flatMap((m) => ["-v", m]), "--entrypoint", "sh", "tc-creature", "-c", script]);
}

/**
 * A one-shot shell on the experiment owner's vessel (the same machine-id every time). It runs as root in the container,
 * so what it writes under state/<run>/owner is given back to the user running this runner before it returns; otherwise
 * the run's cleanup cannot remove the directory (#20).
 */
const hostIds = process.getuid ? `${process.getuid()}:${process.getgid()}` : null;
function asOwner(script, mounts = []) {
  const giveBack = hostIds ? `; s=$?; chown -R ${hostIds} /owner; exit $s` : "";
  return docker(["run", "--rm", "-v", `${state}/owner:/owner`, ...mounts.flatMap((m) => ["-v", m]), "--entrypoint", "sh", "tc-creature", "-c", `cp /owner/machine-id /etc/machine-id; (${script})${giveBack}`]);
}

const own = (n) => `--home /owner --creatura owner --no-mesh`;

function teardown() {
  docker(["rm", "-f", "tc-world", "tc-creature-a", "tc-creature-b", "tc-door"], { allowFail: true });
  docker(["network", "rm", NET], { allowFail: true });
}

// ---- the machines -------------------------------------------------------------------------------------------

async function setUp() {
  teardown();
  docker(["network", "create", "--subnet", SUBNET, NET]);

  // git keys: one per creature (or solo), and the runner's own for the reference app
  mkdirSync(`${state}/world/keys`, { recursive: true });
  for (const who of [...(solo ? ["solo"] : names), "runner"]) {
    execFileSync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", `${who}@two-creatures`, "-f", `${state}/keys-${who}`]);
    copyFileSync(`${state}/keys-${who}.pub`, `${state}/world/keys/${who}.pub`);
  }

  // the experiment owner: its own key, never the real owner's
  mkdirSync(`${state}/owner`, { recursive: true });
  writeFileSync(`${state}/owner/machine-id`, crypto.randomUUID().replaceAll("-", "") + "\n");
  asOwner(`creatura cell init ${own()} >/dev/null && creatura cell owner-key ${own()} --out /owner/owners.txt >/dev/null`);
  copyFileSync(`${state}/owner/owners.txt`, `${state}/world/owners.txt`);
  writeFileSync(`${state}/world/world.env`, `BOARD=${board ? (typedBoard ? "typed" : "on") : "off"}\nHUB=${ledger ? "on" : "off"}\nWORLD_IP=${IP.world}\n`);

  // the images the app's builds start from, loaded into world's own Docker so a reset doesn't pull them again
  const preload = join(HERE, "build", "preload");
  mkdirSync(preload, { recursive: true });
  if (!existsSync(`${preload}/node-22.tar`)) {
    say("saving node:22-bookworm-slim for world's preload (once)");
    docker(["save", "-o", `${preload}/node-22.tar`, "node:22-bookworm-slim"]);
  }

  if (uxJudge) {
    // The door (see HOST): on the default bridge first, so its published ports answer from an address this host can
    // talk to, then joined to the sandbox, where it forwards to world.
    docker(["run", "-d", "--name", "tc-door", "--network", "bridge", "-p", `127.0.0.1:${HOST.app}:8080`, "-p", `127.0.0.1:${HOST.deployer}:7001`,
      "-e", `TARGET=${IP.world}`, "-v", `${HERE}/runner/door.mjs:/door.mjs:ro`, "node:22-bookworm-slim", "node", "/door.mjs"]);
    docker(["network", "connect", NET, "tc-door"]);
  }

  say(`world (board ${board ? (typedBoard ? "typed" : "on") : "off"}, hub ${ledger ? "on" : "off"})`);
  docker(["run", "-d", "--privileged", "--name", "tc-world", "--hostname", "world", "--network", NET, "--ip", IP.world,
    "-v", `${state}/world:/srv/setup:ro`, "-v", `${preload}:/srv/preload:ro`, "tc-world"]);
  await until("world ready", () => docker(["exec", "tc-world", "test", "-f", "/run/world-ready"], { allowFail: true }) !== null, 180);

  for (const n of names) {
    const setup = `${state}/creature-${n}`;
    mkdirSync(setup, { recursive: true });
    copyFileSync(`${state}/keys-${solo ? "solo" : n}`, `${setup}/id_ed25519`);
    writeFileSync(`${setup}/creature.env`, `NAME=${n}\nPEER=${solo ? "none" : n === "a" ? "b" : "a"}\nCHANNEL=${channel}\nWORLD=world\n`);
    say(`creature-${n}`);
    docker(["run", "-d", "--name", `tc-creature-${n}`, "--hostname", `creature-${n}`, "--network", NET, "--ip", IP[n],
      "--add-host", `world:${IP.world}`, "-v", `${setup}:/setup:ro`, "-v", `${HERE}:/x:ro`,
      ...(process.env.TC_CA_FILE ? ["-v", `${resolve(process.env.TC_CA_FILE)}:/etc/tc-ca.crt:ro`] : []), "tc-creature"]);
    await until(`creature-${n} ready`, () => docker(["exec", `tc-creature-${n}`, "test", "-f", "/run/creature-ready"], { allowFail: true }) !== null, 60);
    if (ledger) join_(n);
  }
}

/** A creature joins the ledger: it asks, the experiment owner endorses, it accepts and says hello at the hub. */
function join_(n) {
  const c = `tc-creature-${n}`;
  const ex = (cmd) => docker(["exec", "-u", "creature", c, "sh", "-c", cmd]);
  ex("cell request --out /tmp/request.json >/dev/null");
  docker(["cp", `${c}:/tmp/request.json`, `${state}/owner/${n}.request.json`]);
  asOwner(`creatura cell endorse ${own()} --request /owner/${n}.request.json --out /owner/${n}.endorsement.json >/dev/null`);
  docker(["cp", `${state}/owner/${n}.endorsement.json`, `${c}:/tmp/endorsement.json`]);
  docker(["exec", c, "chown", "creature", "/tmp/endorsement.json"]);
  const accepted = ex(`cell accept --endorsement /tmp/endorsement.json && cell sync --hub ${HUB} --quiet`);
  cells[accepted.match(/cell-[0-9a-f]+/)[0]] = n;
  say(`creature-${n} joined the ledger`);
}

// ---- the judge and the claims -------------------------------------------------------------------------------

let judgeRuns = 0;

/**
 * The judge as a web world: the adapter in judge/ filled with a fresh item and the world's address, two cells run in
 * order (add-item, then tick-item) with the script seats, read back from cell.json. The five steps are read off the
 * cells: 1 the page loads (add-item ran), 2 the item is in the data (add-item done), 3 a second browser showed it in
 * time (add-item live), 4 ticked in the data and shown ticked (tick-item done and live), 5 still there after the
 * restart (tick-item survived). Steps stop at the first failure, as the script judge's do.
 */
function judgeUx() {
  const n = ++judgeRuns;
  const item = `milk ${Date.now().toString(36)}`;
  const dir = join(state, "judge", String(n));
  mkdirSync(dir, { recursive: true });
  const fill = (text) => text.replaceAll("{{item}}", item).replaceAll("{{restart}}", `http://127.0.0.1:${HOST.deployer}/restart`);
  const judgeDir = join(HERE, "judge");
  writeFileSync(join(dir, "adapter.json"), fill(readFileSync(join(judgeDir, "shopping-list.adapter.json"), "utf8")));
  for (const task of ["add-item", "tick-item"]) writeFileSync(join(dir, `${task}.script`), fill(readFileSync(join(judgeDir, `${task}.script`), "utf8")));
  const creatura = (process.env.TC_CREATURA ?? `dotnet run -v q --project ${join(HERE, "..", "..", "src", "Creatura.Cli")} --`).split(/\s+/);
  const cells = {};
  for (const task of ["add-item", "tick-item"]) {
    const out = join(dir, task);
    const t0 = Date.now();
    let text = "";
    try {
      text = execFileSync(creatura[0], [...creatura.slice(1), "ux", "run", "--adapter", join(dir, "adapter.json"), "--base-url", `http://127.0.0.1:${HOST.app}/`,
        "--task", task, "--persona", "judge", "--env", "desktop", "--seat", `script:${join(dir, `${task}.script`)}`, "--ticks", "6", "--out", out],
        { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], timeout: 180000 });
    } catch (e) {
      text = `${e.stdout ?? ""}\n${e.stderr ?? ""}`;
    }
    const cellFile = join(out, "cell.json");
    cells[task] = existsSync(cellFile) ? JSON.parse(readFileSync(cellFile, "utf8")) : { error: `no cell.json: ${text.trim().split("\n").filter((l) => l.trim() && !l.startsWith("   at ") && !l.startsWith("---")).slice(-3).join(" | ").slice(0, 300)}` };
    cells[task].ms = Date.now() - t0;
    if (cells[task].error || !cells[task].dataDone) break;   // the next step needs this one
  }
  const add = cells["add-item"] ?? {}, tick = cells["tick-item"] ?? {};
  const steps = [];
  const step = (k, title, pass, detail) => { steps.push({ n: k, title, pass: Boolean(pass), ms: (k <= 3 ? add : tick).ms ?? 0, ...(pass ? {} : { detail: String(detail ?? "").slice(0, 300) }) }); return Boolean(pass); };
  step(1, "the page loads", !add.error, add.error)
    && step(2, "adding an item shows it", add.dataDone, add.checkDetail)
    && step(3, "the other browser shows it within 5 s, without a reload", add.live, add.liveDetail)
    && step(4, "checked in one browser, the other shows it checked within 5 s", tick.dataDone && tick.live, tick.error ?? (!tick.dataDone ? tick.checkDetail : tick.liveDetail))
    && step(5, "after a restart it is still there and still checked", tick.survived, tick.survivedDetail);
  const score = steps.filter((s) => s.pass).length;
  return JSON.stringify({ at: new Date().toISOString(), app: `http://127.0.0.1:${HOST.app}/ (world:8080)`, item, judge: "ux", score, shipped: score === 5, steps });
}

function judge(why) {
  const text = uxJudge ? judgeUx() : docker(["run", "--rm", "--network", NET, "--add-host", `world:${IP.world}`, "tc-judge"], { allowFail: true }) ?? "";
  const line = text.trim().split("\n").pop();
  let result;
  try { result = JSON.parse(line); } catch { result = { score: 0, shipped: false, steps: [{ n: 0, pass: false, detail: `the judge printed no result: ${text.slice(-300)}` }] }; }
  result.why = why;
  result.elapsed = Math.round((Date.now() - t0) / 1000);
  appendFileSync(`${out}/checks.jsonl`, JSON.stringify(result) + "\n");
  say(`judge (${why}): ${result.score}/5${result.shipped ? " shipped" : ""}${result.steps.find((s) => !s.pass)?.detail ? " — " + result.steps.find((s) => !s.pass).detail : ""}`);
  return result;
}

function boardMessages() {
  if (!board) return [];
  const text = docker(["exec", "tc-world", "cat", "/srv/board/messages.jsonl"], { allowFail: true }) ?? "";
  return text.split("\n").filter(Boolean).map((l) => JSON.parse(l));
}

/** What a typed board refused (MT): the message, who sent it and the reason; it never reached the peer. */
function refusedMessages() {
  if (!typedBoard) return [];
  const text = docker(["exec", "tc-world", "cat", "/srv/board/refused.jsonl"], { allowFail: true }) ?? "";
  return text.split("\n").filter(Boolean).map((l) => JSON.parse(l));
}

function ledgerEntries(n) {
  if (!ledger) return [];
  const text = docker(["exec", `tc-creature-${n}`, "cat", "/home/creature/.creatura/ledger.json"], { allowFail: true });
  if (!text) return [];
  try {
    const j = JSON.parse(text);
    return Array.isArray(j) ? j : j.entries ?? j.Entries ?? [];
  } catch { return []; }
}

const kindOf = (e) => String(e.kind ?? e.Kind ?? "");
const cells = {}; // cell id -> creature name, from each creature's accept

/** What the turns of docs/design-turns.md left in the ledgers: the waits, proposals and answers, and whose plan was accepted. */
function turns() {
  if (!ledger) return undefined;
  const entries = new Map();
  for (const n of names) for (const e of ledgerEntries(n)) entries.set(`${e.cell}#${e.sequence}`, e);
  const progress = [...entries.values()].filter((e) => kindOf(e) === "Progress").map((e) => e.payload ?? {});
  const accepted = progress.filter((p) => p.answers && p.verdict === "Accept").map((p) => p.answers);
  return {
    waits: progress.filter((p) => p.waitingOn).length,
    proposals: progress.filter((p) => p.topic && !p.answers).length,
    answers: progress.filter((p) => p.answers).length,
    objections: progress.filter((p) => p.answers && p.verdict === "Object").length,
    plannedBy: [...new Set(accepted.map((id) => cells[id.split("#")[0]] ?? id.split("#")[0]))],
  };
}
const seen = new Set();
const claims = [];

/**
 * A board message claims done only when it starts with DONE, as the goal asks, after an optional "creature-b:" or "b:".
 * A message that mentions DONE on the way ("I'll say DONE when...") is talk, not a claim (#21).
 */
const saysDone = (text) => /^DONE\b/.test(String(text).replace(/^\s*(?:creature-)?[ab]\s*:\s*/i, "").trimStart());

/** New claims of done since the last look: a message that starts with DONE (on a typed board, a landed done whose checks look at the app), or a Release entry. */
function newClaims() {
  const found = [];
  for (const m of boardMessages()) {
    const key = `board:${m.n}`;
    const isDone = typedBoard ? claimsApp(m.message) : saysDone(m.text);
    if (!seen.has(key) && isDone) found.push({ key, via: "board", who: m.from, text: m.text.slice(0, 300) });
    seen.add(key);
  }
  for (const n of names) {
    for (const e of ledgerEntries(n)) {
      const key = `ledger:${e.signature ?? e.Signature ?? JSON.stringify(e).slice(0, 200)}`;
      if (!seen.has(key) && /release/i.test(kindOf(e))) found.push({ key, via: "ledger", who: cells[e.cell] ?? e.cell, text: JSON.stringify(e.body ?? e.Body ?? e.payload ?? "").slice(0, 300) });
      seen.add(key);
    }
  }
  return found;
}

const refusedClaims = [];
const lastRefusedJudged = {};
/**
 * The dones a typed board refused since the last look. Each is judged too, at most one per creature every two
 * minutes, so the record can say whether the board refused only what the judge also holds false (a refused done the
 * judge scores 5 of 5 would be the language being too strict).
 */
function takeRefused() {
  if (!typedBoard) return;
  refusedMessages().forEach((r, i) => {
    const key = `refused:${i}`;
    if (seen.has(key)) return;
    seen.add(key);
    if (!claimsApp(parse.safe(r.text).message)) return;   // a refused claim that the app is done (a refused prose DONE never parsed)
    const c = { who: r.from, text: r.text.slice(0, 300), error: r.error, at: Math.round((Date.now() - t0) / 1000), score: null };
    if (Date.now() - (lastRefusedJudged[r.from] ?? 0) >= 120000) {
      lastRefusedJudged[r.from] = Date.now();
      c.score = judge(`refused claim by ${r.from}`).score;
    }
    refusedClaims.push(c);
  });
}

/**
 * Judge the claims of done made since the last look, at once: claims found together share one verdict, so a claim
 * is never scored on a state of the app reached after another claim's judging.
 */
function takeClaims() {
  const found = newClaims();
  if (!found.length) return null;
  const who = found.map((c) => `${c.who} via ${c.via}`).join(", ");
  const r = judge(`claim by ${who}`);
  for (const c of found) claims.push({ ...c, at: r.elapsed, score: r.score, true: r.shipped });
  return r;
}

// ---- the run ------------------------------------------------------------------------------------------------

function deployReference() {
  const env = breakWhat && breakWhat !== "down" ? `echo BREAK=${breakWhat} > .env && ` : "";
  if (breakWhat === "down") return say("reference: nothing deployed (down)");
  inSandbox(`set -e
mkdir -p ~/.ssh && cp /keys/keys-runner ~/.ssh/id_ed25519 && chmod 600 ~/.ssh/id_ed25519
printf 'Host world\\n  User git\\n  StrictHostKeyChecking accept-new\\n' > ~/.ssh/config
git config --global user.name runner && git config --global user.email runner@two-creatures
for r in a b; do
  rm -rf /tmp/r && cp -R /ref/repo-$r /tmp/r && cd /tmp/r
  ${env}git init -q -b main && git add -A && git commit -q -m "reference app, repo-$r"
  git push -q world:repo-$r.git HEAD:main HEAD:deploy 2>&1 | grep -v '^remote: world' || true
done`, [`${state}:/keys:ro`, `${HERE}/reference:/ref:ro`]);
  say(`reference deployed${breakWhat ? ` with BREAK=${breakWhat}` : ""}`);
}

function deployStatus() {
  return JSON.parse(docker(["exec", "tc-world", "wget", "-qO-", "http://127.0.0.1:7001/status"], { allowFail: true }) ?? "{}");
}

function startAgents() {
  names.forEach((n, i) => {
    // KIND picks the part (fake.mjs); liar is the fake that also claims done before anything is published; a model
    // kind runs model.mjs with the key and limits in its environment, never on its command line
    const model = MODEL_KINDS.includes(agentKinds[i]);
    // a model-driven agent runs under its own process name, so the model's own clean-ups (`pkill node`, `pkill -x
    // node`) kill its test servers and not the driver (DD-M with roles, 2026-10-31: creature-b killed itself that way)
    if (model) docker(["exec", `tc-creature-${n}`, "sh", "-c", 'cp -n "$(command -v node)" /usr/local/bin/creature-agent']);
    const cmd = model ? "creature-agent /x/agents/model.mjs" : `KIND=${agentKinds[i]} ${agentKinds[i] === "liar" ? "LIE=1 " : ""}node /x/agents/fake.mjs`;
    const env = model ? Object.entries(agentEnv()).flatMap(([k, v]) => ["-e", `${k}=${v}`]) : [];
    docker(["exec", "-d", "-u", "creature", "-w", "/home/creature", ...env, `tc-creature-${n}`, "sh", "-c", `${cmd} > /home/creature/agent.log 2>&1; echo $? > /home/creature/agent.exit`]);
    say(`agent on creature-${n}: ${agentKinds[i]}${model ? ` (${agentEnv().DEEPSEEK_MODEL})` : ""}`);
  });
}

/** What each model-driven creature spent, from the file its agent writes at the end. */
function agentUsage() {
  if (!agentKinds.some((k) => MODEL_KINDS.includes(k))) return undefined;
  return Object.fromEntries(names.map((n) => {
    const text = docker(["exec", `tc-creature-${n}`, "cat", "/home/creature/agent-usage.json"], { allowFail: true });
    try { return [n, text ? JSON.parse(text) : null]; } catch { return [n, null]; }
  }));
}

const finished = (n) => docker(["exec", `tc-creature-${n}`, "test", "-f", "/home/creature/agent.exit"], { allowFail: true }) !== null;

/**
 * The cells' reputation after the job (docs/design-ratings.md, R2): the experiment owner rates each creature's cell on
 * the ledger arms, from the run's own verdicts (shipped, its false claims, the contract kept when a plan was accepted
 * and the app shipped), one owner-signed entry each, written in the owner's home and kept in the record as
 * reputation.jsonl. This run's creatures never see them (their world is torn down); a later arm that carries the
 * owner's ledger into the next run does (X-D11).
 */
function rate(summary) {
  if (reference || !ledger || Object.keys(cells).length < 2) return;
  const results = Object.entries(cells).map(([cellId, n]) => ({
    cell: cellId,
    shipped: Boolean(summary.shipped),
    falseClaims: summary.claims.filter((c) => c.who === n && !c.true).length,
    contractKept: Boolean(summary.shipped) && (summary.turns?.plannedBy?.length ?? 0) > 0,
  }));
  try {
    writeFileSync(`${state}/owner/results.json`, JSON.stringify(results));
    const lines = asOwner(`creatura cell rate ${own()} --job ${runId} --results /owner/results.json`);
    writeFileSync(`${out}/reputation.jsonl`, (lines ?? "").trim() + "\n");
    say(`rated ${results.length} cells (reputation.jsonl)`);
  } catch (e) {
    say(`rating failed: ${String(e.message ?? e).split("\n")[0]}`);
  }
}

function collect(summary) {
  const cp = (from, to) => docker(["cp", from, `${out}/${to}`], { allowFail: true });
  cp("tc-world:/srv/deploy/logs", "deploy-logs");
  if (board) cp("tc-world:/srv/board/messages.jsonl", "messages.jsonl");
  if (typedBoard) {
    // the owner reads the talk in their language: every landed message decoded, then the digest a person can take in
    const landed = boardMessages(), refused = refusedMessages();
    writeFileSync(`${out}/refused.jsonl`, refused.map((r) => JSON.stringify(r)).join("\n") + (refused.length ? "\n" : ""));
    writeFileSync(`${out}/messages.${lang}.md`, landed.map((m) => `#${m.n} ${m.at.slice(11, 19)}  ${m.message ? decode(m.message, m.from, lang) : m.text}
      ${m.text}`).join("\n") + "\n");
    writeFileSync(`${out}/digest.${lang}.md`, digest(landed, refused, { lang }).join("\n") + "\n");
  }
  for (const r of ["repo-a", "repo-b"]) {
    // written under /srv, not /tmp: world's /tmp is a tmpfs, which `docker cp` cannot read (no bundle in any record before 2026-10-31)
    if (docker(["exec", "tc-world", "git", "-c", "safe.directory=*", "--git-dir", `/srv/git/${r}.git`, "bundle", "create", `/srv/${r}.bundle`, "--all"], { allowFail: true }) !== null) cp(`tc-world:/srv/${r}.bundle`, `${r}.bundle`);
  }
  for (const n of names) {
    cp(`tc-creature-${n}:/home/creature/agent.log`, `agent-${n}.log`);
    cp(`tc-creature-${n}:/home/creature/agent-transcript.jsonl`, `agent-${n}-transcript.jsonl`);
    if (ledger) cp(`tc-creature-${n}:/home/creature/.creatura/ledger.json`, `ledger-${n}.json`);
  }
  for (const f of ["deployer", "hub", "board", "sshd"]) cp(`tc-world:/var/log/${f}.log`, `world-${f}.log`);
  writeFileSync(`${out}/summary.json`, JSON.stringify(summary, null, 1) + "\n");
}

async function main() {
  say(`run ${runId}`);
  await setUp();
  const started = Date.now();
  if (reference) {
    deployReference();
    if (breakWhat !== "down") await until("reference running", () => ["repo-a", "repo-b"].every((r) => deployStatus()[r]?.state === "running"), 600);
  } else {
    startAgents();
  }

  let last = reference ? judge("reference") : null;
  let firstShip = last?.shipped ? last.elapsed : null;
  let lastCheck = Date.now();
  while (!reference) {
    await sleep(2000);
    takeRefused();
    const r = takeClaims();
    if (r) {
      last = r;
      if (r.shipped && firstShip === null) firstShip = r.elapsed;
    }
    if (Date.now() - lastCheck >= checkEvery * 1000) {
      last = judge("periodic");
      lastCheck = Date.now();
      if (last.shipped && firstShip === null) firstShip = last.elapsed;
    }
    if (names.every(finished)) { say("every agent has stopped"); break; }
    if (Date.now() - started > minutes * 60000) { say(`time limit (${minutes} min)`); break; }
  }
  if (!reference) {
    // A last look: a claim made just before the agents stopped is judged now, with the final state.
    takeRefused();
    takeClaims();
    last = judge("final");
  }
  if (last.shipped && firstShip === null) firstShip = last.elapsed;

  const summary = {
    run: runId, arm, agents: agentKinds, channel, roles: roles || undefined, reference: reference ? breakWhat || "intact" : undefined, judge: uxJudge ? "ux" : "script",
    score: last.score, shipped: last.shipped, firstShipSeconds: firstShip,
    seconds: Math.round((Date.now() - t0) / 1000),
    claims: claims.map(({ key, ...c }) => c), falseClaims: claims.filter((c) => !c.true).length,
    messages: boardMessages().length,
    talk: board ? { characters: boardMessages().reduce((n, m) => n + m.text.length, 0) } : undefined,
    typed: typedBoard ? {
      language: "cellspeak/0", lang,
      refused: refusedMessages().length,
      refusedByReason: refusedMessages().reduce((o, r) => ({ ...o, [r.reason]: (o[r.reason] ?? 0) + 1 }), {}),
      refusedClaims,
      notes: boardMessages().filter((m) => m.message?.type === "note").length,
      digest: digest(boardMessages(), refusedMessages(), { lang }),
    } : undefined,
    ledgerEntries: Object.fromEntries(names.map((n) => [n, ledgerEntries(n).length])),
    turns: turns(),
    usage: agentUsage(),
    agentsExited: Object.fromEntries(names.map((n) => [n, finished(n)])),
  };
  collect(summary);
  rate(summary);
  say(`result: ${summary.score}/5${summary.shipped ? ", shipped" : ""}; record in ${out}`);
  return summary;
}

main()
  .catch((e) => {
    console.error(`run: ${e.message}`);
    writeFileSync(`${out}/error.txt`, String(e.stack ?? e));
    process.exitCode = 1;
  })
  .finally(() => {
    if (!keep) teardown();
    if (!keep) {
      try { rmSync(state, { recursive: true, force: true }); }
      catch (e) { console.error(`run: could not remove ${state} (${e.code}); the record is complete, remove it by hand`); }
    }
  });
