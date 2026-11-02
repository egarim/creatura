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

const HERE = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const NET = "tc-sandbox";
const SUBNET = "100.127.200.0/24";
const IP = { world: "100.127.200.10", a: "100.127.200.21", b: "100.127.200.22" };
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
const roles = argv.includes("--roles"); // the parts written in: a the server, b the UI (goal.md)
if (!arm) usage("--arm is required");
const channel = reference ? "none" : arm.split("-")[1];
const solo = channel === "0";
if (!reference && !["L", "LM", "M", "0"].includes(channel)) usage(`unknown channel in ${arm}`);
const agentKinds = reference ? [] : (opt("--agents") ?? usage("--agents is required")).split(",");
if (!reference && agentKinds.length !== (solo ? 1 : 2)) usage(solo ? "an arm alone takes one agent" : "a pair takes two agents, a,b");
const TURN_KINDS = ["polite", "simultaneous"];
const MODEL_KINDS = ["deepseek"];
for (const k of agentKinds) if (!["fake", "liar", ...TURN_KINDS, ...MODEL_KINDS].includes(k)) usage(`agent '${k}' is wired in slice X1; this runner takes fake, liar, polite, simultaneous and deepseek`);
const ledger = channel === "L" || channel === "LM";
const board = channel === "M" || channel === "LM";
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
  console.error(`run: ${why}\nusage: run.mjs --arm <CC-L|...|C-0> --agents <kind>[,<kind>] [--minutes n] [--check-every s] [--keep] [--steps n] [--budget usd] [--roles]\n       run.mjs --reference [label|live|check|persist|down]`);
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

/** A one-shot shell on the experiment owner's vessel (the same machine-id every time). */
function asOwner(script, mounts = []) {
  return docker(["run", "--rm", "-v", `${state}/owner:/owner`, ...mounts.flatMap((m) => ["-v", m]), "--entrypoint", "sh", "tc-creature", "-c", `cp /owner/machine-id /etc/machine-id; ${script}`]);
}

const own = (n) => `--home /owner --creatura owner --no-mesh`;

function teardown() {
  docker(["rm", "-f", "tc-world", "tc-creature-a", "tc-creature-b"], { allowFail: true });
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
  writeFileSync(`${state}/world/world.env`, `BOARD=${board ? "on" : "off"}\nHUB=${ledger ? "on" : "off"}\nWORLD_IP=${IP.world}\n`);

  // the images the app's builds start from, loaded into world's own Docker so a reset doesn't pull them again
  const preload = join(HERE, "build", "preload");
  mkdirSync(preload, { recursive: true });
  if (!existsSync(`${preload}/node-22.tar`)) {
    say("saving node:22-bookworm-slim for world's preload (once)");
    docker(["save", "-o", `${preload}/node-22.tar`, "node:22-bookworm-slim"]);
  }

  say(`world (board ${board ? "on" : "off"}, hub ${ledger ? "on" : "off"})`);
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

function judge(why) {
  const text = docker(["run", "--rm", "--network", NET, "--add-host", `world:${IP.world}`, "tc-judge"], { allowFail: true }) ?? "";
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

/** New claims of done since the last look: a message saying DONE, or a Release entry. */
function newClaims() {
  const found = [];
  for (const m of boardMessages()) {
    const key = `board:${m.n}`;
    if (!seen.has(key) && /\bDONE\b/.test(m.text)) found.push({ key, via: "board", who: m.from, text: m.text.slice(0, 300) });
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

function collect(summary) {
  const cp = (from, to) => docker(["cp", from, `${out}/${to}`], { allowFail: true });
  cp("tc-world:/srv/deploy/logs", "deploy-logs");
  if (board) cp("tc-world:/srv/board/messages.jsonl", "messages.jsonl");
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
    takeClaims();
    last = judge("final");
  }
  if (last.shipped && firstShip === null) firstShip = last.elapsed;

  const summary = {
    run: runId, arm, agents: agentKinds, channel, roles: roles || undefined, reference: reference ? breakWhat || "intact" : undefined,
    score: last.score, shipped: last.shipped, firstShipSeconds: firstShip,
    seconds: Math.round((Date.now() - t0) / 1000),
    claims: claims.map(({ key, ...c }) => c), falseClaims: claims.filter((c) => !c.true).length,
    messages: boardMessages().length, ledgerEntries: Object.fromEntries(names.map((n) => [n, ledgerEntries(n).length])),
    turns: turns(),
    usage: agentUsage(),
    agentsExited: Object.fromEntries(names.map((n) => [n, finished(n)])),
  };
  collect(summary);
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
    if (!keep) rmSync(state, { recursive: true, force: true });
  });
