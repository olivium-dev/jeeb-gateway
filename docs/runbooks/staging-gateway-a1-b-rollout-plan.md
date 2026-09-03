# Staging gateway A1/B rollout plan

Status: **PREPARE ONLY — LIVE MUTATION BLOCKED**

Approved local base: `5f3da8b2a2bffbfbc96243d80e21ca0f3a8df1e4`

Target: `olivium-ephemerals` with global IPv4 `192.168.2.20`

Service: `jeeb-staging-jeeb-gateway`

This plan is intentionally non-executable while the workflow's first step is the
owner block. It does not authorize a build, push, SSH session, secret mutation,
Swarm mutation, nginx change, Cloudflare change, or staging request.

## Safety invariants

1. Dispatch is accepted only from the repository default branch when GitHub
   reports that ref as protected, and only through the protected `staging`
   environment.
2. The remote target must prove both exact identities before credentials or
   Swarm state are touched: short hostname `olivium-ephemerals` and global IPv4
   `192.168.2.20`.
3. The image is the build-produced immutable digest. The deploy updates the
   existing `jeeb-staging-jeeb-gateway` service only; it never creates, deletes,
   or replaces that service.
4. A stable incumbent snapshot binds the full `Spec`, `Service.ID`,
   `Version.Index`, digest, ports, networks, replicas, and secret names.
5. Start-first is permitted only after preflight proves the canonical
   `10000:8080/ingress` routing-mesh topology. A host-mode port is a hard stop.
6. Swarm handles task/readiness failures with start-first plus pause. Failures
   in later public/canary gates invoke the reviewed forward-fix update bound to
   the candidate `Service.ID` and `Version.Index`. A third Spec wins: recovery
   refuses to overwrite it.
7. A1 and B are separate contracts. A1 cannot accept an activation input.
8. The candidate is a complete desired `Spec`, derived from and hash-bound to
   the captured incumbent. Recovery is armed before that Spec is submitted to
   Docker Engine with the incumbent `Service.ID` and `Version.Index`.
9. The exact `jeeb-staging-net` ID must resolve to an encrypted, attachable,
   non-ingress Swarm overlay. Gateway, OTP, and realtime services must each be
   attached to that ID, and their running tasks must resolve one another through
   Swarm DNS before the CAS. The candidate attaches only that exact network ID.
10. Candidate semantics are validated before CAS: both OTP configuration aliases
    use `http://jeeb-staging-one-time-password:8080`, b05 is exact, international
    phone eligibility is active (`AllowedRegion=LB` is retained only for the
    default-off emergency restriction), realtime uses the reviewed overlay
    endpoint, Voice remains off, and host-port OTP/realtime aliases are absent.

## Phase T — topology conversion handoff (dry run only)

Owner: Principal Swarm Deploy Engineer + Principal Nginx Ingress Engineer. This
phase must be independently reviewed and run in its own approved maintenance
change before A1 can be unblocked.

The intended sequence is:

1. Capture the current service identity, full Spec, digest, replicas, ports,
   networks, and secret names into a private `0700` change directory.
2. Add a reviewed temporary loopback-only ingress bridge for nginx. It must
   reach the gateway through `jeeb-staging-net` service DNS and must not publish
   a new LAN-reachable port.
3. Have the nginx owner point only the gateway upstream at the temporary bridge,
   run `nginx -t`, and use `nginx -s reload` (never restart). Prove public
   readiness and the existing contract canary.
4. Convert canonical service publication from
   `published=10000,target=8080,mode=host` to
   `published=10000,target=8080,mode=ingress` without changing image, env,
   secrets, replicas, or overlay attachments. Use the incumbent
   `Version.Index` as the mutation CAS and verify the complete post-change Spec.
5. Have the nginx owner restore its canonical upstream, run `nginx -t`, reload,
   and prove readiness/canaries again.
