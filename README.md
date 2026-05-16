# jeeb-gateway
Jeeb BFF gateway — C#/.NET 8, NSwag-generated clients, aggregates all downstream services.

## Endpoints

### Notification preferences (T-backend-031)

- `GET  /users/me/notification-preferences` — returns the caller's per-category toggles plus the list of always-on channels.
- `PATCH /users/me/notification-preferences` — partial update of `offers`, `chat`, `statusChanges`, `ratingReminders`. Attempting to disable `otp` or `systemCritical` returns `400`.

Caller identity is taken from the `sub`/`NameIdentifier` claim, with `X-User-Id` as an MVP fallback until JWT validation is wired up.

Backed by `InMemoryNotificationPreferencesStore` for the MVP. Production wiring will replace it with an NSwag-generated client for the notification-service preferences endpoints.

### Data export (T-backend-042, GDPR-like right of access)

- `POST /users/me/data-export` — body: `{ "format": "json" | "pdf" }` (default `json`). Queues a full export (profile, saved addresses, orders, ratings, chat history). Returns `202 Accepted` with `status: queued` and `dueBy = requestedAt + 72h`. Idempotent while a previous request is still open (queued / processing / ready).
- `GET  /users/me/data-export` — returns the caller's latest export record so the mobile app can poll until `status` flips to `ready` and `downloadUrl` is populated.
- `GET  /users/me/data-export/{token}/download` — single-use; the unguessable token is the credential, not the session. Returns the payload bytes (`application/json`); subsequent hits on the same token are `404`. Links expire after 7 days by default (configurable).

Backed by `InMemoryDataExportStore` + the `DataExportProcessor` hosted worker for the MVP. Notification fan-out (email + push) goes through `IDataExportNotifier`; production wiring will replace the in-memory implementations with Postgres-backed equivalents and an NSwag-generated notification-service client. The 72-hour SLA lives in `DataExportOptions.Sla`.

### Prohibited-item NLP scan (T-backend-048)

- `POST /prohibited-items/scan` — body: `{ "description": "...", "requestId": "optional" }`. Runs the active catalog through normalization → exact, synonym, and Damerau–Levenshtein fuzzy matching. Response contains the matches array, a `requiresReview` flag, and `autoBlocked: false` (always). When `requiresReview` is true the gateway records a `FlaggedRequest` row and returns its id; the caller MUST NOT auto-block on the response.
- `GET  /admin/prohibited-items/flagged?status=pending|cleared|upheld&page=&pageSize=` — admin queue.
- `GET  /admin/prohibited-items/flagged/{id}` — single flagged record.
- `POST /admin/prohibited-items/flagged/{id}/decision` — body: `{ "decision": "cleared" | "upheld", "note": "..." }`.

Fuzzy thresholds are length-tiered (min token length 4, distance 1 for ≤6 chars, 2 for longer); the review score floor is 0.78. The integration suite asserts a false-positive rate below 5% on a curated benign corpus.
