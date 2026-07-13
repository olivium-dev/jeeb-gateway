#!/usr/bin/env node
// api-recorder / replay.mjs
// Backend-only replay companion for the recording reverse-proxy.
//
// After a mobile E2E runs THROUGH the proxy (which records every call), this tool
// replays selected recorded requests DIRECTLY against the gateway — no mobile, no
// emulator — so backend iteration is fast. Captured Authorization tokens are stale,
// so it re-mints a FRESH token via the super-login seam (SuperLogin OpenMode is ON):
//
//   1. GET  {GATEWAY}/api/User/super-login/users     → pick a userId
//   2. POST {GATEWAY}/auth/tokens  {userId}           → fresh accessToken
//
// then swaps every replayed request's Authorization for the live Bearer and reports
// pass/fail per call.
//
// The mobile E2E remains the ONLY real test; this is a debug/dev accelerator.
//
// Zero external dependencies (Node 22+: node:sqlite + global fetch). Single file.
//
// Usage:
//   node replay.mjs [options]
//
// Source of recorded rows (default: the SQLite DB):
//   --db <path>            SQLite DB file        (default ./api-recorder.db, env DB)
//   --from-logs <baseUrl>  read via proxy /__logs instead of the DB
//                          (e.g. http://localhost:8890)
//
// Target gateway:
//   --gateway <url>        (default http://192.168.2.39:10090, env GATEWAY)
//
// Auth seam:
//   --user-id <id>         use this userId instead of the first roster user
//   --service-auth-key <k> send X-Service-Auth-Key (only needed if OpenMode is OFF)
//
// Filtering which recorded rows to replay:
//   --path <substr>        only rows whose path contains substr
//   --method <M>           only rows with this method
//   --status <N>           only rows recorded with this status
//   --id <uuid>            replay a single row by correlation id
//   --limit <N>            cap number of rows (default 200)
//   --skip-internal        skip /__ and auth/super-login rows (default: on)
//   --include-auth         also replay the recorded auth/super-login calls
//
// Behaviour:
//   --expect match         PASS only when replayed status === recorded status
//                          (default: PASS when replayed status is 2xx/3xx)
//   --dry-run              print what WOULD be replayed, mint no token, send nothing

import { DatabaseSync } from 'node:sqlite';

// ---------------------------------------------------------------------------
// Arg parsing
// ---------------------------------------------------------------------------
function parseArgs(argv) {
  const out = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith('--')) { out[key] = true; }
      else { out[key] = next; i++; }
    } else { out._.push(a); }
  }
  return out;
}
const args = parseArgs(process.argv.slice(2));

const DB_PATH = args.db || process.env.DB || './api-recorder.db';
const GATEWAY = (args.gateway || process.env.GATEWAY || 'http://192.168.2.39:10090').replace(/\/+$/, '');
const LIMIT = parseInt(args.limit || '200', 10);
const EXPECT_MATCH = args.expect === 'match';
const DRY_RUN = !!args['dry-run'];
const SKIP_INTERNAL = args['include-auth'] ? false : true;

const C = {
  reset: '\x1b[0m', red: '\x1b[31m', green: '\x1b[32m',
  yellow: '\x1b[33m', cyan: '\x1b[36m', gray: '\x1b[90m', bold: '\x1b[1m',
};

// ---------------------------------------------------------------------------
// Load recorded rows — from the DB or via the proxy's /__logs
// ---------------------------------------------------------------------------
function b64ToBuf(b64) { return b64 == null ? null : Buffer.from(b64, 'base64'); }

async function loadRows() {
  if (args['from-logs']) {
    const base = String(args['from-logs']).replace(/\/+$/, '');
    const u = new URL(base + '/__logs');
    if (args.status) u.searchParams.set('status', args.status);
    if (args.path) u.searchParams.set('path', args.path);
    if (args.method) u.searchParams.set('method', args.method);
    u.searchParams.set('limit', String(LIMIT));
    const resp = await fetch(u);
    if (!resp.ok) throw new Error(`/__logs returned ${resp.status}`);
    const data = await resp.json();
    return data.rows.map((r) => ({
      id: r.id, method: r.method, path: r.path, query: r.query,
      req_headers: r.req_headers || {},
      req_body: r.req_body ? b64ToBuf(r.req_body.b64) : null,
      status: r.status,
    }));
  }
  // Default: read the SQLite DB directly.
  const db = new DatabaseSync(DB_PATH, { readOnly: true });
  const where = [];
  const params = [];
  if (args.id) { where.push('id = ?'); params.push(args.id); }
  if (args.status) { where.push('status = ?'); params.push(parseInt(args.status, 10)); }
  if (args.path) { where.push('path LIKE ?'); params.push('%' + args.path + '%'); }
  if (args.method) { where.push('method = ?'); params.push(String(args.method).toUpperCase()); }
  const sql = `SELECT id, method, path, query, req_headers, req_body, status
               FROM requests ${where.length ? 'WHERE ' + where.join(' AND ') : ''}
               ORDER BY ts ASC LIMIT ${LIMIT}`;
  const rows = db.prepare(sql).all(...params).map((r) => ({
    id: r.id, method: r.method, path: r.path, query: r.query,
    req_headers: r.req_headers ? JSON.parse(r.req_headers) : {},
    req_body: r.req_body == null ? null : Buffer.from(r.req_body),
    status: r.status,
  }));
  db.close();
  return rows;
}

