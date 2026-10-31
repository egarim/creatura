#!/usr/bin/env node
// A creature driven by a hosted model: the first real agent of the harness (docs/design-two-creatures.md, slice X1,
// brought forward because the Claude Code cloud sessions of docs/design-turns.md T2 ran out of credits). It reads
// its name, peer and channel from ~/.creature.env, fills in the goal of agents/goal.md for its arm, and gives the
// model two tools: `shell`, one bash command on this machine, and `stop`. Everything else is the model's: what to
// build, how to talk to its peer, when to say done. The model is DeepSeek through its OpenAI-compatible API
// (DEEPSEEK_API_KEY, DEEPSEEK_MODEL, DEEPSEEK_BASE_URL), as the convoy's seats use it; the key never reaches the
// shell the model runs, the record or the log. It stops when the model says so, or at AGENT_MINUTES, AGENT_STEPS or
// AGENT_BUDGET (USD). Every call and command goes to ~/agent-transcript.jsonl, what it spent to ~/agent-usage.json.
import { spawnSync } from "node:child_process";
import { appendFileSync, readFileSync, writeFileSync } from "node:fs";

const HOME = process.env.HOME;
const env = Object.fromEntries(readFileSync(`${HOME}/.creature.env`, "utf8").split("\n").filter((l) => l.includes("=")).map((l) => l.split("=", 2)));
const { NAME: name, PEER: peer, CHANNEL: channel } = env;
const key = process.env.DEEPSEEK_API_KEY || process.env.deepseek || "";
const model = process.env.DEEPSEEK_MODEL || "deepseek-chat";
const base = (process.env.DEEPSEEK_BASE_URL || "https://api.deepseek.com").replace(/\/+$/, "");
const minutes = Number(process.env.AGENT_MINUTES || 120);
const maxSteps = Number(process.env.AGENT_STEPS || 200);
const budget = Number(process.env.AGENT_BUDGET || 2);
/** USD per million tokens, the catalog's figures for deepseek-chat (ModelCatalog.cs); cache hits counted at the input price. */
const PRICE = { input: 0.28, output: 0.42 };
const TRANSCRIPT = `${HOME}/agent-transcript.jsonl`;
const USAGE = `${HOME}/agent-usage.json`;
const started = Date.now();
const deadline = started + Math.max(1, minutes - 2) * 60000;

const log = (s) => console.log(`${new Date().toISOString()} ${name}: ${s}`);
const record = (entry) => appendFileSync(TRANSCRIPT, JSON.stringify({ at: new Date().toISOString(), ...entry }) + "\n");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- the goal, from agents/goal.md, filled in for this arm ---------------------------------------------------

/** The lines quoted (`> `) right after a marker line in goal.md, joined; the file is the one source of the words. */
function quoted(text, marker) {
  const lines = text.split("\n");
  const at = lines.findIndex((l) => l.startsWith(marker));
  if (at < 0) throw new Error(`goal.md has no "${marker}"`);
  const out = [];
  for (let i = at + 1; i < lines.length && lines[i].startsWith(">"); i++) out.push(lines[i].replace(/^> ?/, ""));
  return out.join("\n").trim();
}

function goal() {
  const text = readFileSync(process.env.GOAL_FILE || "/x/agents/goal.md", "utf8");
  const template = text.split(/^---$/m)[1].trim();
  const solo = channel === "0";
  const ledger = channel === "L" || channel === "LM";
  const board = channel === "M" || channel === "LM";
  const together = solo ? quoted(text, "alone:") : quoted(text, "**`{{together}}`**, in a pair:").replaceAll("{{peer}}", peer);
  const repos = solo ? "you own both and can push to both" : `you own \`repo-${name}\` and can push only to it; you can read \`repo-${peer}\``;
  const channelText = [ledger ? quoted(text, "**`{{channel}}`**, ledger (L):") : "", board ? quoted(text, "messages (M):") : ""].filter(Boolean).join("\n\n");
  const done = solo ? "in your final message" : ledger && board ? "with `cell release --evidence`, and on the board in a message that starts with DONE"
    : ledger ? "with `cell release --evidence`" : "on the board, in a message that starts with DONE";
  return template.replaceAll("{{together}}", together).replaceAll("{{repos}}", repos).replaceAll("{{channel}}", channelText).replaceAll("{{done}}", done);
}

