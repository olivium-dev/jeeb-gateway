# P01 submission snapshot contract

This local draft replaces the earlier multi-key completion-marker design. The public authenticated POST/GET submission routes and their response fields are unchanged. No deployment, live migration or mobile route activation is performed by this change.

## Atomic persistence

For each user and allow-listed template, the gateway writes exactly one nested preference, `jeeb.form.<templateName>`, through the existing `Data_SetNestedPreference` API. The RUP implementation replaces this entire dictionary in one SQL `INSERT ... ON CONFLICT ... UPDATE` (`remote-user-preferences/src/handlers.rs`, `set_nested_preference`). No RUP source or endpoint change is required.

The nested dictionary has exactly these four string values:

```json
{
  "schema_version": "1",
  "submitted_at": "2026-09-06T12:00:00.0000000+00:00",
  "zone_key": "",
  "answers": "{\"state\":\"Beirut\",\"country\":\"Lebanon\",\"address\":\"Home\",\"home_base\":\"{\\\"lat\\\":33.89,\\\"lng\\\":35.5}\"}"
}
```

`answers` is JSON encoding the schema-keyed string dictionary; object answers remain JSON strings inside it. Empty `zone_key` represents unchecked coverage. There are no reserved metadata fields inside the answer dictionary. Timestamp, zone and answers share one commit boundary. A read performs one nested-preference GET and validates the snapshot version, structure and values; the controller then validates reconstructed answers against both the current builder schema and complete template document.

Concurrent submits retain last-commit-wins upsert semantics. A response describes the complete snapshot accepted by that request; a later read may return another complete snapshot written concurrently. There is no claim of replay deduplication or start-time ordering. A transport timeout after the upstream commit can still return 504 although the complete snapshot was persisted; the caller may read back or retry the upsert. It cannot expose a mix of one writer's answers and another writer's metadata through this snapshot protocol.

## Compatibility and rollout coordination

The generic `UserPreferences/nested-preferences/jeeb.form.<template>` response now exposes this snapshot dictionary, rather than direct answer fields. Consumers inspecting the generic surface must decode `answers` and use the snapshot `submitted_at`. Separate `.submitted_at` and `.zone_key` preferences are no longer written or read and must not be used as onboarding completion markers by new consumers.

Legacy flat nested answers, unknown snapshot versions and corrupt snapshots fail explicitly with 502 on typed read. Missing nested preferences return 404. The gateway does not synthesize timestamps, read old marker keys as a fallback, or migrate legacy records automatically. A successful explicitly requested resubmission replaces the whole nested value with the new format. Before deployment, inspect the actual deployed RUP revision and existing user data, coordinate affected generic-surface consumers, and review any required migration separately. Do not run cleanup or migration from this draft.

## Form-definition and answer validation

The schema and template must describe the same complete set of component IDs, output types and required rules. Empty/incomplete documents, duplicate IDs/rules and contradictory required declarations are dependency failures. When a valid string component has no explicit `maxLength` rule, the documented 256-character validation bound applies; this is an input limit, not recovery from a failed template fetch. Stored answers undergo the same required, type, length and home-base validation as new submissions. Invalid persisted data is never a successful completed submission.

The real gateway integration project includes generated-client HTTP-boundary tests for single-key writes, overlapping independent store instances, pre-commit failure, commit-then-timeout, malformed/legacy snapshots and cancellation. Full-host endpoint tests cover invalid completed answers and incomplete definitions. These tests simulate the documented atomic upstream operation; deployed database behavior still requires the separate owner rollout verification.

## Published API artifact (G13)

`artifacts/openapi/jeeb-gateway.v1.json` is a curated contract document, not a raw dump of `scripts/export-openapi.sh`. It was created by the #364 essential-CMS carve-out and has since been advanced by narrow additive slices grafted onto the committed base — the pattern `scripts/update-wallet-openapi-slice.{sh,jq}` established for the partner-wallet paths. Two properties depend on that: the reviewed path/method baseline in `tests/JeebGateway.IntegrationTests/Contracts/jeeb-gateway.path-methods.baseline.txt`, and the `multipleOf: 0.01` minor-unit rule the slice injects into the four partner money-request schemas, which Swashbuckle does not emit. Overwriting the whole file with a raw export drops that rule and rewrites 171 unrelated paths.

P01 therefore follows the same pattern. `scripts/export-openapi.sh` produces the candidate; `scripts/update-form-submissions-openapi-slice.sh BASE CANDIDATE OUTPUT` copies exactly `/form-builder/templates/{templateName}/submit` and `/form-builder/templates/{templateName}/submission` plus their schema closure (`FormSubmissionResponse`, `CoverageDto`) onto the base. The result is additive only: two paths, two schemas, no removals, so `scripts/check-openapi-path-method-compatibility.sh` and all four `OpenApiCompatibilityContractTests` pass.

The candidate export also shows the committed artifact trails the served routes by the W6-02 unversioned compat twins and the tenant-parameterised realtime paths. Reconciling that reviewed baseline is a separate owner-gated contract decision and is out of scope here.
