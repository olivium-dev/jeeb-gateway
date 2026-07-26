# AC4/AC5 live round-trip fixture provenance

- **What it is:** The verbatim response body of
  `GET /messages/receiver/FM1-AC45-b02-20260726?page=1&page_size=10`.
- **Where from:** `notification-service` on MSI `192.168.2.39`, reached at
  `127.0.0.1:10026`.
- **When:** Captured 2026-07-26.
- **Deployed gateway SHA at capture time:** The deployed gateway was `d883dfd`.
  FM-1's branch was not deployed, so this fixture captures the service
  round-trip, not FM-1's gateway path.
- **How produced:** Two POSTs were made for receiver
  `FM1-AC45-b02-20260726`: `jeeb.offer_received` with `offer_amount` `12.5`,
  and `jeeb.offer_accepted` with `accepted_amount` `12.5`. Both used
  `pickup_location` = `Hamra, Beirut` and non-null `senderProfilePicture` and
  `nickname` per defect D-FM1-01. One GET then captured the response above.
- **Why these values:** `12.5` must come back as an unquoted JSON number to
  detect JEBV4-332 husking. `Hamra, Beirut` contains a comma and a space so
  delimiter-based corruption is visible rather than silent.
- **Byte faithfulness:** The captured response is unmodified: it was not
  reformatted, pretty-printed, or re-serialised.
- **Cross-reference:**
  `docs/batches/b02-20260726/validation/evidence/FM-1/AC4-AC5-live-roundtrip.md`.
