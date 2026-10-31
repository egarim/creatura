// The deployer, on world:7001. A push to a repository's deploy branch (queued by the post-receive hook) is checked
// out and started with `docker compose`, as project pub-<repo>, every service also attached to the shared network
// `pub` so the two repositories' services can reach each other by service name. Whatever publishes port 8080 is
// the published app at http://world:8080/.
//   GET  /status               -> {"repo-a": {"sha": ..., "state": "running" | "failed" | "deploying", "at": ...}}
//   GET  /deploy/<repo>/log    -> the last deploys' output, as text
//   POST /restart              -> restart every published container (the outside test's step 5)
import { createServer } from "node:http";
import { appendFileSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { spawn } from "node:child_process";

const QUEUE = "/srv/deploy/queue";
const WORK = "/srv/deploy/work";
const LOGS = "/srv/deploy/logs";
const REPOS = ["repo-a", "repo-b"];
const status = {};
let busy = Promise.resolve();

function run(cmd, args, opts = {}) {
  return new Promise((resolve) => {
    const child = spawn(cmd, args, { ...opts, stdio: ["ignore", "pipe", "pipe"] });
    let out = "";
    child.stdout.on("data", (d) => (out += d));
    child.stderr.on("data", (d) => (out += d));
    child.on("close", (code) => resolve({ code, out }));
  });
}

function log(repo, text) {
  appendFileSync(`${LOGS}/${repo}.log`, `[${new Date().toISOString()}] ${text.trimEnd()}\n`);
}

const composeFile = (dir) => ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"].find((f) => existsSync(`${dir}/${f}`));

async function deploy(repo, sha) {
  status[repo] = { sha, state: "deploying", at: new Date().toISOString() };
  log(repo, `deploying ${sha.slice(0, 7)}`);
  const dir = `${WORK}/${repo}`;
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  const archive = await run("sh", ["-c", `git --git-dir=/srv/git/${repo}.git archive ${sha} | tar -x -C ${dir}`]);
  if (archive.code !== 0) return fail(repo, sha, `could not check out ${sha}:\n${archive.out}`);
  const file = composeFile(dir);
  if (!file) return fail(repo, sha, "no compose.yaml at the root of the deploy branch");
  const services = await run("docker", ["compose", "-f", file, "config", "--services"], { cwd: dir });
  if (services.code !== 0) return fail(repo, sha, `compose.yaml does not parse:\n${services.out}`);
  const names = services.out.split("\n").map((s) => s.trim()).filter(Boolean);
  const override = ["services:", ...names.flatMap((n) => [`  ${n}:`, "    networks: [default, pub]"]), "networks:", "  default: {}", "  pub:", "    external: true", ""].join("\n");
  writeFileSync(`${dir}/.world-override.yaml`, override);
  const up = await run("docker", ["compose", "-p", `pub-${repo}`, "-f", file, "-f", ".world-override.yaml", "up", "-d", "--build", "--remove-orphans"], { cwd: dir });
  log(repo, up.out);
  if (up.code !== 0) return fail(repo, sha, `docker compose up failed (exit ${up.code})`);
  status[repo] = { sha, state: "running", at: new Date().toISOString() };
  log(repo, `running ${sha.slice(0, 7)}`);
}

function fail(repo, sha, why) {
  status[repo] = { sha, state: "failed", at: new Date().toISOString(), why };
  log(repo, `FAILED: ${why}`);
}

setInterval(() => {
  for (const repo of readdirSync(QUEUE)) {
    if (!REPOS.includes(repo)) continue;
    const sha = readFileSync(`${QUEUE}/${repo}`, "utf8").trim();
    rmSync(`${QUEUE}/${repo}`, { force: true });
    if (!/^[0-9a-f]{40}$/.test(sha)) continue;
    busy = busy.then(() => deploy(repo, sha)).catch((e) => fail(repo, sha, String(e)));
  }
}, 1000);

async function restartAll() {
  const ids = await run("docker", ["ps", "-q", "--filter", "label=com.docker.compose.project"]);
  const list = ids.out.split("\n").filter(Boolean);
  if (list.length) await run("docker", ["restart", ...list]);
  return list.length;
}

createServer(async (req, res) => {
  const url = new URL(req.url, "http://world");
  const m = url.pathname.match(/^\/deploy\/(repo-[ab])\/log$/);
  if (req.method === "GET" && url.pathname === "/status") {
    res.writeHead(200, { "content-type": "application/json" });
    return res.end(JSON.stringify(status));
  }
  if (req.method === "GET" && m) {
    const file = `${LOGS}/${m[1]}.log`;
    res.writeHead(200, { "content-type": "text/plain; charset=utf-8" });
    return res.end(existsSync(file) ? readFileSync(file, "utf8").slice(-64 * 1024) : "no deploy yet\n");
  }
  if (req.method === "POST" && url.pathname === "/restart") {
    await busy;
    const n = await restartAll();
    res.writeHead(200, { "content-type": "application/json" });
    return res.end(JSON.stringify({ restarted: n }));
  }
  res.writeHead(404, { "content-type": "text/plain" });
  res.end("GET /status, GET /deploy/<repo>/log, POST /restart\n");
}).listen(7001, () => console.log("deployer: listening on 7001"));
