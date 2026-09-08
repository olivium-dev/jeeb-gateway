# Staging paired delivery authentication

This is a source contract, not a deployment receipt. The fixed workflow is
`.github/workflows/jeeb-staging-paired-activation.yml`. Registration does not imply
caller compatibility review, secret provisioning or activation has occurred.

## Execution contract

1. Gateway `prepare` runs on the exact reviewed, current protected main after its
   hosted audit. It builds once and uploads a nonsecret, digest-bound build receipt.
2. Delivery's own protected preparation workflow builds and pulls its candidate,
   compares protected database identity, runs the read-only schema inspector, and
   records a 900-second receipt plus the exact verified inspector bytes. Its receipt
   binds source/tree/image/local image ID, service ID/version, network ID/version,
   daemon identity, SSH UID/home and run/attempt. No database URL or credential hash
   is present. Root independently reviews that exact delivery identity.
3. Gateway `activate` verifies its own prepare run, artifact digest, source/tree and
   image receipt using only its own `GITHUB_TOKEN`. It does not rebuild. It pulls
   only its own image using its current repository credential. Delivery's image
   must already be present on the exact target daemon, with matching labels and
   digest; there is no cross-repository credential or fallback pull.
4. A strict, nontruncating `flock` holder uses the same canonical host lock inode
   as both normal deployment routes. A stale owner, symlink, unsafe owner/mode or
   conflicting holder is an error. The runner keeps that SSH holder alive through
   both forward transactions and all proofs.
5. The target is only `olivium-ephemerals`, manager address `192.168.2.20`, API1.52,
   and the exact observed daemon/node identity. Both incumbent Specs must be
   stable single-replica services on the existing encrypted attachable overlay,
   with expected ports and `FailureAction=pause`. Delivery must already declare
   `SKIP_DB_INIT=true`, as required by the protected publisher's inspector.
6. If needed, provision only `jeeb-staging-delivery-service-auth-v1`, labeled
   `jeeb.environment=staging`, `jeeb.purpose=delivery-service-auth`, `jeeb.version=1`.
   An exclusive durable intent precedes one private cryptographic secret POST.
   A lost acknowledgement permits only a bounded read-only object reconciliation,
   never another create. Existing exact metadata can be reused; mounted-content
   format is proved after gateway rollout. No value, value hash or token file is
   produced outside Docker's secret storage.
7. Candidates derive from the captured complete Specs. The only changes are the
   exact images, delivery-auth environment/mount declarations and, if absent, an
   exact current `node.id` constraint. All other constraints remain; contradictory
   node/hostname/role constraints are refused. Database transport, unrelated
   credentials and the independent delivery-import bearer are retained.
8. An exclusive one-use receipt claim and durable phase history precede gateway
   CAS. Each role also burns an exclusive POST-attempt marker before its socket
   request. A rejected/lost acknowledgement or failed proof stops; there is no
   automatic retry, compensating update, migration or rollback.
9. Gateway goes first. Exact Spec/image/task/node/env/mount metadata and health
   are verified. A CLI intercepted before app/worker startup, running as65532,
   proves the real mounted credential loader. It binds to the actual allowlisted
   configured base `http://192.168.2.20:10055`, not a substitute overlay route.
10. Immediately before delivery CAS, the same receipt-bound delivery ID/version
    must still be unchanged. The exact staged inspector runs again on the local
    image against its unchanged database URL. Decoding that URL supplies parser
    arguments, not a new independent protected-credential comparison: custody
    comes from the publisher's protected comparison and unchanged version.
11. Delivery is activated with required authentication and read-only startup
    attestation. The gateway's actual handler must get204 from the fixed readiness
    path; missing, invalid and duplicate credentials must get401. Both services
    are finally re-inspected before recording `complete`.

## Persistent state and normal routes

`~/.jeeb-deploy/paired-releases/jeeb-staging-delivery-service-auth-v1/` contains six
append-only0400 phase files. They record only nonsecret source/tree/image/run,
service ID/version and secret ID metadata. Every mkdir/file creation is fsynced.
Claims and provisioning intent live beside it, outside per-run cleanup.

The shared normal-deployment retention helper rejects incomplete, unreadable,
malformed or inconsistent histories. Complete history requires both services to
retain the same exact credential declaration and immutable secret ID. Fully
stripped auth is not treated as a new pre-activation baseline. No cleanup removes
history or claims, and no stale-lock action clears uncertain activation state.

## Exact Engine evidence

Protected GET-only infrastructure run34199192160 observed Engine29.1.3,
GitCommit/package identity `29.1.3-0ubuntu3~24.04.2`, API1.44–1.52 and both
incumbents without mount/config/loader overrides. Both have no node constraints;
the narrowly allowed current-node pin is therefore required by this route.

The [official Ubuntu source package](https://packages.ubuntu.com/en/noble-updates/docker.io)
was read on MSI. Its `.dsc` SHA256 values match the downloaded original archive
`c141c822c60b4ee4f8caaba1b0676beaaa1e60b5a0421e36bac0245f72de6054`
and exact downstream patch archive
`bae2ae50512ead69e4cb9c17fa54a9277fcc4d0875569d24f9caff115295cdb6`.
The original executor returns success before registry access when the canonical
digest is locally present. None of its six downstream patches alter the executor
or Swarm router (only BuildKit vendor code and tini). This also matches the
[exact upstream release](https://github.com/moby/moby/blob/fbf3ed25f893e6ce21336f1101590e40a13934f4/daemon/cluster/executor/container/adapter.go).
The release router leaves `queryRegistry=false` for API1.52 service updates.
The runtime accepts only this exact observed release/package tuple, requires the
local digest/image identity before each POST and pins scheduling to that node.
This is the native successful no-pull path, not failed-pull/cache fallback.

## Explicit outstanding readiness checks

- Recheck the exact daemon/build, node and local cache identity at runtime;
  any different package build requires new source review, not a wider allowlist.
- Inventory external delivery callers and metrics scrapers. Eight gateway client
  chains and a successful gateway readiness call are not a complete external
  caller audit; provider-wide middleware may also affect carrier callbacks and
  metrics. No synthetic `all-callers-ready` flag is offered.
- Hosted source checks, owner review and real staging execution remain separate
  from synthetic MSI tests. The tests exercise fixtures, not live auth activation.

The runner checks gateway protected current main before each service submission.
It cannot independently query private delivery current main with a gateway token;
the reviewed delivery identity and short-lived one-use receipt are the explicit
cross-repository contract, not a claim of continuous delivery-head observation.
