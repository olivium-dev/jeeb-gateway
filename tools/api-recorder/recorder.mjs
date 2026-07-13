#!/usr/bin/env node
// api-recorder / recorder.mjs
// Recording reverse-proxy: sits between the mobile app and the gateway.
// The app points at this proxy (PORT); the proxy forwards to TARGET (the gateway)
// and records every request/response exchange to SQLite for later backend-only replay.
//
// Zero external dependencies. Node 22+ (uses node:sqlite + node:http/https).
// On Node 22 node:sqlite requires --experimental-sqlite; on Node 23+/26 it is stable.
// Single file. See README.md.
//
// Config (env):
//   PORT      listen port for the proxy            (default 8890)
//   TARGET    upstream gateway base URL            (default http://192.168.2.39:10090)
//   DB        SQLite file path                     (default ./api-recorder.db)
//   RETAIN_H  retention window in hours            (default 48)
//   MAX_BODY  max stored body bytes (per side)     (default 262144 = 256KB)
//   UPSTREAM_TIMEOUT_MS  upstream socket timeout   (default 30000)

import http from 'node:http';
import https from 'node:https';
import { DatabaseSync } from 'node:sqlite';
import { randomUUID } from 'node:crypto';
import { URL } from 'node:url';

// ---------------------------------------------------------------------------
// Config
// ---------------------------------------------------------------------------
const PORT = parseInt(process.env.PORT || '8890', 10);
const TARGET = process.env.TARGET || 'http://192.168.2.39:10090';
const DB_PATH = process.env.DB || './api-recorder.db';
const RETAIN_H = parseInt(process.env.RETAIN_H || '48', 10);
const MAX_BODY = parseInt(process.env.MAX_BODY || String(256 * 1024), 10);
const UPSTREAM_TIMEOUT_MS = parseInt(process.env.UPSTREAM_TIMEOUT_MS || '30000', 10);

const targetUrl = new URL(TARGET);
const upstreamClient = targetUrl.protocol === 'https:' ? https : http;

// ---------------------------------------------------------------------------
// SQLite setup
// ---------------------------------------------------------------------------
const db = new DatabaseSync(DB_PATH);
db.exec('PRAGMA journal_mode = WAL;');
db.exec('PRAGMA synchronous = NORMAL;');
db.exec(`
  CREATE TABLE IF NOT EXISTS requests (
    id                 TEXT PRIMARY KEY,   -- correlation UUID (X-Request-Id)
    ts                 TEXT NOT NULL,      -- ISO-8601 UTC, when the request ARRIVED
    ts_ms              INTEGER NOT NULL,   -- epoch ms of arrival
    client_ip          TEXT,
    user_agent         TEXT,
    auth_redacted      TEXT,               -- scheme + last 6 chars only
    method             TEXT,
    path               TEXT,
    query              TEXT,
    req_headers        TEXT,               -- JSON
    req_body           BLOB,               -- raw bytes, possibly truncated for storage
    req_body_size      INTEGER,            -- TRUE size in bytes
    req_body_truncated INTEGER,            -- 0/1
    status             INTEGER,            -- NULL when upstream unreachable
    res_headers        TEXT,               -- JSON
    res_body           BLOB,               -- raw bytes, possibly truncated for storage
    res_body_size      INTEGER,            -- TRUE size in bytes
    res_body_truncated INTEGER,            -- 0/1
    duration_ms        INTEGER,
    error              TEXT                -- set when upstream unreachable
  );
`);
db.exec('CREATE INDEX IF NOT EXISTS idx_requests_ts        ON requests(ts);');
db.exec('CREATE INDEX IF NOT EXISTS idx_requests_status    ON requests(status);');
db.exec('CREATE INDEX IF NOT EXISTS idx_requests_path      ON requests(path);');
db.exec('CREATE INDEX IF NOT EXISTS idx_requests_client_ip ON requests(client_ip);');

