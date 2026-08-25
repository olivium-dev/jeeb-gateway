# Staging gateway A1/B rollout checklist

Last updated: 2026-08-25

Mode: **PREPARE ONLY — no live authority granted**

## Preparation

- [x] Exact clean PR worktree selected at `b3d27996e90f8bfeef6e156221d9d01cbee13b02`.
- [x] Existing owner block retained before checkout, build, registry, SSH, secret,
      Swarm, and staging mutations.
- [x] A1 and B configuration contracts written separately.
- [ ] Independent guardrail/security review approves the exact prepared diff.
- [ ] Owner removes or supersedes the first-step live block in a separate commit.

## Protected source and target

- [ ] Default branch and `github.ref_protected == true` proven by the workflow.
      Read-only inventory on 2026-08-25 reports `main protected=false`; the
      prepared workflow therefore fails closed.
- [ ] Protected GitHub environment is exactly `staging`. Read-only inventory
      reports a custom `main` branch policy, no reviewer rule, and
      `can_admins_bypass=true`; independent protection remains an owner task.
- [ ] Strict known-host pin matches the requested SSH host.
- [ ] Remote short hostname is exactly `olivium-ephemerals`.
- [ ] Remote global IPv4 inventory contains exactly `192.168.2.20` as the
      approved target identity.
- [ ] No `.50` reference and no UPG/payment route passes static gates.

## Topology handoff (must finish before A1)

- [ ] Read-only live Swarm inventory captured by the deploy owner.
- [ ] Nginx owner identifies the active config and approved temporary
      loopback-only overlay-DNS bridge.
- [ ] Temporary bridge uses an immutable image digest and exposes no LAN port.
- [ ] `nginx -t` passes; nginx is reloaded, never restarted.
- [ ] Canonical gateway port is converted to `10000:8080/ingress` by version CAS.
- [ ] Full Spec proves image/env/secrets/replicas/networks unchanged by conversion.
- [ ] Independent controlled-request evidence confirms the exact proxy source IP
      presented through ingress mode; `docker_gwbridge` IPAM alone is not proof.
- [ ] Public readiness and contract canaries pass before and after conversion.
- [ ] Temporary bridge removed only after canonical ingress verification.
- [ ] Backend owner targets are migrated to reviewed overlay DNS independently.

## A1 bootstrap

- [x] Chat OFF.
- [x] Realtime OFF.
- [x] Voice OFF.
- [x] OTP ON.
- [x] Super Login open mode OFF.
- [x] Demo users OFF.
- [x] Descriptor remains Staging-only and file-key-backed.
- [ ] Exact incumbent full Spec/ID/version/manifest captured privately.
- [ ] Incumbent and candidate images are immutable digests.
- [ ] Existing-service-only and one-replica assertions pass.
- [ ] Ingress/start-first/automatic-rollback preflight passes.
- [ ] Runtime image, readiness, A1 flags, public, descriptor, and final-Spec gates pass.

## Recovery adversarial coverage

- [x] Concurrent third-Spec race refuses recovery mutation.
- [x] Third Spec immediately before forward submission yields 409 and zero overwrite.
- [x] HTTP 200 and HTTP 409 accept only an exact candidate.
- [x] Lost-before-acceptance gets one exact-incumbent retry; lost-after-acceptance
      reconciles without a duplicate.
- [x] Candidate capture failure after acceptance invokes recovery and cannot
      silently leave an unverified candidate.
- [x] Readiness failure recovers and verifies the exact incumbent.
- [x] Canary failure recovers and verifies the exact incumbent.
- [x] Lost-response reconciliation is bounded and idempotent.
- [x] Incorrect rollback state/verification fails closed.
- [x] Step summary persists only sanitized Spec hashes and fixed-enum results;
      no full Spec, secret name, or secret value is persisted.

## B activation (separate blocked change)

- [x] B contract is separate from A1 and not exposed as an A1 input.
- [ ] Real non-Super-Login SMS canary passes.
- [ ] Real voice canary passes.
- [ ] Real Firebase/chat canary passes.
- [ ] Scoped public WSS canary passes.
- [ ] Real delivery and KYC device paths pass.
- [ ] Independent approval authorizes the B flag-only delta.
- [ ] Store release owner independently approves mobile distribution.
