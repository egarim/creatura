// The message board, on world:7000, in the arms with messages: a plain shared chat with no structure, or, in the arms
// with typed messages (BOARD_MODE=typed, channel MT), a board that takes only cellspeak v0 (lang/v0.mjs, issue #47).
//   POST /say     {"from": "a", "text": "..."}  -> {"n": 7}
//   GET  /listen?since=6                         -> [{"n": 7, "at": "...", "from": "a", "text": "..."}]
// Every message is kept in /srv/board/messages.jsonl, which the runner collects.
//
// On a typed board a message lands only when it parses, keeps the board's rules (admit) and, when it claims something
// about the world, when the world agrees: a done's and an offer's checks must pass, a need's check must still fail.
// The world runs them itself: the deployer's status, the git repositories, the published app. A refused message gets
// 422 with the reason, never reaches the peer, and is kept in /srv/board/refused.jsonl for the record.
import { createServer } from "node:http";
import { appendFileSync, existsSync, readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { admit, checksFor, parse, GRAMMAR, VERSION } from "./lang/v0.mjs";

const file = "/srv/board/messages.jsonl";
const refusedFile = "/srv/board/refused.jsonl";
const typed = process.env.BOARD_MODE === "typed";
const messages = existsSync(file) ? readFileSync(file, "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l)) : [];
const LIMIT = 8 * 1024;

function reply(res, status, body) {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(body));
}

/** One check, run by the world: whether it holds now, and what was seen. */
async function holds(c) {
  try {
    if (c.kind === "deployed") {
      const status = await fetch("http://127.0.0.1:7001/status", { signal: AbortSignal.timeout(5000) }).then((r) => r.json());
      const state = status[c.repo]?.state ?? "never deployed";
      return { ok: state === "running", saw: `${c.repo} is ${state}` };
    }
    if (c.kind === "file") {
      for (const branch of ["main", "deploy"]) {
        const r = spawnSync("git", ["-c", "safe.directory=*", "--git-dir", `/srv/git/${c.repo}.git`, "cat-file", "-e", `${branch}:${c.path}`]);
        if (r.status === 0) return { ok: true, saw: `${c.repo} has ${c.path} on ${branch}` };
      }
      return { ok: false, saw: `${c.repo} has no ${c.path} on main or deploy` };
    }
    // the app and the deployer answer on world's own ports
    const local = c.url.replace(/^http:\/\/world:/, "http://127.0.0.1:");
    const res = await fetch(local, { signal: AbortSignal.timeout(8000) });
    if (c.kind === "http") return { ok: res.status === c.status, saw: `${c.url} answered ${res.status}` };
    const body = await res.json().catch(() => undefined);
    return { ok: res.ok && Array.isArray(body), saw: `${c.url} answered ${res.status}${Array.isArray(body) ? " with a list" : " without a JSON list"}` };
  } catch (e) {
    return { ok: false, saw: `${c.url ?? c.repo}: no answer (${e.message})` };
  }
}

/** A typed message: refused with the reason, or admitted with what the world saw. */
async function judgeTyped(from, text) {
  const p = parse.safe(text);
  if (!p.ok) return { error: `${p.error}\n${GRAMMAR}`, reason: "language" };
  const rule = admit(p.message, { from, messages });
  if (rule) return { error: rule, reason: rule.startsWith("no change") ? "repeat" : "rule" };
  const { checks, mustPass } = checksFor(p.message);
  const seen = [];
  for (const c of checks) {
    const r = await holds(c);
    seen.push(r.saw);
    if (r.ok !== mustPass) return { error: mustPass ? `the world does not agree: ${r.saw}` : `you don't need it: ${r.saw} already`, reason: mustPass ? "world" : "needless", check: c, seen };
  }
  return { message: p.message, seen };
}

function refuse(res, from, text, { error, reason, check, seen }) {
  appendFileSync(refusedFile, JSON.stringify({ at: new Date().toISOString(), from, text, error: error.split("\n")[0], reason, check, seen }) + "\n");
  reply(res, 422, { error, refused: true });
}

let queue = Promise.resolve(); // one typed message at a time, so the rules see every earlier message

createServer((req, res) => {
  const url = new URL(req.url, "http://world");
  if (req.method === "GET" && url.pathname === "/listen") {
    const since = Number(url.searchParams.get("since") ?? 0) || 0;
    return reply(res, 200, messages.filter((m) => m.n > since));
  }
  if (req.method === "GET" && url.pathname === "/language") return reply(res, 200, { typed, version: typed ? VERSION : null, grammar: typed ? GRAMMAR : null });
  if (req.method === "POST" && url.pathname === "/say") {
    let body = "";
    req.on("data", (chunk) => {
      body += chunk;
      if (body.length > LIMIT) req.destroy();
    });
    req.on("end", () => {
      let m;
      try { m = JSON.parse(body); } catch { return reply(res, 400, { error: "send JSON: {\"from\": ..., \"text\": ...}" }); }
      if (typeof m.from !== "string" || typeof m.text !== "string" || !m.text.trim()) return reply(res, 400, { error: "from and text are required" });
      const from = m.from.slice(0, 40);
      const land = (text, extra = {}) => {
        const message = { n: messages.length + 1, at: new Date().toISOString(), from, text: text.slice(0, 4000), ...extra };
        messages.push(message);
        appendFileSync(file, JSON.stringify(message) + "\n");
        reply(res, 200, { n: message.n });
      };
      if (!typed) return land(m.text);
      queue = queue.then(async () => {
        const text = m.text.trim();
        const r = await judgeTyped(from, text);
        if (r.error) return refuse(res, from, text, r);
        land(text, { message: r.message, ...(r.seen.length ? { seen: r.seen } : {}) });
      }).catch((e) => reply(res, 500, { error: e.message }));
    });
    return;
  }
  reply(res, 404, { error: "POST /say, GET /listen?since=<n> or GET /language" });
}).listen(7000, () => console.log(`board: listening on 7000${typed ? ` (typed: ${VERSION})` : ""}`));