const insertStmt = db.prepare(`
  INSERT INTO requests (
    id, ts, ts_ms, client_ip, user_agent, auth_redacted,
    method, path, query, req_headers, req_body, req_body_size, req_body_truncated,
    status, res_headers, res_body, res_body_size, res_body_truncated,
    duration_ms, error
  ) VALUES (
    ?, ?, ?, ?, ?, ?,
    ?, ?, ?, ?, ?, ?, ?,
    ?, ?, ?, ?, ?,
    ?, ?
  );
`);

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
const C = {
  reset: '\x1b[0m', red: '\x1b[31m', green: '\x1b[32m',
  yellow: '\x1b[33m', cyan: '\x1b[36m', gray: '\x1b[90m', bold: '\x1b[1m',
};

function colorForStatus(status) {
  if (status == null) return C.red;          // upstream error
  if (status >= 500) return C.red;
  if (status >= 400) return C.yellow;
  if (status >= 300) return C.cyan;
  return C.green;
}

function redactAuth(authHeader) {
  if (!authHeader) return null;
  const s = String(authHeader);
  const sp = s.indexOf(' ');
  const scheme = sp === -1 ? s : s.slice(0, sp);
  const token = sp === -1 ? '' : s.slice(sp + 1);
  const tail = token.length <= 6 ? token : token.slice(-6);
  return `${scheme} …${tail}`;
}

function firstForwardedHop(xff, remote) {
  if (xff) {
    const first = String(xff).split(',')[0].trim();
    if (first) return first;
  }
  return remote || null;
}

// Truncate a Buffer to MAX_BODY for storage; return { blob, size, truncated }.
function forStorage(buf) {
  const size = buf.length;
  if (size <= MAX_BODY) return { blob: buf, size, truncated: 0 };
  return { blob: buf.subarray(0, MAX_BODY), size, truncated: 1 };
}

function readBody(stream) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    stream.on('data', (c) => chunks.push(c));
    stream.on('end', () => resolve(Buffer.concat(chunks)));
    stream.on('error', reject);
  });
}

// ---------------------------------------------------------------------------
// Retention: hourly prune of rows older than RETAIN_H, then VACUUM.
// (SQLite does not return freed pages to the OS without VACUUM.)
// ---------------------------------------------------------------------------
function runRetention() {
  try {
    const cutoff = new Date(Date.now() - RETAIN_H * 3600 * 1000).toISOString();
    const res = db.prepare('DELETE FROM requests WHERE ts < ?').run(cutoff);
    db.exec('VACUUM;');
    if (res.changes > 0) {
      console.log(`${C.gray}[retention] pruned ${res.changes} row(s) older than ${cutoff}; vacuumed${C.reset}`);
    }
  } catch (e) {
    console.error(`${C.red}[retention] error: ${e.message}${C.reset}`);
  }
}
setInterval(runRetention, 3600 * 1000);
runRetention(); // sweep once at boot

// ---------------------------------------------------------------------------
// Built-in query endpoints (served BY the proxy, never forwarded)
// ---------------------------------------------------------------------------
function bodyToJson(blob, size, truncated) {
  if (blob == null) return null;
  const buf = Buffer.isBuffer(blob) ? blob : Buffer.from(blob);
  return {
    b64: buf.toString('base64'),
    stored_bytes: buf.length,
    true_bytes: size,
    truncated: !!truncated,
    marker: truncated ? `[truncated: ${size} bytes total]` : null,
  };
}

function rowToJson(r) {
  return {
    id: r.id,
    ts: r.ts,
    client_ip: r.client_ip,
    user_agent: r.user_agent,
    auth_redacted: r.auth_redacted,
    method: r.method,
    path: r.path,
    query: r.query,
    req_headers: r.req_headers ? JSON.parse(r.req_headers) : null,
    req_body: bodyToJson(r.req_body, r.req_body_size, r.req_body_truncated),
    status: r.status,
    res_headers: r.res_headers ? JSON.parse(r.res_headers) : null,
    res_body: bodyToJson(r.res_body, r.res_body_size, r.res_body_truncated),
    duration_ms: r.duration_ms,
    error: r.error,
  };
}

