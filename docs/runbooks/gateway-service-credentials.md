# Gateway service credentials — env keys, arming order, verification

> **STATUS 2026-08-16 — REFERENCE ONLY. Arming anything NEW is not queued work.**
> Owner ruling: *"Security between microservices is not needed, keep it simple."* No mTLS, no
> per-hop tokens, no service-user split and no new internal-hop credential is to be added — or
> proposed. OA-11 is closed as won't-do with the risk accepted, and the queued
> `IMPORT_API_TOKEN` / `SETTLEMENT_API_TOKEN` rotation is cancelled. What is already armed stays
> as it is; these recipes are kept because they are correct, not because anything is scheduled.
> **Still open and NOT covered by that ruling: OA-22**, a push-webhook `X-Api-Key` that leaked
> into a session transcript — a leaked key is a different question from whether internal hops
> need auth.

> **FACT CORRECTION 2026-08-16 — this file used to say both credentials were absent on MSI.
> They are not; they are ARMED.** Measured on the live gateway (`1753f97`, started 10:05:49) by
> listing env KEY names and value LENGTHS only, never values: both
> `JeebStateService__ServiceTokenFile` and
> `Users__DataExport__FeedbackRatings__ServiceTokenFile` are set to 57-character paths, both
> target files exist under `/home/ec2-user/iter5-native/secrets/` mode `0600`, and the gateway
> journal carries `jeeb-state-service: credential ARMED from …`. Read the arming procedure below
> as the record of how that state was reached, **not** as pending work.

Two gateway→upstream shared secrets are opt-in by env. **Both are ARMED on MSI today** (see the
correction above); the text below describes how they are armed and how to prove it.

| env key | secret file on MSI | what it switches on |
|---|---|---|
| `JeebStateService__ServiceTokenFile` | `/home/ec2-user/iter5-native/secrets/state-ownership-token` | Bearer auth on **every** jeeb-state-service HttpClient (idempotency, cases, refresh tokens, disputes, saga-bundle + broadcast recorders) |
| `Users__DataExport__FeedbackRatings__ServiceTokenFile` | `/home/ec2-user/iter5-native/secrets/feedback-export-token` | the GDPR export's real ratings consumer (PR #378); unset keeps the in-memory provider that exports `"ratings": []` |

## jeeb-state-service credential

Order is load-bearing — **gateway first, then state-service**. The gateway sends the credential
whenever it is armed; state-service only starts requiring it after its own cutover.

1. `printf '%s' "$TOKEN" > secrets/state-ownership-token` — **no trailing newline**.
   Asymmetry pinned by `ATrailingNewlineInTheSecretFileIsTrimmedOffTheWire`: the gateway *trims*
   the file, state-service's `OwnershipCredentialFile` does *not*. A newline makes the two sides
   disagree and every `/v1` call 403s.
2. `chmod 0600`, owner `ec2-user` (the gateway's own user).
3. Add the env line, restart the gateway.
4. The journal must carry
   `jeeb-state-service: credential ARMED from …`. An armed-but-unusable secret is a **boot
   failure**, by design — the gateway refuses to start rather than 500 every login.
5. **Login canary, mandatory.** `/health/ready` is not proof: the state health check probes
   `{BaseUrl}/health`, which is outside the authenticated `/v1` surface, so it stayed green through
   the 2026-08-11 outage. Run a real `POST /v1/auth/otp/request` + `/verify` and one authenticated
   read before declaring success; roll back otherwise.

## feedback-ratings export credential

Add the env line and restart. The consumer is inert without it. Prove it with a real
`POST /users/me/data-export` for a user who has ratings — an empty `ratings` array is the exact
silent lie the consumer removes, so an empty result means the key did not take effect.

While editing `env/gateway.env`: `FeedbackServiceApi__BaseUrl` is assigned twice (empty, then
`http://127.0.0.1:10064`). Last-wins makes it correct today; drop the empty line.