// ---- the tools ------------------------------------------------------------------------------------------------

const tools = [
  {
    type: "function",
    function: {
      name: "shell",
      description: "Run one bash command on your machine, as the user creature, from your home directory. Returns its exit code, stdout and stderr (a long output is cut in the middle). Use it for everything: files, git, the cell command, say and listen, curl, and sleep (at most 120 seconds) to wait for your peer.",
      parameters: { type: "object", properties: { command: { type: "string", description: "the bash command" } }, required: ["command"] },
    },
  },
  {
    type: "function",
    function: {
      name: "stop",
      description: "Stop for good: you have said done through the channel, with the evidence, or you cannot go on. One line on where things stand.",
      parameters: { type: "object", properties: { summary: { type: "string" } }, required: ["summary"] },
    },
  },
];

const CUT = 6000;
const cut = (s) => (s.length <= CUT ? s : `${s.slice(0, 4000)}\n…[${s.length - 5500} characters cut]…\n${s.slice(-1500)}`);

/** One command, with the creature's name, peer, channel and world in its environment and the model's key kept out of it; three minutes at most. */
function shell(command) {
  const t0 = Date.now();
  const childEnv = { ...process.env, ...env };
  for (const k of ["DEEPSEEK_API_KEY", "deepseek", "DEEPSEEK_MODEL", "DEEPSEEK_BASE_URL", "AGENT_MINUTES", "AGENT_STEPS", "AGENT_BUDGET"]) delete childEnv[k];
  const r = spawnSync("bash", ["-lc", command], { cwd: HOME, encoding: "utf8", env: childEnv, timeout: 180000, maxBuffer: 16 * 1024 * 1024 });
  const exit = r.status ?? (r.signal ? `killed (${r.signal})` : "?");
  const output = cut(`${r.stdout ?? ""}${r.stderr ? `\n[stderr]\n${r.stderr}` : ""}`.trim());
  return { exit, ms: Date.now() - t0, output, text: `exit ${exit}\n${output || "(no output)"}` };
}

// ---- the model ------------------------------------------------------------------------------------------------

const usage = { model, calls: 0, steps: 0, promptTokens: 0, completionTokens: 0, cacheHitTokens: 0, cost: 0, stoppedBy: null, seconds: 0 };

/** One chat completion, with tools; retried on a busy or failing endpoint, never on a refused key. */
async function chat(messages) {
  for (let attempt = 1; ; attempt++) {
    let res;
    try {
      res = await fetch(`${base}/chat/completions`, {
        method: "POST",
        headers: { authorization: `Bearer ${key}`, "content-type": "application/json" },
        body: JSON.stringify({ model, messages, tools, tool_choice: "auto", max_tokens: 4096 }),
        signal: AbortSignal.timeout(180000),
      });
    } catch (e) {
      if (attempt >= 5) throw new Error(`the model endpoint did not answer: ${e.message}`);
      log(`model call failed (${e.message}); retry ${attempt}`);
      await sleep(2000 * 2 ** attempt);
      continue;
    }
    if (res.status === 429 || res.status >= 500) {
      if (attempt >= 5) throw new Error(`the model endpoint answered ${res.status} five times`);
      log(`model answered ${res.status}; retry ${attempt}`);
      await sleep(2000 * 2 ** attempt);
      continue;
    }
    if (!res.ok) throw new Error(`the model endpoint answered ${res.status}: ${(await res.text()).slice(0, 300)}`);
    const body = await res.json();
    usage.calls++;
    const u = body.usage ?? {};
    usage.promptTokens += u.prompt_tokens ?? 0;
    usage.completionTokens += u.completion_tokens ?? 0;
    usage.cacheHitTokens += u.prompt_cache_hit_tokens ?? 0;
    usage.cost = ((usage.promptTokens * PRICE.input) + (usage.completionTokens * PRICE.output)) / 1e6;
    return body.choices?.[0]?.message ?? { role: "assistant", content: "" };
  }
}

