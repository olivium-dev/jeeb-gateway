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
| 0 preflight | `GET /health/live` | reachable (the **only** health read; it gates nothing else) |
| 1 identity | `POST /auth/tokens` ×2 with `X-Service-Auth-Key` | both fixed canary bearers mint |
| 2 presence | `PUT {prefix}/jeebers/me/availability`, `POST /location/update` | jeeber online with a GPS fix at the pickup point |
| 3 device | `PUT /api/PushNotification/register` | the jeeber has an FCM seat, so a dispatch row can exist |
| 4 request | `GET /tiers`, `POST /v1/requests` | a Flash request at the fixed offshore coordinate |
| 5 chat gate | `GET /v1/chat/jeeb/conversations/by-request/{id}` (create on 404) | the conversation resolves — **a 503 here is `UseUpstream__Chat=false`** |
| 6 lifecycle | `POST /v1/requests/{id}/offers` as the jeeber, `POST /v1/offers/{id}/accept` as the client | the jeeber is actually seated in the conversation |
| 7 chat | `POST /v1/conversations/{id}/messages`, then `GET …/messages` **as the jeeber**, then `POST /v1/chat/firebase-token` | the recipient's own viewer-scoped page carries the message, and the minted Firebase uid equals the jeeber id |
| 8 push | ledger or inbox (below) | a push outcome for the jeeber |
| 9 cleanup | EXIT trap: availability off + cancel | bounded trail **even when the run fails** |

### Why leg 5 runs before leg 8

`UseUpstream__Chat` self-reverts on every staging gateway deploy and is the
top-ranked cause of chat outages. If the push leg ran first, a simultaneously
broken push would consume the budget and the run would never reach — and never
name — the flag. The chat gate therefore comes first, so the likeliest root
cause is always reported by name.

### The create MUST be `POST /v1/requests`

New-request fan-out lives **only** on the V1 create route. The legacy
`POST /requests` has no `NotifyNewRequestAsync` caller at all and seeds no
delivery-service row, so a request created there pushes nothing, produces no
durable `jeeb.new_request` record, and gives the accept saga nothing to work
with. Both routes return 201 with an id, which is what makes the mistake
invisible: the canary looks healthy right up to a push leg that can never go
green. `test-canary-lib.sh` asserts the V1 route by value and asserts the legacy
route is absent, so a regression fails offline in CI.

### Why leg 6 exists (this is not optional)

chat-service creates the conversation with **only the owner** as a participant.
The jeeber becomes a participant through an offer (`jeeber_offerer`), and a full
`Participant` through accept. Without leg 6, `GET …/messages` as the jeeber is a
403 from chat-service's membership gate, and the visibility assertion would fail
on a perfectly healthy stack.

Two further consequences, both load-bearing:

- The message is sent with **`"audience": "all"` — a string, not an object.**
  The visibility resolver treats a structured `audience` as opaque and falls back
  to the role matrix, under which a restricted offerer sees only their own
  messages. A string `"all"` is the shape the resolver actually reads.
- `JEEB_CANARY_ACCEPT_OFFER=false` stops after the offer. The jeeber is then
  seated as a restricted offerer, which the `"all"` audience still satisfies —
  and no accepted delivery is created per run. Set it if the accepted-delivery
  trail becomes a problem; the default (`true`) walks the full real path and
  exercises the offer and accept push events too.

### Why the canary ids are not GUIDs

`canary-chat-push-client` / `canary-chat-push-jeeber` do not parse as GUIDs, and
the offer route's wallet-sufficiency guard only runs when the caller id does
(`Guid.TryParse`). A GUID-shaped canary jeeber would need a funded wallet to
place its offer. Keep these ids non-GUID.

### The fixed coordinate is offshore, and that is a hard rule

New-request fan-out pushes to **every online jeeber within the tier radius**
(Flash = 3 km) of the pickup point. A downtown-Beirut coordinate would send a
real "New delivery request" to every real staging tester's phone, 96 times a day.
The default `33.9500, 35.2000` is in the sea west of Beirut: no tester is ever
within 3 km, and the only jeeber the fan-out can reach is the canary's own,
because leg 2 uploads its fix to exactly that point. The offer route re-checks
the same radius fail-closed, so leg 6 also proves the fix landed.

**If you change `JEEB_CANARY_LAT/LNG`, move the jeeber's fix with it, and keep it
away from anywhere a human tests.**

### Out of scope

The Phoenix socket lane — `GET /v1/realtime/jeeb:chat:{id}`, the membership
ticket, and `wss://…/socket/websocket` — is **not** probed. Staging runs with
`UseUpstream__Realtime=false`, so that leg is dead by design there, and the app
renders chat from Firestore regardless. A socket canary is a separate probe.

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

