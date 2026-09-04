# Chat activation state, and what `/health/ready` now says about it

2026-09-04. Companion to `staging-gateway-a1-b-rollout-checklist.md`.

## The ratchet this replaces

`jeeb-staging-deploy.yml` used to write `FeatureFlags__UseUpstream__Chat=false` from two
hardcoded literals — the `add_env` row and the post-deploy `verify_bootstrap_flags` contract.
With that flag off, **every** `/v1/conversations/*` and `/v1/realtime/*:chat:*` route returns
503 (`JeebConversationsController.UpstreamUnavailable`). The only writer of `=true` is
`jeeb-chat-b-activation.yml`, a `workflow_dispatch`-only workflow. So a completed Chat B
activation was reverted by the next staging deploy, and restoring it required a human to
remember a manual workflow. Nothing on `/health/ready` showed chat was off.

## Single source of truth

The deploy step resolves the flag **once**, near the top of `Deploy Swarm service`, and both
consumers read that one variable. Resolution order:

| # | Source | Wins when |
|---|---|---|
| 1 | `vars.JEEB_STAGING_CHAT_ENABLED` (repository or `staging` environment variable) | set to exactly `true` or `false`; any other non-empty value fails the deploy with exit 64 |
| 2 | the incumbent service's persisted `FeatureFlags__UseUpstream__Chat` (`scripts/read-staging-chat-flag.sh`) | the variable is unset and the running service declares the row |
| 3 | `true` | nothing is declared and nothing is persisted |

Rung 2 is what makes Chat B activation durable: `jeeb-chat-b-activation.yml` persists `=true`
onto the Swarm service, and the next deploy carries that value forward instead of stomping it.
Rung 3 means a fresh service comes up with chat **on** — a deploy is never the thing that
turns chat off.

`chat_upstream_enabled` is consumed in three places, and the third has a trap: the candidate-Spec
builder pipes a **quoted** `REMOTE` heredoc to the staging host, so the name is expanded *there*,
not on the runner. It must therefore appear in the `for variable in …` forwarding list — the remote
runs `set -euo pipefail`, so an unforwarded name aborts the deploy with `unbound variable` (run
`33819016720`). `scripts/check-remote-heredoc-variable-forwarding.sh` now enforces this for every
name, not just this one.

### Day 0 — this PR does NOT turn chat on

**The first staging deploy after merging this lands `Chat=false` and chat stays off.** Rung 1 is
empty (no `JEEB_STAGING_CHAT_ENABLED` variable exists at repository or `staging` environment level)
and the incumbent persists `false` (live `/v1/conversations*` 503s "UseUpstream:Chat is off"), so
rung 2 carries `false` forward. This change removes the ratchet; it does not perform the
activation. To actually turn chat on, do **one** of:

- set repository or `staging`-environment variable `JEEB_STAGING_CHAT_ENABLED=true` and deploy —
  after which the variable may be deleted, because rung 2 now carries `true` forward; **or**
- dispatch `jeeb-chat-b-activation.yml` (its precondition — exactly one row, `=false` — is
  satisfied today), which is now durable across later deploys.

Order matters: after the first option the activation workflow refuses, because its precondition
requires the incumbent to be `false`. It activates; it does not toggle.

### Operating it

- **Turn chat off deliberately:** set repository/environment variable
  `JEEB_STAGING_CHAT_ENABLED=false` and deploy. This is the only way a deploy lands `false`
  when the incumbent is `true`, and it is recorded in the run log
  (`Chat upstream for this deploy: false (from declared vars.JEEB_STAGING_CHAT_ENABLED)`).
- **Turn chat on:** run `jeeb-chat-b-activation.yml` (unchanged — protected environment, owner
  gate, typed confirmation, live Firebase identity smoke, A1 forward-fix on failure). Its
  precondition still requires the incumbent to be `false`, so it activates rather than toggles;
  it is now durable because the deploy no longer reverts it. Optionally also set
  `JEEB_STAGING_CHAT_ENABLED=true` so the declared and persisted states agree.
- **Staged A1 bootstrap:** set `JEEB_STAGING_CHAT_ENABLED=false` before the A1 deploy. The
  `deploy/staging-gateway/{a1-bootstrap,b-activation}.env` phase contracts are unchanged and
  still describe the two phases.