function handleLogs(reqUrl, res) {
  const q = reqUrl.searchParams;
  const where = [];
  const args = [];
  if (q.has('status')) { where.push('status = ?'); args.push(parseInt(q.get('status'), 10)); }
  if (q.get('errors') === '1') { where.push('error IS NOT NULL'); }
  if (q.has('path')) { where.push('path LIKE ?'); args.push('%' + q.get('path') + '%'); }
  if (q.has('client')) { where.push('client_ip = ?'); args.push(q.get('client')); }
  if (q.has('method')) { where.push('method = ?'); args.push(q.get('method').toUpperCase()); }
  if (q.has('since')) { where.push('ts >= ?'); args.push(q.get('since')); }
  const limit = Math.min(parseInt(q.get('limit') || '100', 10) || 100, 5000);
  const sql = `SELECT * FROM requests ${where.length ? 'WHERE ' + where.join(' AND ') : ''} ORDER BY ts DESC LIMIT ${limit}`;
  const rows = db.prepare(sql).all(...args).map(rowToJson);
  sendJson(res, 200, { count: rows.length, rows });
}

function handleStats(res) {
  const total = db.prepare('SELECT COUNT(*) c FROM requests').get().c;
  const errors = db.prepare('SELECT COUNT(*) c FROM requests WHERE error IS NOT NULL').get().c;
  const s5xx = db.prepare('SELECT COUNT(*) c FROM requests WHERE status >= 500').get().c;
  const s4xx = db.prepare('SELECT COUNT(*) c FROM requests WHERE status >= 400 AND status < 500').get().c;
  const avg = db.prepare('SELECT AVG(duration_ms) a FROM requests').get().a;
  const byStatus = db.prepare('SELECT status, COUNT(*) c FROM requests GROUP BY status ORDER BY status').all();
  const byPath = db.prepare('SELECT path, COUNT(*) c FROM requests GROUP BY path ORDER BY c DESC LIMIT 20').all();
  const oldest = db.prepare('SELECT MIN(ts) t FROM requests').get().t;
  const newest = db.prepare('SELECT MAX(ts) t FROM requests').get().t;
  sendJson(res, 200, {
    total,
    errors,
    error_rate: total ? +(errors / total).toFixed(4) : 0,
    status_5xx: s5xx,
    status_4xx: s4xx,
    avg_duration_ms: avg == null ? null : +avg.toFixed(1),
    oldest_ts: oldest,
    newest_ts: newest,
    by_status: byStatus,
    top_paths: byPath,
    target: TARGET,
    retain_h: RETAIN_H,
    max_body: MAX_BODY,
  });
}

function sendJson(res, code, obj) {
  const body = Buffer.from(JSON.stringify(obj, null, 2));
  res.writeHead(code, { 'content-type': 'application/json', 'content-length': body.length });
  res.end(body);
}

