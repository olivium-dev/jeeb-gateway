# QA-PRE bundle - T-BE-004 / JEB-40 / JEB-503

Pre-implementation test pack for the Jeeb KYC schema registration in
`form-builder-service`. Authored by **Principal QA - Python** ahead of ENG-1
(JEB-507) so the engineer ships against a red-bar contract.

## Contents

| Path | Purpose |
|------|---------|
| `../../tests/jeeb/test_t_be_004_kyc_schema.py` | pytest scenarios covering AC1, AC2, AC4, AC6, AC7, AC8. |
| `i18n-key-check.sh` | Bash + jq CI gate for AC7 (no literal copy; ARB-key regex). |
| `AC-TRACEABILITY.md` | AC <-> test mapping + variant matrix + handoff list. |

## Spec authority

LEAD comment **14763** on JEB-40 (audit applied at comment **14770**). All test
assertions trace back to that pin; the older `.NET / EF Core / DB seed` wording
in the original Story description has been corrected (this is a Python/FastAPI
data-only registration).

## Expected initial state

All tests FAIL until ENG-1 ships `flavors/jeeb_jeeber_v1/{national_id,passport,
residency}.json` and the matching ARB keys in
`apps/jeeber-app/lib/l10n/intl_{ar,en}.arb`. That is correct.

## Handoffs

- Live HTTP 200 + cacheVersion log check -> QA-POST **JEB-505**.
- Schemathesis fuzz over the form-builder OpenAPI -> QA-POST **JEB-505**.
- Mobile-side `arb_completeness_check.dart` -> **JEB-17 / T-MOB-013**.
- ToS template `jeeb_tos_v1` registration (AC8 dependency) -> **T-BE-005**.
