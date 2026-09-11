# One-use private chat migration

The protected staging workflow changes only gateway `ChatServiceApi__BaseUrl` from
`http://192.168.2.20:10028` to `http://jeeb-staging-chat-api:5176`, proves the exact
Healthy/Firestore-verified chat readiness row, then removes only chat's exact host
port publication. The binary and every other service Spec field remain unchanged.
Both services must already run on the same intended encrypted staging overlay.
The gateway may additionally have the exact inspected Docker ingress network;
arbitrary extra networks are rejected. Chat private verification requires no service,
Endpoint, HostConfig or actual container port publications and no host networking.

The initial chat binary is pinned by `scripts/staging-chat-private-build.json` to
reviewed source `ce68f35`, successful prepare run `34593792741`, its immutable
artifact/receipt hashes and exact image digest. The existing image-only update run
`34594010622` deployed that binary with identity disabled. The migration checks the
running immutable image, source/tree labels, process configuration, effective
environment, project and numeric image-user/signing-mount ownership without reading
credential contents or executing in containers. A fixed malformed JSON probe proves
the legacy endpoint is disabled before any service update; it contains no UID or token.

Run the separately reviewed current-delivery reconciliation first. Supply its exact
reviewed seal hash, whose workflow provenance must be verified by the local bundled
gateway reconciliation contract. Its initial service evidence must still match exactly
before the first gateway change. Subsequent retention checks preserve active Delivery
credential posture and the bytes of every original paired journal/claim. Historical
paired activation completion remains unproven; this route never edits that history.

The same canonical host lock is held throughout. New receipts live only under
`.jeeb-deploy/paired-releases/chat-private-migration-v1`, using exclusive 0400 phase
files in a private 0700 directory. They bind baseline/candidate Spec hashes and exact
nonsecret runtime identities. Every Engine POST is claimed durably before submission.
An existing or partially created directory, consumed claim, failed POST, cancellation
or uncertain result requires separate review: no automatic POST retry, reset, deletion
or implicit continuation exists. Read-only rollout polling does not resubmit updates.
Same-image updates explicitly retain incumbent registry authentication with
`registryAuthFrom=spec`; the workflow does not read new registry credentials.
Every readiness attempt first rechecks both expected service Specs, stable task/container
snapshots and baseline/lock retention. Alongside bounded transport failures, only the three
reviewed upstream startup failure descriptions (connection probe failure, three-second
budget, or upstream 503) are retried;
unknown or structural drift fails immediately. Both runtime proofs are refreshed again
before the completion receipt is appended.

`activate-identity` is deliberately rejected before SSH and again in the remote bundle.
The private migration receipt does not authorize identity signing. Actual nginx secret-map
structure, Caddy routes, active tunnel configuration and remaining ingress gaps require
separate positive proof and review before that operation can be implemented. A successful
read-only inventory with `isolation=unproven` must never be reinterpreted as approval.

## Failed-run read-only checkpoint

After a failed or uncertain migration, select `diagnose-private` in this same
protected workflow. Supply current reviewed main and the already approved public
baseline seal. This operation does not rerun reconciliation or migration. It reads
only the fixed migration journal and current prerequisites, reporting safe phase
metadata and fixed failure stages. Guard source locations refer to the reviewed
source; no raw exception, configuration, environment, private journal body, or
credential value is emitted.

The diagnostic must not create files, claim submissions, advance phases, or make
Engine updates. It uses a shared lock on the existing canonical lock file and
refuses an active or stale owner marker. Missing/invalid state is reported, not
repaired. A successful diagnostic means a report was collected, not that preflight
passed or that another migration attempt is authorized. The original migration
guards, one-use journal, and activation prohibition remain unchanged.
The diagnostic can exit `0` while individual `checks` fail or `snapshotStable`
is `false`; inspect those fields and the journal state. Every report includes its
exact reviewed `sourceCommit`, run, and attempt so guard line numbers are traceable.

Migration failures also retain exit `1` and the existing stop warning, with a
sanitized failure location and validated source/run/attempt identifiers. This
report never includes exception text or authorizes a retry; a service update may
already have taken effect even when its submission returned an error.

For interrupted run `34605416479`, attempt `1`, source
`a333ff9b96116aa4833bf0fdeec0a9720e041ce1`, the recorded gateway submission must
not be replayed. Read-only recorded-candidate reconciliation compares the fixed
prepared/submission prefix, approved baseline and build, recorded identities and
candidate hashes, gateway current/previous Specs, and unchanged original chat.
It proves only the current checkpoint, not the lost submission response or the
original rollout's completion. Even a matching, stable checkpoint grants no
continuation or identity-activation authority. A forward continuation requires
separate source review and live evidence; it must never reset or delete claims.
