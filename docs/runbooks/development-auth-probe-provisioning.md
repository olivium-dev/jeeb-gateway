# Development Auth probe provisioning

This new operator-local tool prepares the short-lived probe needed by the MSI
gateway diagnostics activation. It creates exactly one fresh reserved non-routable
Firebase Auth fixture and imports one probe into the existing protected signing
environment. It does not log in, grant IAM, enable a provider, register a Jeeb user,
send an email, discover an existing user, change a service, or dispatch a workflow.

The old scratchpad provisioner is unavailable. This implementation has its own
review and source history; it is not a recovery of the old reviewed bytes.

## Gates before execution

1. The operator is explicitly authorized to create one fresh non-routable fixture,
   retain its Auth record through acceptance, and use the current ROW-20 window.
   The command flags acknowledge that authorization; they do not prove that
   ROW-20 has issued GO. An explicitly authorized agent may operate locally,
   subject to the same independent live gates.
2. The script must be merged, reviewed, and run from clean current protected
   gateway `main`. A branch, draft PR, copied script or green test suite cannot
   pass its source gate. The protected minter remains pinned by exact bytes and
   SHA-256 `e22ab00c5abe1c3ed017b60d38968e7c61bef76bc63680d303720aed6e53fc93`.
3. The existing gcloud identity must describe only `jeeb-development-msi` and pass
   target-scoped IAM checks for project/client reads, Auth configuration read and
   Auth user creation. `roles/firebaseauth.admin` includes these permissions;
   another existing administrator must grant access when it is absent. The tool
   does not self-bootstrap or use account count as access evidence.
4. Exactly one active registered Android app must match `app.jeeb.mobile.dev` in
   that project. The package derives from protected mobile source at
   `ecd1551d9921977280bc47a67ec59e9a3c3216ba`; the same source names development
   secret `JEEB_DEVELOPMENT_JEEB_MOBILE_ANDROID_GOOGLE_SERVICES_JSON_B64`.
   The tool reads only the matched project's app configuration and verifies its
   project number, project ID, app identity, package and unique API key. It does
   not read the GitHub mobile secret or pick a first app/key fallback.
5. Email/Password sign-in must already be enabled with password required.
   Disabled/unknown state stops before mutation. Any global provider-setting
   change requires its own narrow review and approval.
6. `development-msi-gateway-signing` must restrict deployment to protected
   branches, contain the signing key, and have no existing probe. The two SSH
   org secrets must demonstrably include this gateway in their selected scope;
   other selected-repository names are not emitted.
   The probe must have no repository or org shadow. The tool stops on incomplete
   or unavailable scope checks and rechecks probe absence immediately before
   writing. This is an exclusive operator window, not a compare-and-swap API:
   another writer must not modify the probe concurrently.
7. ROW-20 must first certify custody, installed helper/policy, live binding,
   health, predecessor, residue and the shared deployment lock, then open the
   serialized window. Minting is deferred until this window so the probe is fresh.

Current denied project access means the tool is not ready to execute, even when
its source review and offline tests pass. A missing/changed provider resource
must be investigated in the development scope; never recreate a project based
on an opaque permission/not-found error.

## Authorized invocation, after all gates

Run from the clean protected gateway worktree on the operator's Mac:

```sh
/usr/bin/python3 -I -B scripts/provision-development-auth-probe.py \
  --execute \
  --approve-new-nonroutable-fixture \
  --accept-retained-fixture \
  --acknowledge-row20-window
```

Do not place an address, UID, API key, OAuth token or password in this command.
The tool accepts no credential arguments. All four flags are mandatory and no
flag is inferred from a prior failed run.

The new fixture uses an independently generated random UID, password and reserved
`.invalid` address. Administrative creation explicitly fixes the target project;
it never falls back to API-key signup or reuses an existing account. Exactly one
create and one protected password sign-in are allowed. The minter consumes an
immediately unlinked owner-only 0600 regular descriptor, while its executable
source is the captured hash-verified protected blob. A later local source-file
replacement cannot alter that invocation.

The minter's output and evidence are validated before the one secret write.
A rejected/empty mint cannot race a `gh secret set` into storing an empty probe.
Expiry is checked again immediately before the write. Success means the intended
metadata was read back, not that GitHub disclosed or verified the secret value.
No workflow is dispatched by this tool.

## Interruption, retained state and cleanup

Before any user creation, sign-in or secret write, the tool exclusively creates
and fsyncs `~/.jeeb-row03-auth-probe/attempt.json`, with a nonsecret opaque attempt
handle, protected source SHA and timestamp. It fsyncs the private directory and,
when new, that directory's parent. The marker contains no UID, address, token,
password, key or provider response. It persists after every outcome, including
success. A restarted tool refuses to recreate/remint even if later metadata reads
look empty. Do not delete/reset this marker to retry an ambiguous result.

An independent random opaque handle also appears in the newly created fixture's
display name. It supports **owner manual reconciliation only**. Auth account
lookup does not provide a display-name selector, and this tool does not promise
an automated lookup/deletion operation or enumerate unrelated users. No UID is
printed or reversibly encoded into the handle. The fixture record is retained;
its later cleanup needs explicit bounded review. Secrets and private input files
are not retained as handoff artifacts. Mutable buffers are cleared best-effort;
Python cannot guarantee zeroization or secure disk erasure.

After probe import, carry out the separately reviewed gateway activation only
inside ROW-20's window and inspect its actual completion/readback. Remove the
short-lived probe environment secret after the attempt, regardless of result,
using the existing protected runbook. Keep the marker until the fixture, probe
and any ambiguous attempt state have been manually reconciled. Do not blindly
remint, recreate, resend, overwrite an unowned secret, or roll back another lane.

## Offline validation

```sh
python3 -I -B scripts/test_provision_development_auth_probe.py
python3 -I -B scripts/test_mint_development_firebase_diagnostic_probe.py
```

The 41 new tests use synthetic data, fake cloud/credential commands, blocked
network access, and bounded local child processes. They cover failing gates,
redaction, source pinning, project/config ambiguity, private descriptor custody,
restart interlocks, no-retry behavior, mutation ordering and subprocess limits.
The existing 10 minter tests remain unchanged. CI runs both suites without
provisioning credentials.

Reference contracts: [Firebase Android app listing](https://firebase.google.com/docs/reference/firebase-management/rest/v1beta1/projects.androidApps/list),
[app config retrieval](https://firebase.google.com/docs/reference/firebase-management/rest/v1beta1/projects.androidApps/getConfig),
[Auth administrative creation](https://docs.cloud.google.com/identity-platform/docs/reference/rest/v1/accounts/signUp).
