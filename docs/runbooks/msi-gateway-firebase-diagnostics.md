# MSI gateway Firebase diagnostics activation

This is a one-purpose, manual development/MSI activation. It builds the exact
protected `main` commit, stages that non-secret runtime under
`/opt/jeeb-gateway-releases/firebase-diagnostics/jeeb-gateway-<full-sha>-linux-x64`,
and restarts only `jeeb-gateway.service`. The helper holds the shared
`/run/jeeb-msi-service-deploy.lock` across each operation. Before candidate
startup it copies the verified predecessor runtime and environment into a
temporary root-owned directory, verifies it, and atomically publishes the
read-only rollback snapshot.

The host administrator installs the reviewed helper once from the merged commit:

```sh
/usr/bin/sudo /usr/bin/install -o root -g root -m 0755 \
  scripts/jeeb-msi-gateway-firebase-diagnostics-admin.py \
  /usr/local/sbin/jeeb-msi-gateway-firebase-diagnostics-admin
```

The corresponding sudoers policy permits only these three argument-free commands
for `msi-access`:

```text
/usr/local/sbin/jeeb-msi-gateway-firebase-diagnostics-admin preflight
/usr/local/sbin/jeeb-msi-gateway-firebase-diagnostics-admin stage
/usr/local/sbin/jeeb-msi-gateway-firebase-diagnostics-admin activate
```

Run **Activate MSI gateway Firebase diagnostics** manually from protected `main`
after setting a fresh development environment secret named
`JEEB_DEVELOPMENT_FIREBASE_DIAGNOSTIC_PROBE_JSON`. Its exact JSON shape is
`{"idToken":"<fresh token>","expectedSubject":"<Firebase UID>","expectedProvider":"<provider>"}`.
Keep this short-lived value only in the protected
`development-msi-gateway-signing` environment, restricted to protected branches;
the workflow additionally requires exact protected `main`,
with no repository, staging, or production shadow.
The workflow streams it only to the root helper. The helper verifies success on
both public route twins, verifies a corrupt token is reduced to the generic 401
body, and never emits the token, UID, request body, response body, environment,
or command stderr.

The protected `development-msi-gateway-signing` environment also stores the artifact signing private
key as `JEEB_MSI_GATEWAY_ARTIFACT_SIGNING_KEY_PEM`. Keep this secret scoped only
to that protected environment; do not create repository, `development`, staging, or
production shadows. The helper pins its RSA
public key (DER SHA-256
`0d88071bfe63915f1c428cc8899b6b56bb78eb8b9bb01fcf61a7910339193247`)
and rejects every archive without a valid signature. This prevents the narrow
`msi-access` sudo command from staging arbitrary gateway code outside the
protected, current-`main` workflow.

Preflight first verifies the exact unit, eight drop-ins, environment file, full
409-file predecessor tree, metadata, hashes, and absence of ACL/xattr state. It
then issues a root-owned ten-minute one-time nonce bound to those predecessor
identities. The workflow signs the nonce, binding, and complete archive. Stage
consumes the nonce only after it has published the signed candidate receipt.
Preflight also reconciles an already-staged or already-active candidate by full
commit and archive hash, so an SSH disconnect after a completed host operation
can be retried safely.

Activation stops only `jeeb-gateway.service`, repeats the complete predecessor
verification while UID 1001 can no longer change the running runtime, creates
and verifies the protected snapshot, and keeps the unit stopped until the
candidate is ready to start. The candidate itself reads the protected snapshot
of `gateway.env`. Automatic rollback stops the candidate, atomically changes
only the dedicated drop-in to the protected runtime/environment snapshot,
reloads systemd, proves that exact stopped rollback identity, and only then
restarts it with diagnostics explicitly disabled.
Preflight recognizes this verified rollback state, so a later protected signed
run can retry without a broader administrator command.

The activation keeps `ASPNETCORE_ENVIRONMENT=Production` for the MSI gateway's
hardened runtime behavior. Its dedicated systemd drop-in sets only:

```text
Auth__FirebaseTokenDiagnostics__Enabled=true
Auth__FirebaseTokenDiagnostics__Environment=development
Auth__FirebaseTokenDiagnostics__ProjectId=jeeb-development-msi
```

There is no cross-repository dispatch, schedule, production path, or
user-management service operation in this procedure.