6. Independently confirm the source address that ingress-mode nginx requests
   present to the gateway. Use a controlled request plus edge/gateway evidence;
   do not infer it from the `docker_gwbridge` IPAM gateway alone. Record the
   confirmed single proxy IP and independent approver. If it differs from the
   prepared `ForwardedHeaders__KnownProxies__0` value, keep A1 blocked and amend
   the candidate contract before deployment.
7. Remove the temporary bridge only after a final exact topology and public
   verification. Preserve one replica; this single-node fleet is not HA.
8. Independently migrate owner backends to their reviewed `jeeb-staging-net`
   DNS names. This campaign requires OTP and realtime before A1; every other
   backend keeps its own review/readiness handoff. No `.50` address and no
   payment-gateway route is allowed.

No concrete bridge image or nginx path is guessed here. The nginx/registry
owners must supply the reviewed digest and active configuration path after a
read-only live inventory.

## Phase A1 — bootstrap contract

After Phase T and independent approval, the prepared workflow may update only
the existing digest-pinned gateway with:

| Control | A1 value |
|---|---|
| `FeatureFlags__UseUpstream__Chat` | `false` |
| `FeatureFlags__UseUpstream__Realtime` | `false` |
| `Features__RealtimeWebSocketProxy__Enabled` | `false` (route absent; no dial) |
| `FeatureFlags__UseUpstream__Voice` | `false` |
| `FeatureFlags__UseUpstream__Otp` | `true` |
| `Services__ServiceOTP__BaseUrl` | `http://jeeb-staging-one-time-password:8080` |
| `ServiceOTPApi__BaseUrl` | `http://jeeb-staging-one-time-password:8080` |
| `Auth__Otp__ApplicationId` | b05 GUID `0d51afe1-499f-4a29-a55a-36d2dd223b05` |
| OTP phone policy | international eligibility: `AllowedRegion=LB`, `EnforceRegion=false` |
| `Services__Realtime__BaseUrl` | `http://jeeb-staging-realtime-comunication-service:4000` |
| `SuperLogin__OpenMode` | `false` |
| `DemoUsers__Enabled` | `false` |
| staging realtime descriptor | enabled by Staging environment plus file-backed mint key |

Required gates, in order: pre-CAS encrypted-overlay membership and in-task DNS,
complete candidate semantic validation, immutable runtime image, readiness,
post-CAS overlay/DNS, exact A1 flags, public gateway contract, controlled
ingress/XFF source proof, then a final exact candidate Spec check. Credentials
stay memory-only and are never logged. Any external-gate failure leaves the run
failed even when exact-incumbent recovery succeeds.

The authenticated WSS gate — 101 upgrade, exact Phoenix topic join,
forged-ticket rejection, cross-topic rejection — is a **Phase B** gate and must
not run under A1. A1 pins `Features:RealtimeWebSocketProxy:Enabled=false`, so
`/socket/websocket` is unmapped and no image can return 101; an earlier revision
of this list contradicted the A1 flag table above and made the deploy
unsatisfiable. The deploy workflow now reads the flag from the submitted
candidate Spec and logs
`staging phase=authenticated-realtime result=skipped-proxy-inactive` under A1.

## Phase B — activation contract

B is a separate, still-blocked change. It must not be exposed as an input to A1.
Its reviewed configuration delta is limited to:

| Control | B value |
|---|---|
| `FeatureFlags__UseUpstream__Chat` | `true` |
| `FeatureFlags__UseUpstream__Realtime` | `true` |
| `Features__RealtimeWebSocketProxy__Enabled` | `true` (Staging-only exact-path proxy) |
| `FeatureFlags__UseUpstream__Voice` | `false` (campaign lock) |
| `FeatureFlags__UseUpstream__Otp` | `true` |
| `SuperLogin__OpenMode` | `false` |
| `DemoUsers__Enabled` | `false` |
| staging realtime descriptor | remains enabled and file-backed |