The Chat B activation workflow's 11-step protected shape (`validate-chat-b-activation-authority.py`)
was deliberately **not** restructured: it is PR #552's reviewed mutation authority, and the
ratchet is fixed on the deploy side where it lived.

### Gates that keep it fixed

- `scripts/check-staging-gateway-phase-contracts.sh` and `scripts/validate-jeeb-firebase-contract.py`
  and `scripts/check-staging-realtime-probe-contract.sh` each reject a re-introduced
  `add_env FeatureFlags__UseUpstream__Chat <literal>`.
- `scripts/test-staging-chat-flag-resolution.sh` (CI) **extracts** the resolver from the
  workflow and executes it against a stubbed `ssh`/`docker` across all six resolution cases.
- `scripts/check-remote-heredoc-variable-forwarding.sh` (CI) rejects any variable expanded
  inside the quoted `REMOTE` heredoc that the deploy does not forward to the staging host.
  `scripts/test-remote-heredoc-variable-forwarding.sh` proves it against four mutants, one of
  which is the exact regression of run `33819016720`.
- The deploy asserts what it declared. Degraded is HTTP 200, so `curl -fsS` on `/health/ready`
  cannot see a chat-off deploy; `verify_chat_readiness_row` (after `verify_bootstrap_flags`, and
  skipped in `devtool-reassert`) reads the JSON and requires `chat-upstream-readiness` to be
  `Healthy` when the resolved flag is `true`, or `Degraded` with `disabled by flag` when it is
  `false`. A chat-off deploy — or a chat-on deploy whose Firestore probe is UNVERIFIED — now fails
  the gate instead of passing silently.

### Rung 2 fails open, loudly

Any SSH or `docker service inspect` failure resolves to rung 3 (`true`) rather than deploying chat
off on a read error. The two cases are logged distinctly — `::warning::no persisted chat state on
incumbent; defaulting true` versus `::warning::could not read the incumbent chat state; defaulting
true` — so the log never claims "absent" when it means "unreadable". The only durable *off* is the
declared variable.

## `/health/ready` roster

**`GatewayHealthRoster.ExpectedReadyCount` is 27.** That is the DECLARED roster and the number the
roster tests assert. It is not what a live host serves: `whisper` and `push-relay-scoped-readiness`
register conditionally, so each environment reads fewer rows.

| | declared | staging wire | MSI wire |
|---|---|---|---|
| before | 20 | 19 (no `whisper`) | 18 (no `whisper`, no `push-relay-scoped-readiness` — gap G5) |
| after | **27** | **26** | **25** |

The skew is pre-existing and unchanged here; this change adds the same 7 rows to the declared roster
and to both wires. Measured baseline (2026-09-03, before this landed): staging `/health/ready` = 200
with 19 rows, all Healthy, no chat row. When comparing a live host against the constant, subtract
the conditional rows before calling it a drift.

### `chat-upstream-readiness`

`src/JeebGateway/Health/ChatUpstreamHealthCheck.cs`, tagged `ready` + `downstream`, 3s budget,
never throws. Registered by `HealthCheckExtensions` and gated on `ChatServiceApi:BaseUrl`, so it
is skipped in Development/Testing exactly like the other downstream probes.

