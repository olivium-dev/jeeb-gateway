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
- provider verification proves credential-derived identity, header spoofing
  denial, cross-scope denial, `Idempotency-Key`, body identity, 201 success, and
  same-key replay without a second FCM call;
- candidate `/health/ready` is HTTP 200 before any live `service update`;
- notification-service's non-delivering relay probe proves DNS, provider
  readiness, credential authentication, and `notification.user-delivery` scope
  before any live mutation;
- image references are registry digests, never mutable tags;
- service updates specify `start-first`, parallelism 1, failure action `pause`,
  a health monitor, and Swarm ingress/VIP addressing.
- all three file-backed credentials have distinct printed fingerprints without
  printing their values: retained legacy shared, notification, and gateway.

If any criterion is false, stop before mutation. The incumbent remains serving.

## 3. Merge/review order and deployment hold

The merge/review order is notification-service #61 → gateway #548 →
push-notification #31. Both caller workflows remain held after merge. Do not
deploy a PR image or activate either caller until the protected-main
push-notification image has been deployed and verified in `expand` mode.

## 4. Provider-first activation order

Activate exactly one compatible state at a time:

1. **push-notification expand** — provision three distinct immutable file-backed
   secrets, then deploy the protected-main digest start-first/pause with
   `PUSH_AUTH_MODE=expand`. Verify old callers still work; both new scoped
   readiness endpoints authenticate without sending; notification-key → gateway
   operations and gateway-key → delivery are 403; forged/swapped caller headers
   and missing/bad credentials fail. The legacy key is accepted only without a
   caller header on the pre-existing operation allowlist and is denied from new
   scoped readiness.
2. **notification-service** — candidate readiness must prove MongoDB, API auth,
   and worker. The separate non-delivering relay probe must prove relay DNS,
   provider readiness, caller identity, credential, and delivery scope. On MSI,
   keep the standalone `127.0.0.1:11026` incumbent running, start the Swarm
   replacement on ingress port `11027`, then atomically select it only after both
   probes pass. A failed stable-port probe removes the selection and leaves the
   standalone incumbent serving.
3. **jeeb-gateway** — readiness must prove registration/recovery with only its
   scoped key; notification delivery remains green and the permanent local
   direct-send guard remains armed.
4. **drain legacy, then push-notification strict** — require all old tasks
   drained and zero accepted legacy-key traffic for at least the maximum retry
   interval. Update the same reviewed provider digest start-first/pause with
   `PUSH_AUTH_MODE=strict`. Detach the legacy secret from the service but retain
   the immutable secret object for the rollback window. Re-run both positive
   E2Es, scoped readiness, spoofing/cross-denial, and receiver-side delivery.
5. **ephemeral runtime** — only after the service trio is compatible; its
   dedicated owner-bound Firebase credential remains a separate activation gate.

After each step, verify:

```bash
docker service inspect SERVICE --format '{{.Spec.TaskTemplate.ContainerSpec.Image}} {{.Spec.UpdateConfig.Order}} {{.Spec.UpdateConfig.FailureAction}}'
docker service ps SERVICE --no-trunc
curl -fsS --max-time 5 http://127.0.0.1:PUBLISHED_PORT/health/ready
```

Expected: digest image, `start-first pause`, desired replicas healthy, readiness
200. Do not proceed on `paused`, `rollback_*`, missing health, or mixed unknown images.

## 5. Failed replacement gate

When Swarm pauses an update or readiness is not 200, use only allowlisted
non-secret formats:

```bash
docker service inspect SERVICE --format 'service={{.Spec.Name}} image={{.Spec.TaskTemplate.ContainerSpec.Image}} update={{if .UpdateStatus}}{{.UpdateStatus.State}}{{else}}none{{end}}'
docker service ps SERVICE --no-trunc --format 'task={{.ID}} desired={{.DesiredState}} current={{.CurrentState}} error={{.Error}}'
```

Confirm the incumbent task remains desired/running and serving readiness. Keep
the failed replacement task and immutable secret/config objects for diagnosis.
Do not delete/recreate the service and do not force a second rollout over a
paused state. Do not print service environment or application logs from this
failure path; both may contain credentials or reflected upstream values.

## 6. Compatibility rollback

Rollback selects and applies immutable digests through new start-first/pause
updates. Before strict, move only the failing caller to its recorded prior
digest while the provider remains in expand. After strict, first apply a
provider update using the same reviewed digest in expand, then select prior
caller digests one at a time. Return to the original shared-only provider only
after every caller is confirmed back on the legacy credential. Ephemeral remains
last.

Use the same start-first/pause/readiness gates for every selected digest. Never
roll callers back before relaxing the relay; older callers may omit the key and
would otherwise be rejected.

## 7. Success criteria

- notification-service `/health/ready` and push-notification `/health/ready` are 200;
- gateway direct-send guard remains armed;
- notification and gateway credentials are distinct and each is cross-denied
  from the other's provider operations;
- a fresh notification is durably accepted and relayed once;
- replaying the same idempotency key returns the stored result and emits no
  second FCM send;
- no secret value appears in deployment commands, service logs, or diagnostics;
- service state remains stable for the update monitor window before proceeding.
- strict activation has zero accepted legacy-key traffic for at least the
  maximum retry interval and the detached legacy secret remains available only
  for the bounded rollback window.

## 8. Escalation

Stop and escalate to the Jeeb service owner when architecture ownership is
unclear, Firebase project identity differs from the environment contract,
readiness stays red after a compatible forward fix, or the incumbent no longer
serves during a paused update. Do not broaden relay access or restore gateway
direct push as an incident workaround.
