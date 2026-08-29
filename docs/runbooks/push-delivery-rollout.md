# Runbook: Push delivery rollout and rollback

**Scope:** notification-service, jeeb-gateway, push-notification, ephemeral runtime
**Severity:** critical when durable notifications are accepted but not relayed
**Team:** Jeeb service owners
**Architecture:** [ADR-0013](../adr/0013-notification-service-settled-push-producer.md)
**Last reviewed:** 2026-08-29

## 1. What is broken

The supported path is producer → durable notification-service → generic
push-notification relay → FCM. A red readiness gate means the single producer,
its durable store, the authenticated relay boundary, or Firebase ownership is
not ready. Do not compensate by enabling gateway direct send.

## 2. Pre-deployment verification

Before mutating any live service, verify all exact-head CI and contracts:

```bash
gh pr checks 61 --repo olivium-dev/notification-service
gh pr checks 548 --repo olivium-dev/jeeb-gateway
gh pr checks 31 --repo olivium-dev/push-notification
```

Healthy criteria:

- all required checks are successful at the frozen full commit SHAs;
- consumer and provider use the identical
  `notification-push-relay-v1.json` SHA-256;
- provider verification proves `X-Api-Key`, `Idempotency-Key`, body identity,
  201 success, and same-key replay without a second FCM call;
- candidate `/health/ready` is HTTP 200 before any live `service update`;
- image references are registry digests, never mutable tags;
- service updates specify `start-first`, parallelism 1, failure action `pause`,
  a health monitor, and Swarm ingress/VIP addressing.

If any criterion is false, stop before mutation. The incumbent remains serving.

## 3. Forward deployment order

Deploy exactly one service at a time:

1. **notification-service** — candidate readiness must prove MongoDB, API auth,
   worker, relay URL, and relay credential.
2. **jeeb-gateway** — readiness must prove durable handover and both file-backed
   credentials; the direct-send guard remains armed.
3. **push-notification** — provider contract must pass before strict relay
   activation; readiness must prove DB, Firebase project, and internal key.
4. **ephemeral runtime** — only after the service trio is compatible; its
   dedicated owner-bound Firebase credential remains a separate activation gate.

After each step, verify:

```bash
docker service inspect SERVICE --format '{{.Spec.TaskTemplate.ContainerSpec.Image}} {{.Spec.UpdateConfig.Order}} {{.Spec.UpdateConfig.FailureAction}}'
docker service ps SERVICE --no-trunc
curl -fsS --max-time 5 http://127.0.0.1:PUBLISHED_PORT/health/ready
```

Expected: digest image, `start-first pause`, desired replicas healthy, readiness
200. Do not proceed on `paused`, `rollback_*`, missing health, or mixed unknown images.

## 4. Failed replacement gate

When Swarm pauses an update or readiness is not 200:

```bash
docker service inspect SERVICE --format '{{json .UpdateStatus}}'
docker service ps SERVICE --no-trunc
docker service logs SERVICE --tail 200
```

Confirm the incumbent task remains desired/running and serving readiness. Keep
the failed replacement task and immutable secret/config objects for diagnosis.
Do not delete/recreate the service and do not force a second rollout over a
paused state.

## 5. Reverse compatibility rollback order

Rollback is an explicit compatible forward update to a selected prior digest,
not `docker service rollback`:

1. **push-notification relay first** — select a digest that accepts both the
   current authenticated contract and the older caller behavior.
2. **gateway and notification-service callers second** — update one at a time
   only after relay readiness is green.
3. **ephemeral runtime last**, if its selected contract references changed.

Use the same start-first/pause/readiness gates for every selected digest. Never
roll callers back before relaxing the relay; older callers may omit the key and
would otherwise be rejected.

## 6. Success criteria

- notification-service `/health/ready` and push-notification `/health/ready` are 200;
- gateway direct-send guard remains armed;
- a fresh notification is durably accepted and relayed once;
- replaying the same idempotency key returns the stored result and emits no
  second FCM send;
- no secret value appears in deployment commands, service logs, or diagnostics;
- service state remains stable for the update monitor window before proceeding.

## 7. Escalation

Stop and escalate to the Jeeb service owner when architecture ownership is
unclear, Firebase project identity differs from the environment contract,
readiness stays red after a compatible forward fix, or the incumbent no longer
serves during a paused update. Do not broaden relay access or restore gateway
direct push as an incident workaround.
