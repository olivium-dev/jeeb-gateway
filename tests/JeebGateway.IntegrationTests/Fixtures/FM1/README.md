# FM-1 fixture provenance

`captured-msi-{karim,nour}-inbox-20260905-page.json` are allowlisted routing-only
projections of the recorded 2026-09-05 MSI notification-service receiver captures
(31/28 rows, receivers `106078a3-…` / `34a52972-…`). They preserve routing IDs,
types, status and timestamps; title/body, sender/receiver, nickname, media,
`_dispatch`, `_idempotency_fingerprint` and all other unlisted fields are omitted.
They are NOT byte-for-byte raw bodies or freshly captured post-deploy gateway proof.

The captures contradict P02's all-addressed claim: four historical Karim
`offer_accepted` rows (`4572e4e3`, `3b86aec9`, `34efe703`, `e4363780`) carry only
`offer_id`, with no request ID in the original top-level or payload data.
Their refs remain null and links stay at inbox root; the contract test pins this
exact set rather than inventing request IDs or hoisting offer IDs.

`captured-offer-received-page.json` and `captured-a5-duplicate-page.json` are
literal, byte-for-byte response bodies captured read-only on 2026-07-26 through
`docs/agents/scripts/msi.sh` from notification-service receivers
`FM1-PROBE-b02-20260726` and `FM1-PROBE-A5A6-b02`.

Files prefixed `constructed-` are deliberately and honestly labelled test
constructions. The live notification-service schema requires a typed
`jeeb.offer_received` payload and does not persist the top-level Jeeb routing
aliases, so the empty/absent/null/array/husk and alias-precedence shapes cannot
be obtained from the existing read-only probe rows. They are committed as
literal JSON files so tests cross the serialization boundary without runtime
string replacement; they are not claimed as captured service evidence.
