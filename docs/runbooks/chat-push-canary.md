# Runbook — end-to-end chat + push canary

## Why this exists

Every large chat/push outage in this fleet passed `/health/ready` while push was
100% dead. The 2026-08-22 incident ran 23 hours with the gateway reporting 19/19
Healthy. `notification-credential` is *green in exactly the state that kills chat
push* (it returns Healthy unconditionally when `FeatureFlags:NotificationDurableWrite:Enabled`
is false, which is the state in which the dispatcher skips the notification-service
call entirely). And chat can be fully `503` — the `UseUpstream__Chat` ratchet — with
every readiness check still green, because no readiness check touches chat at all.

A health check answers *is the process up*. This canary answers three outcome
questions instead:

1. Did a real request fan out to a real jeeber?
2. Did a real push dispatch for that jeeber reach a terminal state?
3. Can the recipient actually see a chat message that was sent?

## What it does, leg by leg

| Leg | Call | Assertion |
|---|---|---|
| 0 preflight | `GET /health/live` | reachable (this is the **only** health read, and it gates nothing else) |
| 1 identity | `POST /auth/tokens` ×2 with `X-Service-Auth-Key` | both fixed canary bearers mint |
| 2 presence | `PUT {prefix}/jeebers/me/availability`, `POST /location/update` | jeeber is online with a GPS fix at the pickup coordinate — fan-out is geo-filtered **fail-closed**, so without this the request reaches nobody and the canary would pass vacuously |
| 3 device | `PUT /api/PushNotification/register` | the jeeber has an FCM seat, so a dispatch row can exist |
| 4 request | `GET /tiers`, `POST /requests` | a Flash request is created at 33.8886, 35.4955 |
| 5 push | ledger or inbox (below) | a push outcome for the jeeber, within the budget |
| 6 chat | `GET /v1/chat/jeeb/conversations/by-request/{id}` → `POST /v1/conversations/{id}/messages` → `GET …/messages` **as the jeeber** → `POST /v1/chat/firebase-token` | the recipient's own viewer-scoped page carries the message, and the minted Firebase uid equals the jeeber id |
| 7 cleanup | availability off, `DELETE /requests/{id}` | bounded trail |

Leg 6 is the visibility lane end-to-end. chat-service scopes the message page to
the bearer and echoes `viewer_id`; a jeeber-side hit therefore proves the message's
`VisibleTo[]` carries the jeeber. The Firebase-uid check closes the third of the
three identifiers that must agree or the recipient silently sees nothing
(gateway mint uid == app `currentUserId` == an element of `VisibleTo[]`).

A `503` from the conversation route is caught and reported by name: that is
`FeatureFlags__UseUpstream__Chat=false`, which every staging gateway deploy
re-writes and which only `jeeb-chat-b-activation.yml` turns back on.

## The push leg has two proof strengths — know which one you got

**`relay-ledger` (full).** `GET {PUSH_LEDGER_BASE_URL}/api/v1/sent-payload/idempotency?target_user_id=…`
with `X-Caller-Id: jeeb-gateway` + `X-Api-Key: $JEEB_PUSH_INTERNAL_API_KEY` (scope
`gateway.recovery`). Asserts a `push_dispatch` row for the canary jeeber reached a
terminal state. A `claimed` / `retry_processing` row is a FAIL — that is the exact
shape of a silently stalled push.

The canary registers a *synthetic* FCM token, so FCM rejects it and the dispatch
terminates as `failed`, not `succeeded`. That is accepted by default
(`JEEB_CANARY_ALLOW_FCM_TOKEN_REJECT=true`) and it is the right call: reaching FCM
at all proves the entire producer chain — recipient resolution, the durable
dispatcher, notification-service, the relay, the FCM credential. What the canary is
detecting is the chain going *silent*, and a rejected token still disproves silence.
Set the variable to `false` only if you attach a real device token to the canary
jeeber, in which case `succeeded` becomes assertable.

**`durable-inbox` (partial, the default today).** push-notification is not
published at the public edge, so a GitHub runner cannot reach the ledger. Without
`PUSH_LEDGER_BASE_URL` the canary falls back to polling `GET /v1/notifications` as
the jeeber for a durable record naming the canary request. This proves gateway →
notification-service, i.e. the leg that was dead in 2026-08-22 — but **not** the FCM
call. The run says so in its logs and in the job summary; do not read it as FCM proof.

To get the full leg, give the runner a route to push-notification and set the repo
variable `JEEB_PUSH_LEDGER_BASE_URL`. Either publish a scoped read path at the edge,
or open a cloudflared SSH port-forward first, exactly as `heartbeat-presence-smoke.yml`
does (`JEEB_SSH_PRIVATE_KEY` + `cloudflared access ssh`), then point
`PUSH_LEDGER_BASE_URL` at `http://127.0.0.1:<forwarded port>`.

## The Firestore assertion (optional, no service account needed)

Set `JEEB_FIREBASE_WEB_API_KEY` and the canary additionally:

1. mints a Firebase custom token for the jeeber at `POST /v1/chat/firebase-token`,
2. exchanges it at Identity Toolkit for an ID token (the API key rides
   `X-Goog-Api-Key`, never the query string, so it cannot leak into a log),
