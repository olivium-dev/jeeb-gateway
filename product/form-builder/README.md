# Jeeb product form-builder assets (relocated — JEB-1473)

These assets are **Jeeb-domain product data** that previously leaked into the
shared, reusable `form-builder-service` repo. Per Golden Rule 2 (no Jeeb-domain
code/data in reusable Olivium services) they now live here, in the Jeeb-owned
product repo (`jeeb-gateway`).

| Path | What | Was in form-builder-service at |
| --- | --- | --- |
| `flavors/jeeb_jeeber_v1/{national_id,passport,residency}.json` | AC6 Jeeb KYC field-schema flavors (incl. the `tos_accepted_version` → `jeeb_tos_v1` equals-rule) | `flavors/jeeb_jeeber_v1/` |
| `flavors/jeeb_kyc_form_builder/{jeeber,provider,client}.json` | Jeeb role flavors | `flavors/jeeb_kyc_form_builder/` |
| `l10n/intl_{ar,en}.arb` | Flutter app ARB keys for the KYC flavors (`kyc.jeeb.v1.*`) | `apps/jeeber-app/lib/l10n/` |
| `qa/i18n-key-check.sh` + `qa/*.md` | The AC7 i18n CI gate + traceability | `qa/t-be-004/` |
| `README-jeeb-kyc.md` | Jeeb KYC template doc | `JEEB_KYC.md` |

## What stays in the shared service

The shared `form-builder-service` keeps **only generic form logic**. The Jeeb
KYC template (`jeeb_kyc_form_builder.json`) is registered there purely as generic
**data** via the `TEMPLATE_JSON_FILES` env var — there is **zero `jeeb` token in
its `.py`**. The canonical alias (`jeeb_jeeber_v1`), the submission /
approval-unlock wiring, and the `jeeb_tos_v1` ToS binding are resolved **here in
the gateway** (`KycBffController`), consuming form-builder-service through the
typed `IFormBuilderServiceClient`.

## AC7 i18n gate

`qa/i18n-key-check.sh` walks `flavors/jeeb_*/*.json` and asserts every field
carries an `i18n_label_key` matching `kyc.jeeb.v1.<field_id>.<slot>` and that no
literal copy strings appear. Run it with:

```bash
bash product/form-builder/qa/i18n-key-check.sh
```

It is wired into CI as `.github/workflows/i18n-gate.yml`.
