# Runbook — deploying the session role-continuity fix (G5)

**Applies to:** the deploy that first ships `RefreshToken.SessionRoleSnapshot` (PR #562).
**One-time only.** Every deploy after this one is ordinary.

## What this deploy does to live sessions

**Every active session is logged out once, fleet-wide, within ~15 minutes of the deploy.**
Plan for it, and tell testers before dispatching.

Why: after the service is replaced, the in-process `InMemoryUsersStore` is empty (durability
register #8 is not armed — it is a singleton `ConcurrentDictionary` whose only writer is OTP verify),
and every refresh record written *before* this deploy has `SessionRoleSnapshot == null`. Those
records therefore take the legacy path, resolve `[]`, and now hit the fail-closed branch:
`RoleResolutionFailed` → **401**.

On the client that 401 is terminal by design: mobile's proactive lane refreshes near the 15-minute
access-token expiry, the reactive lane (`auth_interceptor.dart`, `allowTerminal: true`) sees the
refresh 401 and calls `_logout()`. So each user re-logs in once and is then permanently fixed.

**This is strictly better than not deploying.** On the current `main` the identical restart produces
a *silent* roles-less token instead: the app still looks signed in, `/tiers` still 200s, and every
capability route — `GET /v1/users/me`, `PUT /api/PushNotification/register`, `GET /requests` —
returns `403 forbidden-capability` forever, with push registration retrying in a loop. Users have no
way to recover except noticing the app is broken and re-logging in manually. A clean 401 logout
replaces an unrecoverable 403 brick.

## Steps

1. Announce the one-time re-login to anyone holding a live session (testers, demo accounts, the
   staging phones).
2. Deploy via `jeeb-staging-deploy.yml` as usual (owner-gated dispatch).
3. Expect a burst of `auth.refresh role resolution yielded no roles` warnings for up to ~15 minutes.
   **That is the expected drain of pre-deploy records, not an incident.** It must fall to ~zero once
   users have re-logged in; a sustained rate afterwards is a real defect — investigate.
4. Re-login: mobile users re-authenticate normally (OTP). The staging phones re-login through
   Dev Tool → **Super Login Plus** (no OTP needed).
5. Verify on one device: `GET /v1/users/me` **200** and `PUT /api/PushNotification/register` **201**,
   then leave the app idle >15 minutes and confirm both still succeed after the rotation. That last
   step is the actual regression check — the bug only appears *after* a rotation.

## Recognising this class in 30 seconds (added after the deploy above)

Two signals now exist. Before them the class was invisible: `/health/ready` read green while every
`[RequireCapability]` route 403'd (D2 §4).

**1. The readiness row `refresh-role-continuity`** on `/health/ready` (declared roster 27 → 28):

```json
"refresh-role-continuity": {
  "status": "Degraded",
  "description": "usersStoreProfiles=0 refreshFamiliesActive=412 rolesEmptyRefreshes=37 lastRolesEmptyAt=2026-09-04T09:12:31.0000000Z"
}
```

**Degraded when `usersStoreProfiles == 0` while `refreshFamiliesActive > 0`** — the users store
holds nothing while live sessions rotate against it. Healthy otherwise, including a cold boot with
nothing rotating yet. It is `Degraded`, never `Unhealthy`, so it cannot restart-loop the container
via the Dockerfile `HEALTHCHECK`.

`refreshFamiliesActive` counts distinct sessions *this process has served a rotation for* since
boot — a lower bound on live families, not an inventory. The durable refresh store is a
key-addressed KV with no enumeration, so no process can count live sessions outright; what it can
observe is that real sessions depend on it, which is the population an empty users store harms.
The row therefore trips on the first rotation after a restart, i.e. on the first user, not after
the fleet is gone.

**2. The grep-able log line**, `Error` level, emitted at the exact point a roles-less token would
have been minted (now the fail-closed 401):

```
token_mint.roles_empty path=refresh source=users_store_miss activeRole=client snapshot=absent
```

```bash
journalctl -u jeeb-gateway | grep -c token_mint.roles_empty     # MSI
```

`snapshot=absent` is a pre-G5 record (expected during the one-time drain above);
`snapshot=rejected` means a record carried a snapshot that failed validation — that one is a minter
regression, not a drain, and should be investigated. No user id, token or role list is ever logged.

## Rolling / mixed-version window

If two gateway versions ever serve simultaneously, an old-code replica that wins a rotation writes a
replacement record *without* the snapshot; that chain drops to the legacy path on its next new-code
rotation and gets the same one-time 401. Staging is a single Swarm service replace, so the window is
seconds. No action needed — just do not treat those extra logouts as a new fault.

## Rollback

Reverting is safe and needs no data migration: the new fields are additive JSON on the
state-service-backed record, old code ignores unknown members, and new code reads `null` from old
rows. Rolling back restores the pre-deploy behaviour — including the 403 brick.