Until B lands, the activated branch of the WSS gate is **unreachable**, not
merely unused: `Features__RealtimeWebSocketProxy__Enabled=false` is pinned in
four places that must be edited together — `add_env` and `verify_bootstrap_flags`
in `jeeb-staging-deploy.yml`, `scripts/staging-gateway-candidate-contract.jq`, and
`scripts/check-staging-gateway-phase-contracts.sh` — so today the gate is retired
rather than conditional, and it lights up automatically once all four move.

B requires real, non-Super-Login SMS, Firebase/chat, scoped WSS, delivery, and
KYC canaries plus independent approval. Voice remains off throughout this
campaign and needs its own future activation decision. Store upload remains
outside this gateway runbook.

## Recovery command contract

The forward transaction submits the complete validated candidate Spec to Docker
Engine's service-update endpoint using the captured incumbent service ID and
version query parameter. Recovery is armed before submission. A private evidence
file is atomically set to `submitted-pending-reconciliation` immediately before
the CAS, then replaced by a fixed terminal result. In particular, an unknown
third Spec records `unknown-third-preserved`, while an unavailable authoritative
post-submit capture records `candidate-capture-failed-after-submit`. Forward
outcomes are reconciled as follows:

- HTTP 200: accept only an authoritative exact-candidate Spec.
- HTTP 409: accept only an exact candidate already established by another
  request; preserve and fail on any unknown third Spec.
- lost before acceptance: retry once only while the exact incumbent at the
  original version remains authoritative.
- lost after acceptance: accept the authoritative exact candidate without a
  duplicate request.
- candidate capture or validation failure after submission: fail visibly and
  run the armed recovery reconciler; it cannot silently leave an unverified
  candidate.

The prepared recovery does not invoke Docker's mutable rollback subcommand. It
sends the captured incumbent full Spec through the same Engine endpoint using
the exact observed candidate service ID and version. Recovery outcomes are:

- HTTP 200: require exact incumbent Spec and runtime verification.
- HTTP 409: re-read once; accept only an already-restored incumbent, otherwise
  stop without retry so a concurrent third Spec is never overwritten.
- lost response: re-read; accept an exact incumbent, retry once only when the
  service is still the exact same candidate at the same version, then reconcile.
- any unknown Spec, identity, or version: stop and preserve it for review.

Full Specs, secret names, and secret values remain only in private `0700`/`0600`
short-lived transaction files and are deleted at exit. The step summary receives
only incumbent/candidate SHA-256 hashes and fixed-enum forward/recovery results.

## Verification handoff

The Principal Deploy Verification Engineer receives: captured incumbent
manifest hash, candidate manifest hash, exact image proof, encrypted overlay ID,
service membership and in-task DNS result, readiness result, A1/B flag result,
public canary result, ingress/XFF source proof, authenticated WSS positive and
negative join results, final full-Spec comparison, and recovery result when armed.

## Open defect — `security-cutover`'s remote Version.Index verify

**`security-cutover` must not be used until this is reviewed.** Its remote proof
(`staging_gateway_security_cutover_observe`, `scripts/staging-gateway-security-cutover.sh`)
requires, of one service document read, **both**:

- `.Version.Index == $expected_version` — the index captured at CAS submit, and
- `.UpdateStatus.State == "completed"`.

Those look mutually exclusive. The write that sets `UpdateStatus.State` to
`completed` is itself a write to the service object, so it advances
`Version.Index` past the submit value; a document that satisfies the second
condition should therefore fail the first. This is the same Swarm behaviour that
made the terminal candidate check unsatisfiable in `normal` mode (run
33814328644), where the exact `Version.Index` compare was replaced by a
monotonic one. `security-cutover` deliberately keeps the exact compare in the
terminal check *because* of this remote proof, so the two must be re-reviewed
together — either the remote verify accepts a monotonic advance, or it must
capture the expected index after convergence rather than at submit.

`otp-cutover` has **no** remote CAS proof: it reaches the terminal check through
the ordinary `staging_gateway_forward_apply`, exactly like `normal`, so it uses
the monotonic rule.
