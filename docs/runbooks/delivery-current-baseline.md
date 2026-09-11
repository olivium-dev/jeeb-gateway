# Current delivery baseline: historical completion remains unproven

This protected staging operation accepts a freshly inspected current gateway and
delivery posture. It does **not** assert that the interrupted paired activation
completed successfully, append its missing phases, or authorize another activation
attempt. The original four phase files and three consumed claims remain untouched.

The `jeeb-staging-delivery-current-baseline.yml` workflow requires the designated
owner, current protected main, its successful authority audit, and the explicit
confirmation `reconcile-current-only-history-unproven`. It streams only reviewed
source and uses the existing shared host lock. Docker access is GET-only: no
service/network update, process execution, credential-content read or rotation.

## Evidence and meaning

The existing paired read-only inspector is executed afresh under the lock. The
reconciler requires exact current source/image/task/process/environment bindings,
the expected secret metadata, and canonical active credential declarations on
both roles. It independently rejects process overrides and shadow mounts. These
are Docker inspection checks, not a claim of end-to-end request authorization or
Firebase readiness; those live functional proofs remain separate requirements.

Under `.jeeb-deploy/paired-releases/delivery-current-baseline-v1/`, exclusive 0400
files in a private 0700 directory preserve the complete captured service Specs and
PreviousSpecs, private inspection evidence, and the original journal/claim bytes.
These snapshots can contain unrelated credentials: never print, upload, attach or
copy them into artifacts. Public output contains only immutable hashes and the
fixed authority fields and statements `historical_completion=unproven` and
`current_posture=verified`. The workflow retains only this small public JSON as
`delivery-current-baseline-public-seal-RUN-ATTEMPT`, never the private evidence.

A second stable inspection and historical-file check precedes the exclusive final
`sealed.json`. Any existing directory, missing seal, malformed file, unexpected
file, ownership/mode/link violation or inconsistent hash is a hard failure. There
is no re-entry or partial-record repair operation in this workflow.
Do not rerun this one-use operation, even after a failed or cancelled workflow.

## Consumption by a separately reviewed migration

`scripts/staging-delivery-current-baseline.py bundle reconcile` emits the standalone
reviewed Python helper plus its fixed audit/custody dependencies. Consumers must
pin the reviewed source and stream that bundle; no ambient remote helper is used.

Before either verification mode, run that bundle's `public-seal` command on the
host and pass its bounded JSON output to the runner-side
`scripts/staging-delivery-baseline-provenance.py`, with read-only GitHub Actions
access. This verifies the exact reconciliation run **attempt**, protected main
source, workflow, event, repository, both designated-owner identities and successful
completion. It then compares the host projection with the fixed single-file,
digest-verified immutable public artifact from that attempt. Only its output hash
is approved for the next step. Failed/in-progress runs, missing/expired artifacts,
duplicate artifacts or any hash substitution fail closed. Artifact retention is
90 days; expiry requires separately reviewed archival authority, not bypassing
this check or repeating reconciliation. A later rerun's outcome cannot change the
original attempt's identity, but rerunning reconciliation remains prohibited.

While holding the existing shared lock, invoke the bundle with:

```text
python3 -I - verify-baseline EXPECTED_SEAL_SHA256 SHARED_LOCK_OWNER
```

This first-CAS precondition binds the supplied public seal hash and requires exact
current service objects, including IDs, versions, Specs and PreviousSpecs. The
Python API is `verify_retention(home, lock_owner, exact=True, expected_seal=hash)`;
after success `load_seal(home)` returns `(seal, private_evidence)` for in-memory
candidate construction. Its return values must never be serialized to logs.

Ordinary subsequent retention uses
`verify-retention APPROVED_SEAL_SHA256 SHARED_LOCK_OWNER`. The standalone Bash
adapter is emitted with `bundle retention APPROVED_SEAL_SHA256`, binding that
approved immutable artifact hash into its executable source. It
requires the original journal and claims unchanged, the same daemon/node/service
identities, service versions no lower than the accepted baseline, identical secret
metadata and exact retained credentials on both roles. The record is durable
history, not an expiring runtime-health certificate. Separately authorized future
image/configuration changes are allowed; a same-version changed service object is
not. Every consultation performs fresh runtime inspection under the held lock.

The original complete-history path is unchanged. Only the exact four-phase prefix
can consult this alternative record. Other incomplete/corrupt histories still
fail. Gateway deployment embeds the reviewed standalone retention helper. Other
repositories carrying their own retention copy remain fail-closed until their
publisher explicitly supplies the same reviewed interface.