3. runs **the app's own query** against Firestore REST as that uid:
   `Conversations/{id}/Messages` where `VisibleTo array-contains uid`, ordered by
   `CreatedAt` descending — via `:runQuery` on project `jeeb-5a293`, database `(default)`.

The Firestore security rules do the proving: a uid not in `VisibleTo` gets nothing
back. This is a genuine end-to-end assertion of the lane the mobile listener uses.

A *service-account*–based assertion would instead need a new secret holding the
`jeeb-5a293` admin JSON (equivalently, `Firebase__Chat__ServiceAccountKeyPath`
material) plus a token-minting step. That secret does not exist in this repo today
and none was invented for this canary. The Web-API-key path above is strictly
better anyway: it asserts what a *user* can see, not what an admin can read past
the rules.

## Running it

```bash
# print every call, execute nothing — safe anywhere, needs no secret
scripts/canary/run.sh --base-url https://app.jeeb.fds-1.com --plan

# actually assert
JEEB_TOKEN_MINT_KEY=… scripts/canary/run.sh \
  --base-url https://app.jeeb.fds-1.com --timeout 150 --execute

# one-time identity check (idempotent; nothing is created)
JEEB_TOKEN_MINT_KEY=… scripts/canary/ensure-canary-accounts.sh \
  --base-url https://app.jeeb.fds-1.com

# offline self-test of the jq/bash logic and the plan-mode contract
bash scripts/canary/test-canary-lib.sh
```

Bash + curl + jq only. Every secret is read from the environment and printed as
`$VARNAME`; plan mode issues no request at all.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `JEEB_CANARY_BASE_URL` | `https://app.jeeb.fds-1.com` | gateway origin (`--base-url`) |
| `JEEB_CANARY_TIMEOUT` | `150` | whole-run budget in seconds (`--timeout`) |
| `JEEB_TOKEN_MINT_KEY` | — | **required for `--execute`** |
| `JEEB_CANARY_CLIENT_ID` | `canary-chat-push-client` | fixed canary client |
| `JEEB_CANARY_JEEBER_ID` | `canary-chat-push-jeeber` | fixed canary jeeber |
| `JEEB_CANARY_LAT` / `_LNG` | `33.8886` / `35.4955` | fixed Beirut pickup |
| `JEEB_CANARY_AVAILABILITY_PREFIX` | `/v1` | edge prefix for the availability surface; try `/api` or `` if it 404s |
| `PUSH_LEDGER_BASE_URL` | unset | push-notification origin; enables the full push leg |
| `JEEB_PUSH_INTERNAL_API_KEY` | — | `X-Api-Key` for the ledger read |
| `JEEB_PUSH_CALLER_ID` | `jeeb-gateway` | `X-Caller-Id` for the ledger read |
| `JEEB_FIREBASE_WEB_API_KEY` | unset | enables the Firestore assertion |
| `JEEB_CANARY_ALLOW_FCM_TOKEN_REJECT` | `true` | accept a terminal `failed` as producer-chain proof |

## Triggers

`schedule` every 15 minutes, `workflow_dispatch`, and `workflow_call`. The
concurrency group is keyed on the base URL with `cancel-in-progress: false`, so
runs never overlap on one environment.

The cron **mutates staging**: one Flash request created and cancelled every 15
minutes, plus one chat message tagged `canary`. To stop it, comment out the
`schedule:` block — do not disable the whole workflow, or the deploy gate below
goes with it.

## Using it as a post-cutover deploy gate

The canary is exposed via `workflow_call` precisely so no edit to
`jeeb-staging-deploy.yml` is needed from here. Whoever owns that file adds one
job that runs **after** cutover and after `UseUpstream__Chat` is activated:

```yaml
  chat-push-canary:
    needs: [deploy]
    uses: ./.github/workflows/jeeb-chat-push-canary.yml
    with:
      base_url: https://app.jeeb.fds-1.com
      timeout: '150'
    secrets: inherit
```

Ordering matters: run it after chat activation, or it will correctly fail on the
`UseUpstream__Chat=false` that the deploy itself just wrote.

## When it fails

The run exits 1 naming the leg (`preflight`, `identity`, `presence`, `device`,
`request`, `push`, `chat`) and dumps the last 20 lines of evidence. Read the leg
name first:

| Leg | Most likely cause |
|---|---|
| `identity` | `JEEB_TOKEN_MINT_KEY` rotated, or the mint route moved |
| `presence` | availability prefix wrong at the edge (404), or heart-beat service auth rejected (401) |
| `device` | push-notification registration path down — the relay itself is unreachable |
| `request` | delivery-service / jeeb-state-service down, or the tier catalog lost its Flash row |
| `push` | **the outage class this exists for** — recipient resolution, the durable dispatcher flag, notification-service `WEBHOOK_BASE_URL`, or the FCM credential |
| `chat` | `503` ⇒ `UseUpstream__Chat` is off; a viewer-scoped miss ⇒ `VisibleTo[]` does not carry the jeeber; a uid mismatch ⇒ the Firebase mint and the app disagree on identity |