// ---------------------------------------------------------------------------
// Super-login seam: pick a userId, mint a fresh Bearer token
// ---------------------------------------------------------------------------
async function pickUserId() {
  if (args['user-id']) return String(args['user-id']);
  const resp = await fetch(`${GATEWAY}/api/User/super-login/users`);
  if (!resp.ok) {
    throw new Error(`super-login/users → ${resp.status} (is SuperLogin OpenMode ON at ${GATEWAY}?)`);
  }
  const data = await resp.json();
  const users = data.users || [];
  if (!users.length) throw new Error('super-login/users returned an empty roster');
  const uid = users[0].userId;
  console.log(`${C.gray}super-login roster: ${users.length} users; using userId=${uid} (${users[0].name || '?'})${C.reset}`);
  return uid;
}

async function mintToken(userId) {
  const headers = { 'content-type': 'application/json' };
  if (args['service-auth-key']) headers['X-Service-Auth-Key'] = String(args['service-auth-key']);
  const resp = await fetch(`${GATEWAY}/auth/tokens`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ userId }),
  });
  const text = await resp.text();
  if (!resp.ok) throw new Error(`/auth/tokens → ${resp.status}: ${text.slice(0, 300)}`);
  const pair = JSON.parse(text);
  if (!pair.accessToken) throw new Error(`/auth/tokens returned no accessToken: ${text.slice(0, 300)}`);
  return pair.accessToken;
}

// ---------------------------------------------------------------------------
// Replay a single recorded row against the gateway
// ---------------------------------------------------------------------------
const STRIP_HEADERS = new Set([
  'host', 'content-length', 'connection', 'accept-encoding',
  'authorization', 'x-request-id', 'transfer-encoding',
]);

function buildHeaders(recorded, freshToken) {
  const h = {};
  for (const [k, v] of Object.entries(recorded)) {
    if (STRIP_HEADERS.has(k.toLowerCase())) continue;
    h[k] = Array.isArray(v) ? v.join(', ') : String(v);
  }
  h['authorization'] = `Bearer ${freshToken}`;
  return h;
}

function isInternal(row) {
  const p = (row.path || '');
  if (p.startsWith('/__')) return true;
  if (p.startsWith('/auth/tokens')) return true;
  if (p.includes('/super-login/users')) return true;
  return false;
}

async function replayRow(row, freshToken) {
  const url = `${GATEWAY}${row.path}${row.query ? '?' + row.query : ''}`;
  const method = (row.method || 'GET').toUpperCase();
  const init = { method, headers: buildHeaders(row.req_headers, freshToken) };
  if (method !== 'GET' && method !== 'HEAD' && row.req_body && row.req_body.length > 0) {
    init.body = row.req_body;
  }
  const t0 = Date.now();
  try {
    const resp = await fetch(url, init);
    const ms = Date.now() - t0;
    // Drain body so the socket frees.
    await resp.arrayBuffer().catch(() => {});
    const pass = EXPECT_MATCH ? (resp.status === row.status) : (resp.status >= 200 && resp.status < 400);
    return { pass, status: resp.status, ms };
  } catch (e) {
    return { pass: false, status: null, ms: Date.now() - t0, error: e.message };
  }
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------
async function main() {
  let rows = await loadRows();
  if (SKIP_INTERNAL) rows = rows.filter((r) => !isInternal(r));
  if (!rows.length) {
    console.log(`${C.yellow}No recorded rows match the filter.${C.reset}`);
    return;
  }

  console.log(`${C.bold}Replay${C.reset} ${rows.length} recorded call(s) → ${GATEWAY}`);
  console.log(`${C.gray}expect=${EXPECT_MATCH ? 'exact-status-match' : '2xx/3xx'} dry_run=${DRY_RUN}${C.reset}\n`);

  if (DRY_RUN) {
    for (const r of rows) {
      console.log(`${C.gray}would replay${C.reset} ${(r.method || '?').padEnd(6)} ${r.path}${r.query ? '?' + r.query : ''} ${C.gray}(recorded ${r.status})${C.reset}`);
    }
    return;
  }

  const userId = await pickUserId();
  const token = await mintToken(userId);
  console.log(`${C.green}minted fresh token${C.reset} (…${token.slice(-6)}) for userId=${userId}\n`);

  let passed = 0, failed = 0;
  for (const r of rows) {
    const res = await replayRow(r, token);
    if (res.pass) passed++; else failed++;
    const mark = res.pass ? `${C.green}PASS${C.reset}` : `${C.red}FAIL${C.reset}`;
    const statusLabel = res.status == null ? 'ERR' : String(res.status);
    const cmp = EXPECT_MATCH && res.status !== r.status ? ` ${C.yellow}(recorded ${r.status})${C.reset}` : '';
    console.log(
      `${mark} ${statusLabel.padEnd(3)} ${(r.method || '?').padEnd(6)} ` +
      `${r.path}${r.query ? '?' + r.query : ''} ${C.gray}${res.ms}ms${C.reset}${cmp}` +
      (res.error ? ` ${C.red}(${res.error})${C.reset}` : ''),
    );
  }

  console.log(
    `\n${C.bold}Summary:${C.reset} ${C.green}${passed} passed${C.reset}, ` +
    `${failed ? C.red : C.gray}${failed} failed${C.reset}, ${rows.length} total`,
  );
  process.exit(failed ? 1 : 0);
}

main().catch((e) => {
  console.error(`${C.red}replay error: ${e.message}${C.reset}`);
  process.exit(2);
});
