# ADR-0008 — the gwdbx CMS-config leg is SUPERSEDED by bundler-service

**Status**: Accepted, 2026-08-16 · **Program**: gateway-db-extraction (gwdbx) · **Items**: W3-11 (CMS half),
W3-07 (cms-versions leg), W3-10 (CMS authoring freeze), board item OA-16 · **Judged from**: gateway
`origin/main` @ `b0f0963` + the live MSI deployment.

## Ruling

The `cms_surfaces` + `cms_surface_versions` -> jeeb-state-service `/v1/config-surfaces` cutover is
**SUPERSEDED**, not blocked and not merely abandoned. The mandate it existed to satisfy — *the gateway
owns no CMS state* — is **already met by a different owner**: bundler-service. The plan's route (*the CMS
config lives in state-service*) is dropped.

Those are two different goals and only the first one is the program's. This ADR closes the first as DONE
and records the second as deliberately not pursued.

## Why (verified, not inferred)

1. **The gateway already owns no CMS row.** `ICmsSurfaceStore` is bound unconditionally to
   `BundlerCmsSurfaceStore` (`CmsServiceCollectionExtensions.AddCmsAuthoringPlane`), a stateless adapter
   over bundler-service's namespaced document API with "deliberately no in-process or gateway-Postgres
   fallback". `PostgresCmsSurfaceStore` is already gone from `src/`. The live gateway database
   (`jeeb_gateway` on `127.0.0.1:5442`) holds **0 rows in `cms_surfaces` and 0 in `cms_surface_versions`**
   (counted 2026-08-16).
2. **`CmsConfigMode` had zero functional consumers.** Its only references were the `IsKnown` validation,
   the `RequiresUpstream` boot guard and the importer's skip-if-already-serving check. No store,
   controller or client read it. Flipping it would have changed no behaviour at all while adding a hard
   boot dependency on state-service — the green-no-op cutover this program has already been bitten by.
3. **The import leg did not even move gateway state.** `StateServiceConfigImporter.ImportCmsAsync` read
   `ICmsSurfaceStore` — i.e. **bundler over HTTP** — and republished it into state-service. That produces
   two independently writable CMS catalogs with no reconciler, which the importer's own doc comment
   forbids for catalog legs.
4. **Its dependency is permanently gone.** bundler-service's database lived on `192.168.2.20`, which is
   decommissioned (directive A25) and on the never-touch list. Live probe 2026-08-16: `127.0.0.1:10056`
   refuses connections.
5. **Its source rows are gone.** The 5 `cms_surfaces` rows were destroyed by an owner-instructed truncate;
   see (1) — the tables are empty.

## Flag disposition — PINNED, not deleted

`FeatureFlags:CmsConfigMode` **stays in the registry and in `GwdbxMigrationOptions`, pinned to `local`**:
`Program.cs` now refuses any higher rung at boot (same shape as the `TiersMode` / `RequestsOwnerListMode`
ladder restrictions). Rationale for keeping the key rather than deleting it: a deleted key makes a stale
`FeatureFlags__CmsConfigMode=upstream-authority` in some future env file **silently inert**, whereas the
pin makes it **fail loudly** and name the ADR. Its `delete=W5` row is unchanged — it dies with the rest of
the program section at W5-14, not before.

For this domain `local` does **not** mean "gateway-owned". It means *this ladder never engaged*: the
domain was extracted before the ladder was written, by the bundler route.

## Consequences

- The CMS legs of `StateServiceConfigImporter` and `ConfigParityChecker` are **deleted** in the same PR.
  The W3-07 runner now covers lexicon + acks + flagged only.
- **W3-10** (freeze CMS + lexicon authoring before the import): the CMS half is void; the lexicon half is
  already imported and flipped, so W3-10 has nothing left to do.
- The `cms_surfaces` / `cms_surface_versions` DROP (W5-09) **no longer waits on any freeze-import**. Both
  tables are empty; the G-07 archive is a formality over zero rows.
- **`ConfigImportWorker` becomes retirable.** It was spared the hosted-service purge SOLELY because it
  armed this leg. With the CMS leg void, the lexicon leg imported and flipped, and parity structurally
  unable to report clean (the local stores are empty by design post-flip), nothing is left that only this
  worker can do. Retiring it takes the hosted-service ratchet **19 -> 18**. ~~NOT done in this PR~~ —
  **DONE at ADR-0010**, which deleted the worker, the importer and the parity checker together.

## What this ADR does NOT say

It does not say bundler-service is healthy. Two live, owner-gated config defects are unchanged by this
ruling and are NOT fixed here (config edits are out of scope for this PR):

- `BUNDLER_CMS_BASE_URL` is `http://192.168.2.39:10056/` in `gateway.env`, which **overrides** the
  `configimport.conf` drop-in's `http://127.0.0.1:10056/` (ExecStart sources the env file AFTER systemd
  `Environment=`). That host answers `200` with an empty body for **every** path through Caddy, so
  `/health/ready` reports `"bundler-service":"Healthy"` while bundler is down. Verified 2026-08-16.
- Consequently the CMS routes read a dead upstream that looks alive. `BUNDLER_CMS_NAMESPACE` is **not** a
  defect: it is committed as `jeeb.cms` in `appsettings.json` and `appsettings.Production.json`, so the
  adapter constructs fine even though the env file does not set it. Re-verified 2026-08-16 against the
  DEPLOYED `/home/ec2-user/iter5-native/publish/gateway/appsettings*.json`: both carry `jeeb.cms`.
  The base-URL half is half-fixed at ADR-0010 — the probe can now fail, but the env-file line still wins.

## Guardrails checked

Gateway stays stateless (no store added, one dependency removed); no hosted service added (and one
becomes retirable); no cross-service DSN read (the deleted leg was HTTP, and nothing replaces it); no
breaking change to a reusable service — bundler-service and state-service are untouched, and dropping
this leg also keeps Jeeb CMS vocabulary out of state-service's generic config primitive.
