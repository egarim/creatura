'use strict';
// Shared shopping list API: dependency-free Node.js, durable JSON storage, SSE live updates.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');

const PORT = parseInt(process.env.PORT || '8080', 10);
const DATA_DIR = process.env.DATA_DIR || path.join(__dirname, 'data');
const DATA_FILE = path.join(DATA_DIR, 'items.json');

fs.mkdirSync(DATA_DIR, { recursive: true });

// ---- durable storage: load on start, atomic write on every change ----
let state = { nextId: 1, items: [] };
try {
  const raw = fs.readFileSync(DATA_FILE, 'utf8');
  const parsed = JSON.parse(raw);
  if (parsed && Array.isArray(parsed.items)) {
    state.items = parsed.items.map(it => ({ id: Number(it.id), text: String(it.text), bought: !!it.bought }));
    state.nextId = Math.max(Number(parsed.nextId) || 1, ...state.items.map(i => i.id + 1), 1);
  }
} catch (e) {
  if (e.code !== 'ENOENT') console.error('could not load data file, starting empty:', e.message);
}

function save() {
  const tmp = DATA_FILE + '.tmp';
  const fd = fs.openSync(tmp, 'w');
  try {
    fs.writeSync(fd, JSON.stringify(state));
    fs.fsyncSync(fd);
  } finally { fs.closeSync(fd); }
  fs.renameSync(tmp, DATA_FILE);
  try { const dfd = fs.openSync(DATA_DIR, 'r'); fs.fsyncSync(dfd); fs.closeSync(dfd); } catch (_) {}
}

// ---- SSE subscribers ----
const clients = new Set();
function broadcast() {
  const payload = 'event: items\ndata: ' + JSON.stringify(state.items) + '\n\n';
  for (const res of clients) { try { res.write(payload); } catch (_) {} }
}
function sendItems(res) {
  res.write('event: items\ndata: ' + JSON.stringify(state.items) + '\n\n');
}
setInterval(() => { for (const res of clients) { try { res.write(': ping\n\n'); } catch (_) {} } }, 15000).unref();

// ---- helpers ----
function cors(res) {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, PATCH, DELETE, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
  res.setHeader('Access-Control-Max-Age', '86400');
}
function json(res, code, obj) {
  const body = JSON.stringify(obj);
  cors(res);
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(body), 'Cache-Control': 'no-store' });
  res.end(body);
}
function readBody(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    req.on('data', c => { data += c; if (data.length > 1e6) { reject(new Error('too large')); req.destroy(); } });
    req.on('end', () => {
      if (!data) return resolve({});
      try { resolve(JSON.parse(data)); } catch (_) { reject(new Error('invalid json')); }
    });
    req.on('error', reject);
  });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const p = url.pathname.replace(/\/+$/, '') || '/';
  const method = req.method;

  if (method === 'OPTIONS') { cors(res); res.writeHead(204); return res.end(); }

  try {
    if (method === 'GET' && (p === '/health' || p === '/')) return json(res, 200, { ok: true, items: state.items.length });

    if (method === 'GET' && p === '/items') {
      // bare array (agreed contract); ?envelope=1 gives {"items":[...]}
      if (url.searchParams.get('envelope') === '1') return json(res, 200, { items: state.items });
      return json(res, 200, state.items);
    }

    if (method === 'GET' && p === '/events') {
      cors(res);
      res.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8', 'Cache-Control': 'no-cache, no-transform', Connection: 'keep-alive', 'X-Accel-Buffering': 'no' });
      res.write('retry: 2000\n\n');
      sendItems(res);
      clients.add(res);
      req.on('close', () => clients.delete(res));
      return;
    }

    if (method === 'POST' && p === '/items') {
      let body;
      try { body = await readBody(req); } catch (e) { return json(res, 400, { error: e.message }); }
      const text = typeof body.text === 'string' ? body.text.trim() : '';
      if (!text) return json(res, 400, { error: 'text required' });
      const item = { id: state.nextId++, text, bought: false };
      state.items.push(item);
      save(); broadcast();
      return json(res, 201, item);
    }

    let m = p.match(/^\/items\/(\d+)$/);
    if (m) {
      const id = parseInt(m[1], 10);
      const item = state.items.find(i => i.id === id);
      if (method === 'PATCH' || method === 'PUT') {
        let body;
        try { body = await readBody(req); } catch (e) { return json(res, 400, { error: e.message }); }
        if (!item) return json(res, 404, { error: 'not found' });
        let changed = false;
        if (typeof body.bought === 'boolean' && body.bought !== item.bought) { item.bought = body.bought; changed = true; }
        if (typeof body.text === 'string' && body.text.trim() && body.text.trim() !== item.text) { item.text = body.text.trim(); changed = true; }
        if (changed) { save(); broadcast(); }
        return json(res, 200, item);
      }
      if (method === 'DELETE') {
        if (!item) return json(res, 404, { error: 'not found' });
        state.items = state.items.filter(i => i.id !== id);
        save(); broadcast();
        cors(res); res.writeHead(204); return res.end();
      }
    }

    return json(res, 404, { error: 'not found' });
  } catch (e) {
    console.error('handler error:', e);
    return json(res, 500, { error: 'internal error' });
  }
});

server.listen(PORT, '0.0.0.0', () => console.log('api listening on ' + PORT + ', data at ' + DATA_FILE));
for (const sig of ['SIGTERM', 'SIGINT']) process.on(sig, () => { console.log('shutting down'); server.close(() => process.exit(0)); setTimeout(() => process.exit(0), 2000).unref(); });