/** Keeps the thread inside the model's window: old tool outputs are shortened, the commands and the model's words stay. */
function trim(messages) {
  const keep = 30;
  for (let i = 2; i < messages.length - keep; i++) {
    const m = messages[i];
    if (m.role === "tool" && typeof m.content === "string" && m.content.length > 400) m.content = m.content.slice(0, 300) + "\n…(cut, an earlier step)";
  }
}

async function main() {
  if (!key) throw new Error("no key: DEEPSEEK_API_KEY (or deepseek) is not set");
  const text = goal();
  const solo = channel === "0";
  log(`start (${model}), channel ${channel}${solo ? ", alone" : `, peer ${peer}`}; ${minutes} min, ${maxSteps} steps, USD ${budget}`);
  record({ kind: "start", name, peer, channel, model, minutes, maxSteps, budget, goal: text });

  const system = `You are creature-${name}, an agent on your own Linux machine, with a shell. The goal below is your only instruction; nobody will message you. Act by calling the shell tool, one command per call, and think briefly between calls. Keep to the goal's rules. You have ${minutes} minutes and ${maxSteps} commands. When the app is published and you have said so through the channel, call stop.`;
  const messages = [{ role: "system", content: system }, { role: "user", content: text }];
  let idle = 0;

  loop: for (;;) {
    if (Date.now() > deadline) { usage.stoppedBy = "time"; break; }
    if (usage.steps >= maxSteps) { usage.stoppedBy = "steps"; break; }
    if (usage.cost >= budget) { usage.stoppedBy = "budget"; break; }
    trim(messages);
    const reply = await chat(messages);
    messages.push(reply);
    record({ kind: "assistant", step: usage.steps, content: reply.content ?? "", tool_calls: reply.tool_calls ?? [], usage: { ...usage } });
    const calls = reply.tool_calls ?? [];
    if (calls.length === 0) {
      if (reply.content) log(`says: ${String(reply.content).replace(/\s+/g, " ").slice(0, 300)}`);
      if (++idle >= 3) { usage.stoppedBy = "idle"; break; }
      messages.push({ role: "user", content: "(No tool call. Call shell to act, or stop when you are done.)" });
      continue;
    }
    idle = 0;
    for (const call of calls) {
      let args = {};
      try { args = JSON.parse(call.function?.arguments || "{}"); } catch { args = {}; }
      if (call.function?.name === "stop") {
        log(`stop: ${args.summary ?? ""}`);
        record({ kind: "stop", step: usage.steps, summary: args.summary ?? "" });
        messages.push({ role: "tool", tool_call_id: call.id, content: "stopped" });
        usage.stoppedBy = "stop";
        break loop;
      }
      if (call.function?.name !== "shell" || typeof args.command !== "string") {
        messages.push({ role: "tool", tool_call_id: call.id, content: "unknown tool or missing command: use shell {command} or stop {summary}" });
        continue;
      }
      usage.steps++;
      const r = shell(args.command);
      log(`$ ${args.command.replace(/\s+/g, " ").slice(0, 200)} -> exit ${r.exit} (${r.ms} ms, ${r.output.length} chars)`);
      record({ kind: "shell", step: usage.steps, command: args.command, exit: r.exit, ms: r.ms, output: r.output.slice(0, 4000) });
      messages.push({ role: "tool", tool_call_id: call.id, content: r.text });
    }
  }

  usage.seconds = Math.round((Date.now() - started) / 1000);
  writeFileSync(USAGE, JSON.stringify(usage, null, 1) + "\n");
  record({ kind: "end", ...usage });
  log(`end: ${usage.stoppedBy}; ${usage.steps} commands, ${usage.calls} calls, ${usage.promptTokens} in / ${usage.completionTokens} out tokens (${usage.cacheHitTokens} cache hits), USD ${usage.cost.toFixed(4)}, ${usage.seconds} s`);
}

main().catch((e) => {
  log(`failed: ${e.message}`);
  usage.stoppedBy = usage.stoppedBy ?? "error";
  usage.seconds = Math.round((Date.now() - started) / 1000);
  try { writeFileSync(USAGE, JSON.stringify({ ...usage, error: e.message }, null, 1) + "\n"); } catch { /* nothing to do */ }
  process.exitCode = 1;
});
