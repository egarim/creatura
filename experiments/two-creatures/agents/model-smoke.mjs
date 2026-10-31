#!/usr/bin/env node
// A smoke test of agents/model.mjs with no model and no key: a scripted mock of the chat-completions endpoint plays
// the model (a command, a failing command, a turn with no tool call, then stop), and the loop, the goal filled for a
// pair on the ledger, the key kept out of the shell, the transcript and the usage file are checked.
//   node experiments/two-creatures/agents/model-smoke.mjs
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const home = mkdtempSync(join(tmpdir(), "tc-model-"));
writeFileSync(join(home, ".creature.env"), "NAME=a\nPEER=b\nCHANNEL=L\nWORLD=world\n");

const script = [
  { tool: "shell", args: { command: "echo hello from $NAME; echo key=[$DEEPSEEK_API_KEY]; pwd" } },
  { tool: "shell", args: { command: "echo oops >&2; exit 3" } },
  { text: "Thinking it over." },
  { tool: "stop", args: { summary: "smoke: nothing published, as scripted" } },
];
const requests = [];
let n = 0;
const server = createServer((req, res) => {
  let body = "";
  req.on("data", (c) => (body += c));
  req.on("end", () => {
    const j = JSON.parse(body);
    requests.push({ auth: req.headers.authorization, messages: j.messages, tools: j.tools?.map((t) => t.function.name) });
    const step = script[Math.min(n++, script.length - 1)];
    const message = step.tool
      ? { role: "assistant", content: null, tool_calls: [{ id: `call_${n}`, type: "function", function: { name: step.tool, arguments: JSON.stringify(step.args) } }] }
      : { role: "assistant", content: step.text };
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ choices: [{ message }], usage: { prompt_tokens: 1000, completion_tokens: 50, prompt_cache_hit_tokens: 200 } }));
  });
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const base = `http://127.0.0.1:${server.address().port}`;

// The agent runs as a child while this process keeps serving the mock (a synchronous spawn would block the mock).
const run = await new Promise((done) => {
  const child = spawn("node", [join(here, "model.mjs")], {
    env: { ...process.env, HOME: home, GOAL_FILE: resolve(here, "goal.md"), DEEPSEEK_API_KEY: "sk-smoke-secret", DEEPSEEK_BASE_URL: base, DEEPSEEK_MODEL: "mock", AGENT_MINUTES: "5", AGENT_STEPS: "10", AGENT_BUDGET: "1" },
  });
  let stdout = "", stderr = "";
  child.stdout.on("data", (d) => (stdout += d));
  child.stderr.on("data", (d) => (stderr += d));
  const timer = setTimeout(() => child.kill("SIGKILL"), 60000);
  child.on("close", (status) => { clearTimeout(timer); done({ status, stdout, stderr }); });
});
server.close();

const failures = [];
const check = (ok, what) => { if (!ok) failures.push(what); };
check(run.status === 0, `exit ${run.status}: ${run.stderr}`);
check(requests.length === 4, `${requests.length} calls to the model, not 4`);
check(requests[0]?.auth === "Bearer sk-smoke-secret", "the key is not sent as the bearer");
check(requests[0]?.tools?.join() === "shell,stop", `tools offered: ${requests[0]?.tools}`);
const goal = requests[0]?.messages?.[1]?.content ?? "";
check(goal.includes("Your peer is `creature-b`"), "the goal is not filled for the pair");
check(goal.includes("you own `repo-a` and can push only to it; you can read `repo-b`"), "the goal's repos are not filled");
check(goal.includes("cell turn") && goal.includes("cell wait --on"), "the ledger channel text with the turn commands is missing");
check(!goal.includes("say <text>"), "the board's text is in a ledger-only goal");
check(goal.includes("with `cell release --evidence`, with the evidence"), "the done clause is not the ledger's");
check(!goal.includes("{{"), "a placeholder is left unfilled");
const first = requests[1]?.messages?.at(-1)?.content ?? "";
check(first.includes("hello from a") && first.includes("key=[]"), `the first command's output is wrong or the key leaked: ${first.slice(0, 120)}`);
check((requests[2]?.messages?.at(-1)?.content ?? "").startsWith("exit 3"), "the failing command's exit code is not reported");
check((requests[3]?.messages?.at(-1)?.content ?? "").includes("No tool call"), "a turn with no tool call is not nudged");
const usage = existsSync(join(home, "agent-usage.json")) ? JSON.parse(readFileSync(join(home, "agent-usage.json"), "utf8")) : null;
check(usage?.stoppedBy === "stop" && usage?.steps === 2 && usage?.calls === 4, `usage: ${JSON.stringify(usage)}`);
check(Math.abs((usage?.cost ?? 0) - (4000 * 0.28 + 200 * 0.42) / 1e6) < 1e-9, `cost ${usage?.cost}`);
const transcript = existsSync(join(home, "agent-transcript.jsonl")) ? readFileSync(join(home, "agent-transcript.jsonl"), "utf8") : "";
check(transcript.split("\n").filter(Boolean).length === 9, `transcript has ${transcript.split("\n").filter(Boolean).length} lines, not 9`);
check(!transcript.includes("sk-smoke-secret") && !run.stdout.includes("sk-smoke-secret"), "the key is in the transcript or the log");

rmSync(home, { recursive: true, force: true });
if (failures.length) {
  console.log(`model-smoke: FAIL\n- ${failures.join("\n- ")}\n--- agent log ---\n${run.stdout}`);
  process.exit(1);
}
console.log(`model-smoke: ok (4 model calls, 2 commands, stop; the goal filled for a pair on the ledger; the key kept out of the shell and the record)`);