To get the full leg, set the repo variable `JEEB_PUSH_LEDGER_BASE_URL` to a URL the
runner can reach. **Do not bolt a cloudflared SSH hop onto a 15-minute cron** — it
doubles the runtime and the secret surface for every run. The right shape is a
gateway admin proxy for the `?target_user_id=` list form, sitting next to the
existing `GET /admin/v1/case-recovery/push-dispatches/{key}`; that is a small
gateway change, not a canary change. Until then, `push-relay-scoped-readiness`
(in the 19/19 roster since #548) is the standing guard on the relay credential.

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

**The secret does not exist yet.** `JEEB_FIREBASE_WEB_API_KEY` is present at
neither repo nor `staging` environment level, so this leg is currently skipped —
and `jeeb-chat-firebase-live-smoke.yml` is silently degraded for the same reason.
The value is the *public* Web API key of `jeeb-5a293` (Firebase console → project
settings; also the `current_key` in the mobile `google-services.json`). Add it as
a **repo** secret — the canary job declares no `environment:` — and this leg
turns on with no code change.

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
`$VARNAME`; plan mode issues no request at all. Bearers are masked with
`::add-mask::` only when `GITHUB_ACTIONS` is set — outside Actions that directive
is not consumed by anything, so emitting it would print the token instead of
hiding it. A local `--execute` run therefore prints no bearer at all.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `JEEB_CANARY_BASE_URL` | `https://app.jeeb.fds-1.com` | gateway origin (`--base-url`) |
| `JEEB_CANARY_TIMEOUT` | `150` | whole-run hard cap in seconds (`--timeout`); every per-leg deadline is clamped to it |
| `JEEB_CANARY_PUSH_BUDGET` | `60` | budget for the push poll alone |
| `JEEB_CANARY_CHAT_BUDGET` | `30` | budget for the recipient-visibility poll |
| `JEEB_CANARY_FIRESTORE_BUDGET` | `30` | budget for the Firestore poll |
| `JEEB_TOKEN_MINT_KEY` | — | **required for `--execute`**; the run refuses to start without it |
| `JEEB_CANARY_CLIENT_ID` | `canary-chat-push-client` | fixed canary client (keep it non-GUID) |
| `JEEB_CANARY_JEEBER_ID` | `canary-chat-push-jeeber` | fixed canary jeeber (keep it non-GUID) |
| `JEEB_CANARY_LAT` / `_LNG` | `33.9500` / `35.2000` | fixed **offshore** pickup — see the hard rule above |
| `JEEB_CANARY_ACCEPT_OFFER` | `true` | `false` stops after the offer, creating no accepted delivery |
| `JEEB_CANARY_AVAILABILITY_PREFIX` | `/v1` | edge prefix for the availability surface |
| `PUSH_LEDGER_BASE_URL` | unset | push-notification origin; enables the full push leg |
| `JEEB_PUSH_INTERNAL_API_KEY` | — | `X-Api-Key` for the ledger read |
| `JEEB_PUSH_CALLER_ID` | `jeeb-gateway` | `X-Caller-Id` for the ledger read |
| `JEEB_FIREBASE_WEB_API_KEY` | unset | enables the Firestore assertion |
| `JEEB_CANARY_ALLOW_FCM_TOKEN_REJECT` | `true` | accept a terminal `failed` as producer-chain proof |

Per-leg budgets exist so a slow push leg cannot starve the chat polls down to a
single attempt. `JEEB_CANARY_TIMEOUT` is a real ceiling, not a label: every
`canary_deadline` is clamped to `start + JEEB_CANARY_TIMEOUT`, so the sum of the
per-leg budgets can never overrun it.

## Hard dependencies beyond chat and push

Because leg 6 walks the real lifecycle, this canary also fails when
**offer-service** or **delivery-service** is down. That is deliberate — those are
real-app dependencies of a real chat thread — but it means a red `lifecycle` leg
must not be read as a chat or push outage. The leg name in the failure message is
the thing to read first.

## Independence from SuperLogin open mode — read this before trusting the claim

The canary passes `X-Service-Auth-Key: $JEEB_TOKEN_MINT_KEY` and **refuses to
start in `--execute` without it**, so it never *relies* on open mode. But that is
not yet the same as being independent of it: staging currently runs
`SuperLogin__OpenMode=true`, and `AuthorizeMint()` short-circuits before any key
check — so today the header is accepted regardless of its value. The deploy sets
`Security__TokenMint__Enabled=true` but **no `Security__TokenMint__Key`**, in
which case the gate falls back to `JeebJwt__SigningKey`.

**Owner action, one of:** set `Security__TokenMint__Key` from the repo secret
`JEEB_TOKEN_MINT_KEY` in the staging deploy, or confirm that `JEEB_TOKEN_MINT_KEY`
and `JEEB_JWT_SIGNING_KEY` hold the same value. Until one of those is true, the
first close of open mode will break this canary — which is the correct alarm, but
it should be a planned one.

## Triggers

`schedule` every 15 minutes, `workflow_dispatch`, and `workflow_call`. The
concurrency group is keyed on the base URL with `cancel-in-progress: false`, so
runs never overlap on one environment.

The cron **mutates staging**: per run, one Flash request (created, offered on,
accepted, then cancelled), one conversation, one chat message and one durable
notification row. Cleanup runs from an `EXIT` trap, so a **failed** run still
cancels the request and puts the canary jeeber offline. Conversations and
messages are never deleted — there is no API — but they carry `subtype: canary`.
Set `JEEB_CANARY_ACCEPT_OFFER=false` to stop creating accepted deliveries. To
stop the cron, comment out the `schedule:` block — do not disable the whole
workflow, or the deploy gate below goes with it.

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
| `lifecycle` | **offer-service is a hard dependency of this leg** — 502/503 means offer-service is unreachable, not a chat or push outage. 409 `offer-out-of-range` ⇒ the GPS fix never reached delivery-service presence (also a hard dependency, via the presence row the radius check reads); 402 ⇒ the canary id became GUID-shaped and hit the wallet guard; accept 409 ⇒ the request left the pre-acceptance phase |
| `push` | **the outage class this exists for** — recipient resolution, the durable dispatcher flag, notification-service `WEBHOOK_BASE_URL`, or the FCM credential |
| `chat` | `503` ⇒ `UseUpstream__Chat` is off; a viewer-scoped miss ⇒ `VisibleTo[]` does not carry the jeeber; a uid mismatch ⇒ the Firebase mint and the app disagree on identity |