// ---------------------------------------------------------------------------
// Core proxy
// ---------------------------------------------------------------------------
async function handleProxy(clientReq, clientRes) {
  const id = randomUUID();
  const arrivedMs = Date.now();
  const ts = new Date(arrivedMs).toISOString();
  const startHr = process.hrtime.bigint();

  const parsed = new URL(clientReq.url, 'http://internal');
  const path = parsed.pathname;
  const query = parsed.search ? parsed.search.slice(1) : '';

  const clientIp = firstForwardedHop(
    clientReq.headers['x-forwarded-for'],
    clientReq.socket.remoteAddress,
  );
  const userAgent = clientReq.headers['user-agent'] || null;
  const authRedacted = redactAuth(clientReq.headers['authorization']);

  // Buffer the request body fully (buffer-and-forward: bytes in = bytes out).
  let reqBody;
  try {
    reqBody = await readBody(clientReq);
  } catch (e) {
    reqBody = Buffer.alloc(0);
  }

  // Always echo the correlation id back to the client.
  clientRes.setHeader('X-Request-Id', id);

  // Build upstream headers: forward all, fix Host, set a correct content-length.
  const upstreamHeaders = { ...clientReq.headers };
  upstreamHeaders['host'] = targetUrl.host;
  delete upstreamHeaders['content-length'];
  delete upstreamHeaders['transfer-encoding'];
  if (reqBody.length > 0) upstreamHeaders['content-length'] = String(reqBody.length);
  // Propagate correlation id upstream too (harmless, useful for cross-log tracing).
  upstreamHeaders['x-request-id'] = id;

  const options = {
    protocol: targetUrl.protocol,
    hostname: targetUrl.hostname,
    port: targetUrl.port || (targetUrl.protocol === 'https:' ? 443 : 80),
    method: clientReq.method,
    path: clientReq.url, // preserves path + query exactly
    headers: upstreamHeaders,
  };

  const reqStore = forStorage(reqBody);

  const finish = ({ status, resHeaders, resBody, error }) => {
    const durationMs = Number((process.hrtime.bigint() - startHr) / 1000000n);
    const resStore = resBody ? forStorage(resBody) : { blob: null, size: null, truncated: null };
    try {
      insertStmt.run(
        id, ts, arrivedMs, clientIp, userAgent, authRedacted,
        clientReq.method, path, query, JSON.stringify(clientReq.headers),
        reqStore.blob, reqStore.size, reqStore.truncated,
        status ?? null, resHeaders ? JSON.stringify(resHeaders) : null,
        resStore.blob, resStore.size, resStore.truncated,
        durationMs, error ?? null,
      );
    } catch (e) {
      console.error(`${C.red}[db] insert failed: ${e.message}${C.reset}`);
    }
    // Console line: status / method / path / ms / client
    const col = colorForStatus(status);
    const statusLabel = status == null ? 'ERR' : String(status);
    console.log(
      `${col}${statusLabel.padEnd(3)}${C.reset} ` +
      `${C.bold}${(clientReq.method || '?').padEnd(6)}${C.reset}` +
      `${path}${query ? '?' + query : ''} ` +
      `${C.gray}${durationMs}ms ${clientIp || '-'}${C.reset}` +
      (error ? ` ${C.red}(${error})${C.reset}` : ''),
    );
  };

  const upReq = upstreamClient.request(options, async (upRes) => {
    let resBody;
    try {
      resBody = await readBody(upRes);
    } catch (e) {
      resBody = Buffer.alloc(0);
    }
    // Relay to client: forward status + headers verbatim (raw bytes untouched,
    // so content-encoding like gzip stays valid), add correlation id, fix length.
    const outHeaders = { ...upRes.headers };
    delete outHeaders['transfer-encoding'];
    outHeaders['content-length'] = String(resBody.length);
    outHeaders['x-request-id'] = id;
    try {
      clientRes.writeHead(upRes.statusCode, outHeaders);
      clientRes.end(resBody);
    } catch (_) { /* client may have gone away */ }
    finish({ status: upRes.statusCode, resHeaders: upRes.headers, resBody, error: null });
  });

  upReq.setTimeout(UPSTREAM_TIMEOUT_MS, () => {
    upReq.destroy(new Error(`upstream timeout after ${UPSTREAM_TIMEOUT_MS}ms`));
  });

  upReq.on('error', (err) => {
    // Upstream unreachable / timed out — STILL write the row, then 502 the client.
    if (!clientRes.headersSent) {
      const msg = Buffer.from(JSON.stringify({ error: 'bad_gateway', detail: err.message, request_id: id }));
      try {
        clientRes.writeHead(502, { 'content-type': 'application/json', 'content-length': msg.length, 'x-request-id': id });
        clientRes.end(msg);
      } catch (_) { /* ignore */ }
    }
    finish({ status: null, resHeaders: null, resBody: null, error: err.message });
  });

  if (reqBody.length > 0) upReq.write(reqBody);
  upReq.end();
}

// ---------------------------------------------------------------------------
// Server
// ---------------------------------------------------------------------------
const server = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://internal');
  if (u.pathname === '/__logs') return handleLogs(u, res);
  if (u.pathname === '/__stats') return handleStats(res);
  if (u.pathname === '/__health') return sendJson(res, 200, { ok: true, target: TARGET });
  return handleProxy(req, res);
});

server.listen(PORT, () => {
  console.log(`${C.bold}api-recorder${C.reset} listening on :${PORT} → ${TARGET}`);
  console.log(`${C.gray}db=${DB_PATH} retain=${RETAIN_H}h max_body=${MAX_BODY}B`);
  console.log(`${C.gray}query: http://localhost:${PORT}/__logs  stats: http://localhost:${PORT}/__stats${C.reset}`);
});

function shutdown() {
  console.log('\nshutting down…');
  server.close();
  try { db.close(); } catch (_) { /* ignore */ }
  process.exit(0);
}
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
