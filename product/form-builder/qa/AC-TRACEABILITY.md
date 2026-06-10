# QA-PRE - T-BE-004 (JEB-40 / JEB-503) - AC <-> Test Traceability

**Spec authority**: LEAD comment 14763 on JEB-40 (audit applied at comment 14770).
**Stack**: Python / FastAPI form-builder-service. Data-only registration.
**Branch (per LEAD)**: `feat/t-be-004-jeeb-kyc-schema` (scope: `jeeb-kyc-schema-data`).

## Status

All scenarios below are **pre-implementation**. They WILL FAIL until ENG-1 (JEB-507)
ships `flavors/jeeb_jeeber_v1/{national_id,passport,residency}.json` and the matching
ARB keys in `apps/jeeber-app/lib/l10n/intl_{ar,en}.arb`. That is the intended QA-PRE
behavior - the suite is the engineer's red-bar contract.

## Mapping

| AC | Test(s) | Notes |
|----|---------|-------|
| **AC1** Gherkin: `GET /v1/kyc/jeeb/form-schema` returns 200 | `test_ac1_ac6_template_id_and_version` | Asserts `template_id == "jeeb_jeeber_v1"`. End-to-end HTTP 200 against a running service is QA-POST (JEB-505). |
| **AC2** Gherkin: rahmah backward compat | `test_ac2_existing_flavors_still_parse` | Sanity check that the new flavor does not break parsing of any pre-existing flavor JSON. Full Schemathesis run is QA-POST. |
| **AC3** Contract: Schemathesis backward compat | (delegated to QA-POST JEB-505) | Needs a live service; QA-PRE only pins the schema shape. |
| **AC4** Observability: schema fetch logs `cacheVersion` | `test_ac1_ac6_template_id_and_version` (asserts `template_version` is declared so the log line has a non-empty value) | Log-line emission test owned by QA-POST. |
| **AC5** Gherkin: v2 transparent fetch | (re-author when `jeeb_jeeber_v2` lands) | Out of scope for v1 QA-PRE. |
| **AC6** Data: exact 11 fields with declared validators | `test_ac6_eleven_fields_exact`, `test_ac6_id_type_enum`, `test_ac6_id_number_pattern_per_variant`, `test_ac6_driver_license_expiry_min_date` | Frozen field set (`AC6_FIELDS`), enum, variant-specific `id_number` pattern, `driver_license_expiry` minDate. |
| **AC7** i18n / CI: ARB-key references only | `test_ac7_every_field_has_i18n_label_key`, `test_ac7_no_literal_copy_strings`, `test_ac7_arb_keys_resolve_in_ar_and_en`, `qa/t-be-004/i18n-key-check.sh` | Pytest covers all three slices locally and in CI. The bash gate is wired into post-merge CI per LEAD §3. ARB-key regex: `kyc.jeeb.v1.<field_id>.<slot>`. |
| **AC8** Cross-link: `tos_accepted_version` -> contract-signing-service | `test_ac8_tos_accepted_version_fk` | Accepts either LEAD `equals=jeeb_tos_v1` pin OR a structured `{source: jeeb_tos_v1, kind: foreign-key}` shape. |

## Variant matrix (AC6 `id_type` enum)

Tests parametrize three variants per `flavors/jeeb_jeeber_v1/`:

| Variant       | `id_number` pattern (Q-OPEN-6 default) |
|---------------|-----------------------------------------|
| `national_id` | `^\d{12}$` (12-digit Lebanese ID)       |
| `passport`    | `^[A-Z0-9]{6,9}$`                       |
| `residency`   | `^[A-Z0-9]{6,12}$`                      |

If LEAD/PO pins different patterns, update `ID_NUMBER_PATTERNS` in
`tests/jeeb/test_t_be_004_kyc_schema.py` and re-link this table.

## Running

```bash
# from form-builder-service repo root
pytest tests/jeeb/test_t_be_004_kyc_schema.py -v
bash qa/t-be-004/i18n-key-check.sh
```

## Out of scope (handed off)

- Live `GET /templates/jeeb_jeeber_v1/schema` HTTP 200 + AR/EN cache hit/miss
  -> **QA-POST `JEB-505`**.
- Schemathesis fuzz over the full form-builder OpenAPI surface
  -> **QA-POST `JEB-505`**.
- `arb_completeness_check.dart` mobile-side gate
  -> owned by **`T-MOB-013` / `JEB-17`**.
- Liveness-proof token validation on selfie_with_liveness_url
  -> owned by **`T-MOB-013` / `JEB-17`**.
- `jeeb_tos_v1` template registration in `contract-signing-service`
  -> hard dependency on **`T-BE-005`** (AC8 sequencing rule).
