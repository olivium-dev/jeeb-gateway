# Jeeb KYC form flavor (additive)

Files added by this PR — none of the existing template files, code, or routes are touched.

| File | Purpose |
| --- | --- |
| `jeeb_kyc_form_builder.json`               | Top-level 5-step KYC template |
| `flavors/jeeb_kyc_form_builder/jeeber.json`| Jeeber-role flavor with tier eligibility map |
| `flavors/jeeb_kyc_form_builder/client.json`| Client-role flavor (empty, KYC skipped for Clients) |
| `JEEB_KYC.md`                              | This doc |

## How to enable

Set the existing env var:

```bash
TEMPLATE_JSON_FILES=jeeb_kyc_form_builder.json
```

The existing loader (`app/config.py:get_template_files`) and flavor discovery (`app/main.py`) will pick up the new template and flavors automatically. No Python changes required.

## Why this is non-breaking

- The default `TEMPLATE_JSON_FILES` is unchanged — existing deployments continue to load `form_builder_template.json` + `partner_form_builder.json`.
- The new files live in a dedicated directory (`flavors/jeeb_kyc_form_builder/`) under the existing flavor convention.
- No component-config additions are needed: every component (`Image Upload`, `Single Selection`, `Text Input`, `Liveness Capture`) follows the same shape as `components_config.json`.

## Downstream

Approved KYC submissions toggle the `kyc_approved` flag in `auth-service`, which is what gates the `PATCH /api/jeeb/users/me/role` endpoint (see auth-service PR #1).

Maps to FR-2.1, FR-2.2, BR-12, NFR-4.3.