| State | Result |
|---|---|
| `FeatureFlags:UseUpstream:Chat` false | **Degraded** — "chat disabled by flag" |
| enabled, `ChatServiceApi:BaseUrl` unset | **Degraded** |
| `GET /api/Health/firebase` 200 | **Healthy** (Firestore reachable) |
| any other 2xx (a post-#118 build answers a bodiless **204**) | **Healthy** — "Firestore round-trip UNVERIFIED (legacy 204)" |
| `GET /api/Health/firebase` 404, `GET /api/Health/check` 2xx | **Degraded** — older chat-service; Firestore is UNVERIFIED |
| any other status / timeout / transport fault | **Unhealthy** |

Every 2xx is accepted deliberately. `Dockerfile` runs `HEALTHCHECK … /health/ready`, so an
Unhealthy row here is not a red dashboard entry — Swarm restarts the gateway task. A chat-service
answering 204 with chat on would otherwise restart-loop the gateway. The weaker proof is stated in
the description and asserted by the deploy gate instead.

The previous exclusion comment ("chat-service exposes NO health route") was factually wrong:
chat-service serves `GET /api/Health/check`, and since 2026-09-03 (#116/#118) a real Firestore
probe at `GET /api/Health/firebase`.

### One row per declared credential

`NotificationCredentialHealthCheck` is generalised into
`src/JeebGateway/Health/ConfiguredCredentialHealthCheck.cs`, registered once per entry in
`GatewayCredentialDeclarations.All`. Each declaration carries an *armed* gate and an ordered
resolution chain, and the six Swarm-only `/run/secrets` defaults are **deleted from
`appsettings.Production.json`**. Where a base `appsettings.json` dev default exists
(`BUNDLER_CMS_BEARER_TOKEN_FILE`), Production explicitly neutralises it to `""` so Production
cannot inherit it — the base value stays only so local/CI construction of the CMS store keeps
working. Committed configuration therefore declares KEYS only for every deployed environment;
the deploy supplies the path or the value. That is the 608debf class — a Swarm path baked in as
a code default on a native host — closed structurally.
`ChatAndCredentialReadinessTests.No_effective_production_configuration_defaults_a_credential_to_a_host_path`
asserts the MERGED base+Production configuration, so re-adding a default in either file fails CI.

| Row | Armed when | Chain (first usable rung wins) |
|---|---|---|
| `notification-credential` | `FeatureFlags:NotificationDurableWrite:Enabled` | `ServiceNotificationClient:ServiceTokenFile` (file) → `:ServiceToken` → `NOTIFICATION_SERVICE_TOKEN` → `:ApiToken` |
| `credential-state-service-token` | `JeebStateService:Enabled` | `JeebStateService:ServiceTokenFile` (file) |
| `credential-delivery-service-token` | `FeatureFlags:UseUpstream:Delivery` | `DELIVERY_SERVICE_TOKEN_FILE` → `Services:Delivery:ServiceTokenFile` (files) → `DELIVERY_SERVICE_TOKEN` → `Services:Delivery:ServiceToken` |
| `credential-bundler-cms-bearer` | `BUNDLER_CMS_BASE_URL` set | `BUNDLER_CMS_BEARER_TOKEN_FILE` (file) |
| `credential-internal-job-token` | always | `InternalJobAuth:TokenFile` (file) |
| `credential-private-artifact-store-bearer` | `Users:DataExport:Enabled` and `PRIVATE_ARTIFACT_STORE_BASE_URL` set | `PRIVATE_ARTIFACT_STORE_BEARER_TOKEN_FILE` (file) |
| `credential-data-export-signing-key` | `Users:DataExport:Enabled` | `DATA_EXPORT_TOKEN_SIGNING_KEY_FILE` (file) |

Statuses:

- **Healthy** — not armed, or resolved cleanly; the description names the rung that resolved.
- **Degraded** — armed with no source configured at all (names the chain the deploy must
  supply), or resolved from a later rung while an earlier configured rung was unusable. The
  second case is the F6 masking: an inline value silently covering an unmounted secret file is
  now visible instead of green.
- **Unhealthy** — armed, a source is configured, and nothing in the chain resolves. The
  description names the keys and paths; it never contains token material.

Degraded keeps `/health/ready` at HTTP 200, so a truthful non-fatal finding does not pull the
gateway from rotation. Only Unhealthy 503s.

### Swarm vs native resolution after this change

| Credential | Staging (Swarm) | MSI (native systemd) |
|---|---|---|
| notification | `ServiceNotificationClient__ServiceTokenFile=/run/secrets/notification_service_token` (deploy) | `ServiceNotificationClient__ApiToken` / `NOTIFICATION_SERVICE_TOKEN` env, per PR #522 |
| state-service | `/run/secrets/jeeb_state_service_token` (deploy) | host path in the unit's environment |
| delivery | not supplied → **Degraded**, truthfully: delivery calls have no credential on staging | host path or `DELIVERY_SERVICE_TOKEN` |
| bundler-cms | `/run/secrets/bundler_cms_bearer_token` (deploy) | host path |
| internal-job | `/run/secrets/jeeb_gateway_job_token` (deploy) | host path |
| private artifact / data export | `Users__DataExport__Enabled=false` → not armed | armed; the unit must supply both paths |

**MSI operators:** the six keys no longer have a committed default. If a systemd unit relied on
the `appsettings.Production.json` value, it must now set the key explicitly — and if it does not,
the corresponding `/health/ready` row says so by name instead of failing at the first user
request.
