// The message board, on world:7000, in the arms with messages: a plain shared chat with no structure.
//   POST /say     {"from": "a", "text": "..."}  -> {"n": 7}
//   GET  /listen?since=6                         -> [{"n": 7, "at": "...", "from": "a", "text": "..."}]
// Every message is kept in /srv/board/messages.jsonl, which the runner collects.
import { createServer } from "node:http";
import { appendFileSync, existsSync, readFileSync } from "node:fs";

const file = "/srv/board/messages.jsonl";
const messages = existsSync(file) ? readFileSync(file, "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l)) : [];
const LIMIT = 8 * 1024;

function reply(res, status, body) {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(body));
}

createServer((req, res) => {
  const url = new URL(req.url, "http://world");
  if (req.method === "GET" && url.pathname === "/listen") {
    const since = Number(url.searchParams.get("since") ?? 0) || 0;
    return reply(res, 200, messages.filter((m) => m.n > since));
  }
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
      const message = { n: messages.length + 1, at: new Date().toISOString(), from: m.from.slice(0, 40), text: m.text.slice(0, 4000) };
      messages.push(message);
      appendFileSync(file, JSON.stringify(message) + "\n");
      reply(res, 200, { n: message.n });
    });
    return;
  }
  reply(res, 404, { error: "POST /say or GET /listen?since=<n>" });
}).listen(7000, () => console.log("board: listening on 7000"));
