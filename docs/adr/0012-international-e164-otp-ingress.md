# 0012 — International E.164 OTP ingress

- Status: Accepted
- Date: 2026-08-26
- Supersedes: ADR-0002 and ADR-0003 phone-admission clauses only

## Context

Jeeb OTP sign-in admitted only Lebanese numbers and parsed national formats
against an implicit `LB` default. That prevented international registration and
allowed presentation variants to produce different OTP, throttle, and identity
keys. The product requirement is all-country eligibility with Lebanon selected
by default in the client, not a server-side country allowlist.

## Decision

The gateway requires an explicit international `+` country code. It removes
harmless presentation separators, validates the exact digits with the pinned
libphonenumber implementation, and accepts the value only when formatting it as
E.164 returns the same digit sequence. National-format guessing, `00` prefix
repair, trunk-prefix repair, digit truncation, impossible numbers, and inputs
longer than 32 characters fail as `400 invalid_phone` before the OTP client.

Valid E.164 numbers from every country are eligible by default. The existing
`AllowedRegion` option remains `LB`, while `EnforceRegion` defaults to `false`.
Operators may set it to `true` only as an emergency fraud-containment switch;
that mode rejects other regions as `400 invalid_country`. The normal staging
and production deployment contract pins it to `false`.

The single canonical E.164 value is used for the per-phone limiter, OTP request
and verification, user-management identity lookup, and local session
projection. Verify keeps its generic `401 invalid_otp` response for invalid
phone input.

## Compatibility

The public request fields and success schemas do not change, but accepted input
semantics do: callers must send an explicit international prefix. The known
Jeeb mobile consumer already sends compact `+961…`; adding a country selector
with Lebanon preselected is separate client work. The legacy
`invalid_country` ProblemDetails type remains reserved for emergency-restriction
mode.

## Security and rollout

Rejected and throttled requests never call the OTP service. Fail-closed
user-management identity, role, suspension, and token-mint checks remain after
OTP validation. Merging this decision does not authorize deployment. Release
requires rotated provider credentials, international SMS spend/fraud controls,
and a protected real non-Lebanese request/verify canary with rollback evidence.
