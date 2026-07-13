# api-recorder

A **recording reverse-proxy** for the Jeeb gateway, plus a **backend-only replay**
companion. Zero external dependencies — two single files, Node 22+ only.

The proxy sits **between the mobile app and the gateway**: the app points at the
proxy, the proxy forwards to the gateway (`TARGET`) and records every
request/response exchange to SQLite. After a mobile E2E run, you can **replay** the
recorded calls directly against the gateway (backend-only, no device) to iterate
fast.

> The mobile E2E stays the **only real test**. This is a debug/dev accelerator.

---

## Runtime

Uses `node:sqlite`, `node:http`/`node:https`, `node:crypto` — no `npm install`.

- **Node 23+ / 26** (what this box runs, `v26.3.1`): `node:sqlite` is stable, run directly.
- **Node 22**: add `--experimental-sqlite` (e.g. `node --experimental-sqlite recorder.mjs`).

Chosen over Python because `node:sqlite` runs **cleanly without any flag** on this
machine's Node 26, keeping it truly zero-dep and single-file.

---

## 1. Record — run the proxy

```bash
# Defaults: PORT=8890, TARGET=http://192.168.2.39:10090, DB=./api-recorder.db
node tools/api-recorder/recorder.mjs

# Or configure via env:
PORT=8890 \
TARGET=http://192.168.2.39:10090 \
DB=/path/to/api-recorder.db \
RETAIN_H=48 \
MAX_BODY=262144 \
node tools/api-recorder/recorder.mjs
```

### Point the app / emulator at the proxy

Set the app's API base URL to the proxy instead of the gateway:

- Android emulator → host machine: `http://10.0.2.2:8890`
- Physical device on the LAN: `http://<your-machine-ip>:8890`

Every call now flows app → proxy → gateway and is recorded. Each response carries an
`X-Request-Id` header (the correlation UUID / SQLite row id).

You'll see a colored line per request (color by status class):

```
200 GET   /api/User/me 42ms 10.0.2.2
401 POST  /auth/tokens 8ms 10.0.2.2
ERR POST  /api/Offers 30012ms 10.0.2.2 (upstream timeout after 30000ms)
```

### What's captured (one SQLite row per request)

- Correlation UUID (echoed as `X-Request-Id` response header)
- Arrival timestamp (ISO-8601 UTC, recorded when the request **arrives**)
- Client IP (`X-Forwarded-For` first hop, else socket), User-Agent
- Authorization **redacted** to scheme + last 6 chars (`Bearer …a1b2c3`) — never the full token
- Request: method, path, query, all headers (JSON), full body
- Response: status, all headers (JSON), full body
- Duration (ms)
- On upstream-unreachable: the **error string is stored and the row is still written**
  (the client still gets a `502`)

Bodies are stored as raw bytes (BLOB), **binary-safe** (bytes in = bytes out), and
**truncated for storage** above `MAX_BODY` (default 256 KB) — the true size is kept and
surfaced as a marker: `[truncated: 1048576 bytes total]`. The **full** body is always
forwarded upstream/downstream; truncation only affects what's stored.

### SQLite tuning & retention

- `journal_mode=WAL`, `synchronous=NORMAL`
- Indexes on `ts`, `status`, `path`, `client_ip`
- **Retention**: hourly, rows older than `RETAIN_H` (default 48h) are deleted, then
  `VACUUM` runs so freed space is returned to the OS.

---

## 2. Query — built-in endpoints (served by the proxy, never forwarded)

```bash
# Recent calls (JSON). Filters: status, errors=1, path, client, method, since, limit
curl 'http://localhost:8890/__logs?limit=20'
curl 'http://localhost:8890/__logs?status=500'
curl 'http://localhost:8890/__logs?errors=1'
curl 'http://localhost:8890/__logs?path=/api/Offers&method=POST'
curl 'http://localhost:8890/__logs?client=10.0.2.2'
curl 'http://localhost:8890/__logs?since=2026-07-13T00:00:00Z&limit=100'

# Aggregate stats: totals, error rate, avg duration, by-status, top paths
curl 'http://localhost:8890/__stats'
```

`/__logs` returns request and response bodies base64-encoded, each with
`true_bytes` / `truncated` / `marker` so you never confuse a truncated capture for the
real payload.

---

## 3. Replay — backend-only, after a mobile E2E

Replays selected recorded requests **directly against the gateway** (no device). Stale
captured tokens are swapped for a **freshly minted** one via the super-login seam
(SuperLogin OpenMode is ON on the MSI dev env):

1. `GET  {gateway}/api/User/super-login/users` → pick a `userId`
2. `POST {gateway}/auth/tokens {userId}` → fresh `accessToken`
3. every replayed request's `Authorization` is replaced with the live `Bearer`

```bash
# Dry run — see what would replay (no token minted, nothing sent)
node tools/api-recorder/replay.mjs --dry-run

# Replay all recorded POSTs to /api/Offers against the gateway
node tools/api-recorder/replay.mjs --method POST --path /api/Offers

# Replay one specific recorded call by correlation id
node tools/api-recorder/replay.mjs --id <uuid>

# Pin the user, require the replayed status to EXACTLY match what was recorded
node tools/api-recorder/replay.mjs --user-id <uid> --expect match

# Read from the running proxy's /__logs instead of the DB file
node tools/api-recorder/replay.mjs --from-logs http://localhost:8890 --path /api/User
```

Prints `PASS`/`FAIL` per call and a summary; exits non-zero if any failed.

### Replay options

| Flag | Meaning |
| --- | --- |
| `--db <path>` | SQLite DB (default `./api-recorder.db`, env `DB`) |
| `--from-logs <baseUrl>` | read rows via the proxy's `/__logs` instead of the DB |
| `--gateway <url>` | target gateway (default `http://192.168.2.39:10090`, env `GATEWAY`) |
| `--user-id <id>` | use this userId instead of the first roster user |
| `--service-auth-key <k>` | send `X-Service-Auth-Key` (only if OpenMode is OFF) |
| `--path <substr>` `--method <M>` `--status <N>` `--id <uuid>` | filter which rows replay |
| `--limit <N>` | cap rows (default 200) |
| `--include-auth` | also replay recorded auth/super-login calls (skipped by default) |
| `--expect match` | PASS only when replayed status equals recorded status |
| `--dry-run` | list candidates, mint nothing, send nothing |

---

## Config reference (recorder)

| Env | Default | Meaning |
| --- | --- | --- |
| `PORT` | `8890` | proxy listen port |
| `TARGET` | `http://192.168.2.39:10090` | upstream gateway base URL |
| `DB` | `./api-recorder.db` | SQLite file path |
| `RETAIN_H` | `48` | retention window (hours) |
| `MAX_BODY` | `262144` | max stored body bytes per side (256 KB) |
| `UPSTREAM_TIMEOUT_MS` | `30000` | upstream socket timeout |
