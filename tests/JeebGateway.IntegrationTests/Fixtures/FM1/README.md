# FM-1 fixture provenance

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
