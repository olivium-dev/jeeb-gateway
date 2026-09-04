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
| 2 presence | `PUT {prefix}/jeebers/me/availability` **carrying `latitude`/`longitude`**; `POST /location/update` probed, not required | the returned presence row echoes the pickup coordinate — a 200 with `latitude: null` FAILS |
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

### Presence rides the availability body, not `POST /location/update`

The GPS fix is sent **on the availability call**, because that is the app's only
location upload: the phone calls `PATCH /jeebers/me/availability` with
`latitude` + `longitude` and never calls `POST /location/update` at all (verified
against A33 logcat, 2026-09-04). `PatchCore` forwards those coordinates to
`delivery-service.SetAvailability`, which is the same presence row that
`NewRequestPushNotifier.ResolveRecipientsAsync` and the offer route's
`TierRadiusPolicy` read — so one call seats presence for both geo gates.

The leg then asserts the **echoed** `latitude`/`longitude`, not just the 200.
Until 2026-09-04 the canary sent availability with no coordinates and leaned on
`POST /location/update` for the fix; live staging answered that availability call
`200 {"latitude":null,"longitude":null}`, which is a presence row fan-out can
never match. `canary_presence_fix_landed` fails on exactly that body, and
`test-canary-lib.sh` pins it with the live shape as a fixture.

`POST /location/update` is still called, but **warn-only** (`canary_warn`), because
it is a geolocation-service batch-ingest path the app does not use and its outage
cannot invalidate the presence row above. On staging it currently answers
Cloudflare `502 origin_bad_gateway` for **every** caller and body shape (single
point, batch, GUID id, non-GUID id) while the identical call answers
`200 {"accepted":1}` on MSI — an origin-side staging fault, not a code defect and
not an id-shape defect. Everything that returns before `_store.RecordAsync`
(`{}` → 400, unknown `deliveryId` → 404, oversized batch → 400) answers clean
JSON through the same edge, so the fault is in the gateway's
geolocation-service ingest hop specifically. Set
`JEEB_CANARY_REQUIRE_GPS_STREAM=true` to make it fatal again once that is fixed.

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

### The canary ids MUST be well-formed UUIDs

`[RequireActiveUser]` guards request create, offer submit and accept. It calls
`IBanServiceClient.GetStatusAsync` and **fails closed on any exception** — by
design, so a ban-owner outage can never be read as "active". ban-service rejects
a non-UUID `user_id`, so a non-UUID canary id can never clear the gate: every
create returns `503 account-status-unavailable` while ban-service is perfectly
healthy and serving GUID callers.

The defaults are therefore fixed, deterministic, obviously synthetic UUIDs:

| Actor | Id |
|---|---|
| client | `ca9a4100-0000-4000-8000-000000000001` |
| jeeber | `ca9a4100-0000-4000-8000-000000000002` |
| funding partner holder | `ca9a4100-0000-4000-8000-000000000003` |
| funding admin | `ca9a4100-0000-4000-8000-000000000004` |

They share the `ca9a4100-0000-4000-8000-0000000000NN` prefix so any one of them
is recognisable as canary traffic at a glance, and they are stable across runs so
the trail stays bounded. `test-canary-lib.sh` asserts the shape offline.

*(An earlier revision deliberately used non-UUID ids to dodge the offer-time
wallet guard. That traded a guard we can satisfy for a gate we cannot — the ban
seam has no way to distinguish "malformed id" from "ban-service is down", and
fixing that is a security change needing its own review, not a canary change.)*

### The roles must be the CANONICAL vocabulary, not the contract one

user-management persists — and the OTP-verify path mints — the **opaque** roles
`{customer, driver}` (`Roles.Client = "customer"`, `Roles.Jeeber = "driver"`).
The snake_case `{client, jeeber}` pair is the **client-contract** vocabulary; the
gateway translates opaque → contract on the way *out* of a response body, and the
token never carries it.

Most gates cannot tell the difference, because the capability handler canonicalises
each principal role through `JeebRoleTranslator.ToContract` before matching. But a
handful of actions read the **raw** claim — `DeliveriesController.Cancel` does
`UserIdentity.HasRole(HttpContext, Roles.Client)`, i.e. literally `"customer"`. A
token minted with `roles:["client"]` therefore passed every capability gate the
canary walks and then **403'd the cleanup cancel on every run**, leaking a
broadcasting request until the Flash TTL expired.

So the canary mints what a real sign-in mints:

| Actor | `roles` |
|---|---|
| client | `["customer"]` |
| jeeber | `["driver","customer"]` |

