'use strict';
// Shared shopping list — web UI service (repo-b).
// Serves the page on the published port and proxies every read/write to the `api` service.
const http = require('http');
const fs = require('fs');
const path = require('path');

const PORT = parseInt(process.env.PORT || '8080', 10);
const API_BASE = (process.env.API_BASE || process.env.API_URL || 'http://api:8080').replace(/\/+$/, '');
const API_TIMEOUT_MS = parseInt(process.env.API_TIMEOUT_MS || '4000', 10);

const INDEX = path.join(__dirname, 'public', 'index.html');

function readBody(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    req.on('data', (c) => { data += c; if (data.length > 1e6) { reject(new Error('body too large')); req.destroy(); } });
    req.on('end', () => resolve(data));
    req.on('error', reject);
  });
}

function send(res, status, body, headers) {
  const h = Object.assign({ 'Cache-Control': 'no-store', 'Access-Control-Allow-Origin': '*' }, headers || {});
  res.writeHead(status, h);
  res.end(body);
}
function sendJson(res, status, obj) {
  send(res, status, JSON.stringify(obj), { 'Content-Type': 'application/json; charset=utf-8' });
}

// proxy a request to the api service; resolves to {status, text}
function apiFetch(method, urlPath, body) {
  return new Promise((resolve, reject) => {
    let u;
    try { u = new URL(API_BASE + urlPath); } catch (e) { return reject(e); }
    const lib = u.protocol === 'https:' ? require('https') : http;
    const headers = { Accept: 'application/json' };
    let payload = null;
    if (body !== undefined && body !== null) {
      payload = Buffer.from(typeof body === 'string' ? body : JSON.stringify(body));
      headers['Content-Type'] = 'application/json';
      headers['Content-Length'] = payload.length;
    }
    const req = lib.request({
      protocol: u.protocol, hostname: u.hostname, port: u.port || (u.protocol === 'https:' ? 443 : 80),
      path: u.pathname + u.search, method, headers, timeout: API_TIMEOUT_MS,
    }, (r) => {
      let data = '';
      r.setEncoding('utf8');
      r.on('data', (c) => { data += c; });
      r.on('end', () => resolve({ status: r.statusCode || 502, text: data, headers: r.headers }));
    });
    req.on('timeout', () => req.destroy(new Error('api timeout')));
    req.on('error', reject);
    if (payload) req.write(payload);
    req.end();
  });
}

const server = http.createServer(async (req, res) => {
  const u = new URL(req.url, 'http://localhost');
  const p = u.pathname;

  try {
    if (req.method === 'OPTIONS') {
      return send(res, 204, '', {
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Allow-Methods': 'GET, POST, PATCH, DELETE, OPTIONS',
        'Access-Control-Allow-Headers': 'Content-Type',
      });
    }

    if ((req.method === 'GET' || req.method === 'HEAD') && (p === '/' || p === '/index.html')) {
      const html = fs.readFileSync(INDEX);
      return send(res, 200, html, { 'Content-Type': 'text/html; charset=utf-8' });
    }

    if (p === '/health' || p === '/healthz') {
      let api = { ok: false };
      try {
        const r = await apiFetch('GET', '/health');
        api = { ok: r.status >= 200 && r.status < 300, status: r.status, body: r.text.slice(0, 200) };
      } catch (e) { api = { ok: false, error: String(e && e.message || e) }; }
      return sendJson(res, 200, { ok: api.ok, service: 'web', api: { base: API_BASE, ...api } });
    }

    // --- items: the UI's own JSON API, proxied straight to `api` ---
    if (p === '/items' && req.method === 'GET') {
      const r = await apiFetch('GET', '/items');
      return send(res, r.status, r.text, { 'Content-Type': 'application/json; charset=utf-8' });
    }

    if (p === '/items' && req.method === 'POST') {
      const raw = await readBody(req);
      let body;
      try { body = raw ? JSON.parse(raw) : {}; } catch (e) { return sendJson(res, 400, { error: 'invalid json' }); }
      const r = await apiFetch('POST', '/items', body);
      return send(res, r.status, r.text, { 'Content-Type': 'application/json; charset=utf-8' });
    }

    if (p.startsWith('/items/')) {
      const id = p.slice('/items/'.length);
      if (req.method === 'PATCH') {
        const raw = await readBody(req);
        let body;
        try { body = raw ? JSON.parse(raw) : {}; } catch (e) { return sendJson(res, 400, { error: 'invalid json' }); }
        const r = await apiFetch('PATCH', '/items/' + encodeURIComponent(decodeURIComponent(id)), body);
        return send(res, r.status, r.text, { 'Content-Type': 'application/json; charset=utf-8' });
      }
      if (req.method === 'DELETE') {
        const r = await apiFetch('DELETE', '/items/' + encodeURIComponent(decodeURIComponent(id)));
        return send(res, r.status, r.text, { 'Content-Type': 'application/json; charset=utf-8' });
      }
      return sendJson(res, 405, { error: 'method not allowed' });
    }

    return sendJson(res, 404, { error: 'not found' });
  } catch (e) {
    return sendJson(res, 502, { error: 'api unreachable', detail: String(e && e.message || e) });
  }
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`shoplist web ui on :${PORT}, api at ${API_BASE}`);
});
