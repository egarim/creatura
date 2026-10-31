// The reference app's server half (repo-a): the shopping list's HTTP API and its storage.
//   GET   /api/items            -> [{"id": 1, "name": "milk", "bought": false}]
//   POST  /api/items            {"name": "milk"} -> the new item
//   PATCH /api/items/<id>       {"bought": true} -> the item
// It exists to prove the outside test right, not as an answer for the agents: BREAK=persist keeps the list in memory
// only, so step 5 (a restart) must fail.
import { createServer } from "node:http";
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";

const BREAK = process.env.BREAK ?? "";
const FILE = "/data/items.json";
mkdirSync("/data", { recursive: true });
let items = BREAK !== "persist" && existsSync(FILE) ? JSON.parse(readFileSync(FILE, "utf8")) : [];

function save() {
  if (BREAK === "persist") return;
  writeFileSync(FILE + ".tmp", JSON.stringify(items));
  renameSync(FILE + ".tmp", FILE);
}

function body(req) {
  return new Promise((resolve) => {
    let s = "";
    req.on("data", (c) => (s += c));
    req.on("end", () => {
      try { resolve(JSON.parse(s || "{}")); } catch { resolve(null); }
    });
  });
}

function send(res, status, value) {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(value));
}

createServer(async (req, res) => {
  const url = new URL(req.url, "http://api");
  if (url.pathname === "/api/items" && req.method === "GET") return send(res, 200, items);
  if (url.pathname === "/api/items" && req.method === "POST") {
    const b = await body(req);
    if (!b || typeof b.name !== "string" || !b.name.trim()) return send(res, 400, { error: "name is required" });
    const item = { id: items.reduce((m, i) => Math.max(m, i.id), 0) + 1, name: b.name.trim(), bought: false };
    items.push(item);
    save();
    return send(res, 201, item);
  }
  const m = url.pathname.match(/^\/api\/items\/(\d+)$/);
  if (m && req.method === "PATCH") {
    const item = items.find((i) => i.id === Number(m[1]));
    const b = await body(req);
    if (!item) return send(res, 404, { error: "no such item" });
    if (!b || typeof b.bought !== "boolean") return send(res, 400, { error: "bought must be true or false" });
    item.bought = b.bought;
    save();
    return send(res, 200, item);
  }
  send(res, 404, { error: "not found" });
}).listen(3000, () => console.log("api: listening on 3000"));