**`driver` leads deliberately.** `TokensController` sets `active_role = roles[0]`
when the user has no user-management profile yet, and `active_role` drives things
like the FCM topic chosen at device registration. Keeping `customer` on the jeeber
matches the dual-role shape UM persists (one account can be both).

Overridable via `JEEB_CANARY_CLIENT_ROLES` / `JEEB_CANARY_JEEBER_ROLES`
(comma-separated). `test-canary-lib.sh` asserts the canonical values, the ordering,
and that the contract strings never appear — mutation-checked.

The cleanup also falls back to the legacy `DELETE /requests/{id}` on **403**, not
just 404/405, so a future vocabulary drift cannot silently leak requests again.

### The consequence: the canary jeeber needs a funded wallet

A UUID jeeber re-arms the offer-time wallet-sufficiency guard, which is
`Guid.TryParse`-gated. The guard needs
`RequiredCommission(fee) = round(fee × CommissionCalculator.FlatRate, 2)` — at
the canary's `fee: 6` that is **$0.60**. An unfunded wallet 402s the offer, which
reads like a chat outage if you do not know to look.

So `run.sh` reads `GET /v1/jeeb/wallet` immediately **before** leg 6 and fails
with a message naming the remedy, and `ensure-canary-accounts.sh` tops the wallet
up through the Dev Tool's own route chain:

1. mint an admin bearer for the canary admin id,
2. `POST /dev/partner/credentials` — provision a holder-bound partner credential
   with a **freshly generated password** (never committed, never printed, deleted
   at the end). The identifier is not free-form: `PartnerCredentialStore` accepts
   only `devtool-partner-<holderId without dashes>` and 409s on anything else, so
   the script derives it with `canary_runtime_partner_identifier`. A 409 here means
   a reservation for that holder is still live — it is tombstoned rather than
   deleted and carries another run's password, so it must be waited out (5 min),
3. `POST /v1/partner/auth/login` → partner session,
4. `POST /v1/admin/partners/{partnerId}/wallet/credits` — cash-credit the partner
   as admin, under a fixed idempotency key,
5. `POST /v1/partner/wallet/transfers/predict` — assert `otpRequired == false`,
6. `POST /v1/partner/wallet/transfers` — partner → canary jeeber, fixed
   idempotency key,
7. delete the partner credential, then re-read the balance and assert it clears.

**The idempotency that matters is the balance pre-check**: if the wallet already
clears `JEEB_CANARY_WALLET_MIN`, the whole chain is skipped. Re-running the script
any number of times never stacks credits.

**That is why the scheduled workflow runs it on every run**, as a step before
`run.sh --execute`. It is cheap when funded (one wallet read) and it is the only
thing standing between a drained wallet and a leg-6 402 that nobody is watching
for. Its `READY: … (funding: funded|already|skipped)` line goes to the job
summary, so a run that had to top up says so. `test-canary-lib.sh` asserts the
step exists, runs before the canary, and is not in plan mode.

`JEEB_CANARY_WALLET_TOPUP` defaults to **40** and must stay under
`PartnerWallet__OtpStepUpThreshold` (50) — above it the transfer needs a step-up
code and this stops being an unattended top-up. The script fails with that exact
message rather than silently trying.

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

# identity check + idempotent wallet top-up (skips funding when already funded)
JEEB_TOKEN_MINT_KEY=… scripts/canary/ensure-canary-accounts.sh \
  --base-url https://app.jeeb.fds-1.com

