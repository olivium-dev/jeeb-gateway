# Paired delivery activation — source draft, not an executable release route

This draft is based on gateway `2d91702b6f927d21870b331dbc3de0b2a3d7d14b`.
It adds no workflow, provisioning command, service mutation endpoint, or default
implementation for the required adapters. It cannot run against staging as-is.
Hermetic ordering tests are not runtime credential or deployment evidence.

## Existing components and shared contract

Use `staging-gateway-mutation-lock.sh` for the entire transaction, including final
proof and journal acknowledgement. Delivery's regular CAS deployment must use
the same `jeeb-staging-gateway.lock`/`.owner` protocol. Its normal retention-only
helper is deliberately not an auth-activation mechanism.

Use `staging_gateway_security_cutover_forward_apply` for each captured full-Spec,
Service.ID and Version.Index transaction. Never invoke spec recovery or automatic
rollback. Capture and submit adapters accept only the fixed gateway and delivery
roles. Each service's exact full candidate must reconcile after a successful CAS;
HTTP success alone is insufficient. An uncertain first submission forbids the
second. No repeated POST is permitted by this draft.

## Mandatory integration gates

- `paired_require_current_protected_builds`: GitHub-authoritative protected main
  and exact current source commit for BOTH repositories; exact full-commit build
  records and immutable digests; explicit cross-repository package authorization.
  Verify again before each mutation. A caller boolean or image tag is not proof.
- `paired_require_exact_daemon`: fixed Unix socket `/var/run/docker.sock`, exact
  staging daemon `olivium-ephemerals`, manager address `192.168.2.20`, active manager,
  supported API range including explicitly selected 1.52. No context/host override.
- `paired_require_existing_credential`: owner-controlled provisioning is separate.
  Require existing `jeeb-staging-delivery-service-auth-v1`, exact existing SecretID,
  staging/purpose/version labels, validated private token custody and format.
  No key generation, replacement, raw-token output, or digest publication.
- `paired_validate_narrow_candidates`: compare complete baseline-derived Specs.
  Only exact build image, dedicated auth mount/declarations, and delivery
  `SKIP_DB_INIT=true` may differ. Preserve all unrelated settings. Gateway mount
  UID/GID `65532/65532`, delivery `0/0`, mode `0400`, target
  `delivery_service_token`; both use the SAME SecretID and exact file path.
  Delivery mode is `required`; no inline token or conflicting alias is accepted.
- `paired_probe_delivery_schema`: reuse the reviewed delivery container-only
  schema diagnostic. Bind candidate image and the incumbent's exact DSN bytes to
  protected target/credential identity. Verify the existing attachable encrypted
  overlay; disable health checks; inspector-only entrypoint, private stdin, bounded
  execution, exact owned cleanup and unchanged incumbent. No migrations or rows.

These gates are mandatory callbacks, not implemented successes. The missing
adapters are a deliberate release blocker, not permission to skip a check.

## Ordering and durable record

After all preflight gates, an exclusive durable journal begin must reject existing
history. Persist and fsync a nonsecret record before each submission, with exact
run identity, both source commits/digests, both service IDs/versions, dedicated
SecretID and phase. Validate ownership, mode, nonsymlink ancestors and atomic
write/acknowledgement. Never store full Specs, DSN bytes, credentials or their
digests in the record. Journal adapters must enforce only:

`prepared → gateway-submission-pending → gateway-verified → delivery-submission-pending → delivery-verified → complete`

Gateway is updated and verified first. Its loaded token format/stat and actual
runtime must pass; a conditionally skipped credential check is not evidence.
Only then update delivery to required mode, retaining `SKIP_DB_INIT=true`.
Afterwards prove exact delivery task/container state, authenticated readiness204
and unauthenticated rejection401, plus an actual request through the gateway
handler if an existing scoped runtime mechanism can provide it. These are
authenticated requests, not HMAC-signed requests. Do not infer success from the
inactive provider endpoint before activation.

Failure after a pending record leaves that phase for explicit read-only
reconciliation and a separately reviewed forward plan. No automatic retry,
rollback, journal reset, history removal or stale-lock takeover exists.

## Current blockers

Delivery main is not protected; do not weaken the gate or change settings without
owner authorization. The dedicated staging key is absent; no provisioning is
authorized by this draft. Cross-repository GHCR custody, durable journal storage,
real credential proof and all production callback bindings remain unimplemented.
No publication, dispatch or live mutation is authorized here.
