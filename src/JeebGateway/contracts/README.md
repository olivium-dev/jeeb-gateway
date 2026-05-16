# JeebGateway Upstream Service Contracts

This folder pins **OpenAPI specifications** for every upstream service that
`jeeb-gateway` (a BFF — Backend-For-Frontend) aggregates. Each `<service>.openapi.json`
is the **source of truth** for the generated typed client in
`Services/Generated/<Service>Client.cs`.

## Why specs are committed, not auto-fetched in CI

1. **Reviewability** — a downstream service can ship a breaking change at any
   time. By pinning the spec, the diff appears in the regeneration PR (the one
   that runs `scripts/regenerate-clients.sh`), so a human reviewer sees exactly
   which endpoints/schemas changed before the gateway picks them up.
2. **Build reproducibility** — CI must produce a deterministic gateway binary.
   Pulling specs from a live `/swagger/v1/swagger.json` at build time makes the
   build depend on whether the upstream service is running at that moment.
3. **Offline development** — `dotnet build` works without VPN access to dev
   environments. Generated clients compile from the committed contracts.
4. **No silent drift** — if a developer regenerates clients locally without
   committing the spec, CI fails when the generated `*Client.cs` and the
   `*.openapi.json` are out of sync (verified by the `nswag-clients-up-to-date`
   CI job — see `scripts/regenerate-clients.sh` for the diff check pattern).

## Workflow

```bash
# 1. Regenerate (fetches latest specs + runs NSwag)
./scripts/regenerate-clients.sh

# 2. Inspect the diff: contracts/*.openapi.json AND Services/Generated/*Client.cs
git diff src/JeebGateway/contracts/ src/JeebGateway/Services/Generated/

# 3. Commit BOTH the spec and the regenerated client together
git add src/JeebGateway/contracts/<service>.openapi.json \
        src/JeebGateway/Services/Generated/<Service>Client.cs
git commit -m "chore(contracts): bump <service> openapi to <commit-sha>"
```

## Inventory

See [`SPECS-STATUS.md`](./SPECS-STATUS.md) for the current acquisition status
(committed-real vs placeholder) per upstream service.

## Override fetch URLs

`scripts/regenerate-clients.sh` reads per-service base URLs from environment
variables, defaulting to `localhost:<port>` from each service's
`Properties/launchSettings.json`. Example:

```bash
AUTH_SERVICE_OPENAPI_URL="https://auth-stg.jeeb.internal/swagger/v1/swagger.json" \
CHAT_SERVICE_OPENAPI_URL="https://chat-stg.jeeb.internal/swagger/v1/swagger.json" \
./scripts/regenerate-clients.sh
```