# print the funding chain without executing it
scripts/canary/ensure-canary-accounts.sh --base-url https://app.jeeb.fds-1.com --plan

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
| `JEEB_CANARY_CLIENT_ID` | `ca9a4100-…-0001` | fixed canary client — **must be a well-formed UUID** |
| `JEEB_CANARY_JEEBER_ID` | `ca9a4100-…-0002` | fixed canary jeeber — **must be a well-formed UUID** |
| `JEEB_CANARY_PARTNER_HOLDER_ID` | `ca9a4100-…-0003` | funding partner holder (ensure script only) |
| `JEEB_CANARY_ADMIN_ID` | `ca9a4100-…-0004` | funding admin (ensure script only) |
| `JEEB_CANARY_CLIENT_ROLES` | `customer` | canonical roles minted for the client (comma-separated) |
| `JEEB_CANARY_JEEBER_ROLES` | `driver,customer` | canonical roles for the jeeber; the first also becomes `active_role` |
| `JEEB_CANARY_WALLET_MIN` | `0.60` | offer-guard commission threshold asserted before leg 6 |
| `JEEB_CANARY_WALLET_TOPUP` | `40` | funding amount; must stay under the 50 OTP step-up threshold |
| `JEEB_CANARY_SKIP_FUNDING` | `false` | skip the funding chain in the ensure script |
| `JEEB_CANARY_LAT` / `_LNG` | `33.9500` / `35.2000` | fixed **offshore** pickup — see the hard rule above |
| `JEEB_CANARY_ACCEPT_OFFER` | `true` | `false` stops after the offer, creating no accepted delivery |
| `JEEB_CANARY_AVAILABILITY_PREFIX` | `/v1` | edge prefix for the availability surface |
| `JEEB_CANARY_REQUIRE_GPS_STREAM` | `false` | make a non-200 from `POST /location/update` fatal instead of a warning |
| `PUSH_LEDGER_BASE_URL` | unset | push-notification origin; enables the full push leg |
| `JEEB_PUSH_INTERNAL_API_KEY` | — | `X-Api-Key` for the ledger read |
| `JEEB_PUSH_CALLER_ID` | `jeeb-gateway` | `X-Caller-Id` for the ledger read |
| `JEEB_FIREBASE_WEB_API_KEY` | unset | enables the Firestore assertion |
| `JEEB_CANARY_ALLOW_FCM_TOKEN_REJECT` | `true` | accept a terminal `failed` as producer-chain proof |

Per-leg budgets exist so a slow push leg cannot starve the chat polls down to a
single attempt. `JEEB_CANARY_TIMEOUT` is a real ceiling, not a label: every
`canary_deadline` is clamped to `start + JEEB_CANARY_TIMEOUT`, so the sum of the
per-leg budgets can never overrun it.

## What the scheduled run actually proves today

Two legs run in a reduced mode on the current staging configuration, and it is
worth knowing which before reading a green run as full coverage:

- **Leg 8 (push) runs in `durable-inbox` mode.** `PUSH_LEDGER_BASE_URL` is unset
  (push-notification is not reachable from a GitHub runner), so the leg proves
  gateway → notification-service and **not** the FCM call. The run says so in its
  own log and in the job summary.
- **Leg 9 (Firestore) is skipped entirely.** `JEEB_FIREBASE_WEB_API_KEY` does not
  exist at repo or `staging` level, so the `VisibleTo` query against Firestore
  never runs. Leg 7's viewer-scoped read still proves the visibility lane through
  chat-service; what is missing is the assertion against the datastore the app
  actually renders from.

Both are one configuration change away — see the two sections above — and neither
is a code change.

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
| `presence` | availability prefix wrong at the edge (404), or heart-beat service auth rejected (401). A 200 that fails the coordinate assertion means the presence store accepted the toggle but dropped `latitude`/`longitude` — fan-out and the offer radius check would then match nobody |
| `device` | push-notification registration path down — the relay itself is unreachable |
| `request` | delivery-service / jeeb-state-service down, or the tier catalog lost its Flash row. **503 `account-status-unavailable` means an id stopped being a well-formed UUID**: `[RequireActiveUser]` fails closed when `IBanServiceClient.GetStatusAsync` throws, and ban-service rejects a non-UUID `user_id` — so this is a canary-config fault, not a ban-service outage |
| `cleanup` (warning, not a failure) | a **403** on `DELETE /v1/requests/{id}` means the minted roles drifted off the canonical `{customer,driver}` vocabulary — that route reads the raw claim. The legacy route is retried automatically; if both fail the request leaks until the tier TTL |
| `lifecycle` | **offer-service is a hard dependency of this leg** — 502/503 means offer-service is unreachable, not a chat or push outage. 409 `offer-out-of-range` ⇒ the GPS fix never reached delivery-service presence (also a hard dependency, via the presence row the radius check reads); 402 ⇒ the canary jeeber's wallet fell below the guard threshold — run `ensure-canary-accounts.sh` to top it up (the pre-check before leg 6 should catch this first); accept 409 ⇒ the request left the pre-acceptance phase |
| `push` | **the outage class this exists for** — recipient resolution, the durable dispatcher flag, notification-service `WEBHOOK_BASE_URL`, or the FCM credential |
| `chat` | `503` ⇒ `UseUpstream__Chat` is off; a viewer-scoped miss ⇒ `VisibleTo[]` does not carry the jeeber; a uid mismatch ⇒ the Firebase mint and the app disagree on identity |
