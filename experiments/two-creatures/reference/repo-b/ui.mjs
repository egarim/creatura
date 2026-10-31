// The reference app's UI half (repo-b): serves the page on 8080 and passes /api/* to the server half, which it
// reaches by service name (`api`) over world's shared network. Its BREAK switches each break one step of the
// outside test: label (no "Item" label), live (no updates without a reload), check (the checkbox is not saved).
import { createServer, request } from "node:http";

const BREAK = process.env.BREAK ?? "";
const label = BREAK === "label" ? "Thing" : "Item";
const live = BREAK !== "live";
const saveCheck = BREAK !== "check";

const page = `<!doctype html>
<meta charset="utf-8">
<title>Shopping list</title>
<h1>Shopping list</h1>
<form id="add"><label for="item">${label}</label> <input id="item" name="item" autocomplete="off"> <button type="submit">Add</button></form>
<ul id="list"></ul>
<script>
const list = document.getElementById("list");
async function load() {
  const items = await (await fetch("/api/items")).json();
  list.replaceChildren(...items.map((i) => {
    const li = document.createElement("li");
    const box = document.createElement("input");
    box.type = "checkbox";
    box.id = "item-" + i.id;
    box.checked = i.bought;
    box.onchange = async () => { ${saveCheck ? 'await fetch("/api/items/" + i.id, { method: "PATCH", body: JSON.stringify({ bought: box.checked }) });' : ""} };
    const name = document.createElement("label");
    name.htmlFor = box.id;
    name.textContent = i.name;
    li.append(box, " ", name);
    return li;
  }));
}
document.getElementById("add").onsubmit = async (e) => {
  e.preventDefault();
  const input = document.getElementById("item");
  if (!input.value.trim()) return;
  await fetch("/api/items", { method: "POST", body: JSON.stringify({ name: input.value }) });
  input.value = "";
  load();
};
load();
${live ? "setInterval(() => { if (!list.contains(document.activeElement)) load(); }, 1000);" : ""}
</script>
`;

createServer((req, res) => {
  if (req.url.startsWith("/api/")) {
    const upstream = request({ host: "api", port: 3000, path: req.url, method: req.method, headers: req.headers }, (r) => {
      res.writeHead(r.statusCode, r.headers);
      r.pipe(res);
    });
    upstream.on("error", () => {
      res.writeHead(502, { "content-type": "application/json" });
      res.end('{"error":"the api is not reachable"}');
    });
    return req.pipe(upstream);
  }
  res.writeHead(200, { "content-type": "text/html; charset=utf-8" });
  res.end(page);
}).listen(8080, () => console.log("ui: listening on 8080"));
