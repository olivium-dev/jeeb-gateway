# Contracts — Acquisition Status

Snapshot of how each upstream OpenAPI spec was obtained for the initial BFF
skeleton (T-migrate-gateway-shell). "Real" specs are byte-for-byte copies of
the upstream repo's committed `swagger.json` / `openapi.json`; "placeholder"
specs are minimal valid OpenAPI 3.0 documents with zero paths and a
`x-status: PLACEHOLDER` extension. NSwag will produce an empty client from a
placeholder, which is fine — the BFF skeleton still compiles, and the first
migration ticket per service will replace it via `scripts/regenerate-clients.sh`.

| Service              | File                                | Source                                                                                  | Status            |
|----------------------|-------------------------------------|-----------------------------------------------------------------------------------------|-------------------|
| auth-service         | `auth-service.openapi.json`         | none committed upstream; service uses .NET 5 Startup-based hosting with Swashbuckle     | placeholder       |
| chat-service         | `chat-service.openapi.json`         | committed at `olivium-services/chat-service/swagger.json` (50KB, complete)              | committed-real    |
| user-management      | `user-management.openapi.json`      | committed at `olivium-services/user-management/swagger.json` (19KB, complete)           | committed-real    |
| wallet-service       | `wallet-service.openapi.json`       | none committed upstream; .NET 8 service with Swashbuckle (`Program.cs` confirmed)        | placeholder       |
| matching             | `matching.openapi.json`             | FastAPI service; no spec committed (FastAPI generates it at runtime via `/openapi.json`) | placeholder       |
| notification-service | `notification-service.openapi.json` | FastAPI service; no spec committed                                                       | placeholder       |
| geolocation-service  | `geolocation-service.openapi.json`  | FastAPI service; no spec committed                                                       | placeholder       |
| push-notification    | `push-notification.openapi.json`    | FastAPI service; no spec committed                                                       | placeholder       |
| delivery-service     | `delivery-service.openapi.json`     | committed at `olivium-services/delivery-service/docs/swagger.json` (46KB, complete)     | committed-real    |

## Filling in the placeholders

For each placeholder service the first migration ticket should:

1. Boot the upstream service locally (or point at a dev/staging instance via
   the override env var in `scripts/regenerate-clients.sh`).
2. Run `./scripts/regenerate-clients.sh` — it fetches the live spec, overwrites
   the placeholder, and regenerates the typed client.
3. Commit both `contracts/<service>.openapi.json` AND
   `Services/Generated/<Service>Client.cs` in the same commit so the contract
   diff and code diff are reviewed together.

## Why not start every service locally now?

The brief for T-migrate-gateway-shell explicitly says: "Do NOT start services
to fetch live specs — that's brittle and risky here." We respect that. The
placeholders make the BFF shell compile and run; live specs land per-service
in their migration tickets.
