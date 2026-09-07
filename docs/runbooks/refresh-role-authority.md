# Refresh role continuity

Runtime `ITokenService` requires `IRefreshRoleAuthority` through explicit DI
resolution. Ordinary UM sessions and bounded internal UM sessions read
`GET api/User/{userId}/roles` from user-management on every refresh. The existing
generated client and configured owner base URL are reused; this owner endpoint
has no access-bearer requirement. A refresh request's optional bearer is not used.

The reader validates the returned identity against the refresh record, requires
nonempty explicit roles and a matching active role, then reads ban-service through
the existing suspension gate. No profile is created, cached, seeded, repaired or
granted a default role. A missing identity, malformed role context, revoked role,
changed active role, suspension or unavailable owner refuses the refresh before
rotation. Transport timeout is bounded to five seconds. Failure does not consume
the refresh token, so a transient owner outage can recover using the same token.

The authority returns distinct valid, invalid and unavailable outcomes. Confirmed
missing identities, revoked roles and suspensions remain 401. Transport faults,
timeouts, unexpected owner statuses and failed/malformed UM or ban reads return a
sanitized 503 on every public and admin refresh route; browser cookies are not
deleted. A wrong response identity, absent/null role fields, contradictory active
role, numeric role values or duplicate identity keys cannot confirm revocation.
An explicit empty role list does confirm that no grants remain and returns 401.
The admin resolver's secondary read uses the same authority/parser and retains
its portal-access restriction, including exact missing-identity 404 handling.
Client code must retain its refresh credential on 503 and retry later.
HTTP integration fixtures exercise every route with the real token service and
UM/ban parsers, including a successful retry of the unchanged credential.

The legacy `IUsersStore` projection remains separate because OTP sign-in and
unrelated profile consumers still write it. It is not runtime refresh authority.
The reader is a required constructor dependency as well as a required runtime
registration. Tests inject an explicit test-owned authority; there is no
snapshot or local-store fallback branch in the production refresh service.

Readiness probes the same UM roles route using the all-zero GUID and requires
the owner's exact 404 `user-not-found` ProblemDetails response, then checks the
ban-service read path. A generic 404, 401, timeout or unavailable suspension
source is Degraded. It does not enumerate users or manufacture a profile count.
Existing refresh census counters remain visible; readiness is not a census of
every durable family.

External OIDC operators belong to their provider, not UM. They retain the
existing complete verified provider tuple, role snapshot, MFA/authentication-time
and bounded deadline checks. The branch is selected from persisted external
fields, never from a subject prefix; malformed partial tuples fail closed.
This change does not perform fresh provider revocation/introspection during an
OIDC refresh and does not claim it does.

Source verification used user-management master
`fe30bc5a3e62a689443979f0e9a702ba5fdb69fb`. That owner currently normalizes null
stored role metadata to its own `client` default. The gateway validates the
explicit response but cannot distinguish that owner-side normalization from a
stored `client` role. Changing UM's persistence semantics is separate work.

Before release, confirm the deployed UM route/contract and ban-service access
from staging, then use an authorized synthetic account to validate refresh after
a gateway restart and a controlled role/suspension change. Unit fixtures model a
retained record crossing newly constructed service instances; they do not attest
an actual live database restart or customer-session acceptance.
