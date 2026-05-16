# JeebGateway Downstream Service Clients

This folder hosts the **typed HTTP clients** the gateway uses to call upstream
services. There are two kinds:

| Kind | Folder | Source | Edit by hand? |
|---|---|---|---|
| **Generated** | `Services/Generated/` | NSwag (`scripts/regenerate-clients.sh`) | NEVER |
| **Wrapper / hand-written** | `Services/` (this folder) | Hand-written adapters | Yes |

## Generated clients (`Services/Generated/`)

Each `<Service>Client.cs` is **machine output** from
[NSwag 14.x](https://github.com/RicoSuter/NSwag) based on the pinned OpenAPI
spec at `contracts/<service>.openapi.json`. Files in this folder are marked
with NSwag's auto-generated header — DO NOT modify them by hand. Any change
will be lost the next time `regenerate-clients.sh` runs.

If a generated client is missing a feature you need (custom auth header, request
transform, etc.), extend it via:

1. A `partial class` in this folder (sibling, not inside `Generated/`), or
2. A wrapper class that composes the generated client (preferred for cross-cutting
   policies like retry/circuit-breaker — but resilience belongs in the
   `IHttpClientBuilder` pipeline, not in a wrapper).

## Wrapper clients (`Services/`)

Hand-written services that:

- Adapt a generated NSwag client into a stable domain-shaped interface so
  controllers depend on a Jeeb-owned contract, not the upstream wire shape.
- Aggregate multiple upstream calls (the BFF "fan-out/fan-in" pattern).
- Wrap a non-OpenAPI third-party API (e.g. Firebase, Twilio).

Wrappers live directly under `Services/`. Name them `<DomainConcept>Service.cs`
or `<UpstreamName>Adapter.cs` — never `<UpstreamName>Client.cs` (reserved for
generated clients).

## Registering a client

All HTTP clients are registered in
[`Extensions/ServiceClientExtensions.AddDownstreamClients`](../Extensions/ServiceClientExtensions.cs).
That extension applies the org-standard resilience pipeline
(`Microsoft.Extensions.Http.Resilience` — retry + circuit breaker + timeout).
Never call `AddHttpClient<>()` directly in `Program.cs`.

## Why generated clients are committed

See [`contracts/README.md`](../contracts/README.md). Short version:
contract drift must be reviewable; CI must be hermetic; offline builds must
work.
