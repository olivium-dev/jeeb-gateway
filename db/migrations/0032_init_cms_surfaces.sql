-- =====================================================================
-- Migration: 0032_init_cms_surfaces
-- Ticket:    JEBV4-132 (AUDIT-A IN-MEM-LIVE) durability follow-up
-- Purpose:   Durable backing store for the gateway-owned CMS authoring
--            plane (WS-01, W4/W7a). Replaces InMemoryCmsSurfaceStore
--            (Cms/InMemoryCmsSurfaceStore.cs), whose surfaces + draft +
--            published-version history lived only in gateway process
--            memory and were LOST on every restart / replica move — an
--            admin's draft edits and published config versions silently
--            reverted to the seeded v1 defaults on the next deploy or
--            crash. The MFE config envelopes those surfaces drive
--            (ofl-cms-orders/users/wallet/kyc-mfe) would flap back to
--            seed on any process bounce.
--
--            This is the gateway's OWN admin CMS catalog
--            (JeebGateway.Cms, surfaced by CmsAuthoringController under
--            /gateway/admin/v1/cms/*). Contract mirrors the domain model
--            (CmsModels.cs):
--              * a surface owns one MUTABLE draft config (PUT draft), and
--              * an append-only, monotonically-versioned PUBLISHED history
--                (each PUBLISH snapshots the current draft as a new version).
--            The config payload is an opaque JSON object (string keys →
--            arbitrary JSON values) the consuming MFE owns; stored as JSONB.
--
-- Notes:     Idempotent — CREATE TABLE/INDEX IF NOT EXISTS + the seed uses
--            ON CONFLICT DO NOTHING, so re-running is a no-op and never
--            stomps an admin's draft edit or a published version. The four
--            canonical W2 surfaces (ofl-cms-orders/users/wallet/kyc-mfe),
--            each with a published v1 envelope { surfaceId, enabled:true },
--            mirror InMemoryCmsSurfaceStore's constructor seed EXACTLY
--            (same surface ids, titles, v1 config, published_by='seed') so
--            first-run behaviour is byte-for-byte the same as the in-memory
--            seed. Because the seed only inserts once (ON CONFLICT DO
--            NOTHING), a draft an admin later edits or a version they later
--            publish survives across restarts — the durable, correct
--            behaviour the in-memory store could not offer.
--
--            Draft is nullable (no draft until the first upsert), matching
--            CmsSurface.Draft being null until the first PUT. PUBLISH does
--            NOT clear the draft (mirrors InMemoryCmsSurfaceStore.Publish,
--            which snapshots the draft but leaves it in place). Version
--            numbering is per-surface and monotonic, enforced by the
--            (surface_id, version) composite primary key.
-- Refs:      WS-01 (CMS authoring plane), FR CMS surfaces,
--            JEBV4-132 (IN-MEM-LIVE durability).
-- =====================================================================

BEGIN;

-- One row per CMS surface. The mutable draft config lives here (null until
-- the first draft upsert); the published history lives in
-- cms_surface_versions below.
CREATE TABLE IF NOT EXISTS cms_surfaces (
    surface_id  TEXT        PRIMARY KEY,
    title       TEXT        NOT NULL,
    draft       JSONB,       -- NULL until the first draft upsert (PUT draft)
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Append-only published history, one row per (surface, version). The row
-- with the highest version for a surface is its live published envelope.
CREATE TABLE IF NOT EXISTS cms_surface_versions (
    surface_id    TEXT        NOT NULL
                              REFERENCES cms_surfaces (surface_id) ON DELETE CASCADE,
    version       INT         NOT NULL,
    config        JSONB       NOT NULL,
    published_at  TIMESTAMPTZ NOT NULL,
    published_by  TEXT        NOT NULL,
    PRIMARY KEY (surface_id, version)
);

-- Primary read path: LoadSurface reads a surface's whole version history
-- ordered oldest → newest (ListVersions / diff / LatestPublished).
CREATE INDEX IF NOT EXISTS idx_cms_surface_versions_surface_version
    ON cms_surface_versions (surface_id, version);

-- ---------------------------------------------------------------------
-- Seed the four canonical W2 surfaces + their published v1 envelopes —
-- EXACT mirror of InMemoryCmsSurfaceStore's constructor seed. ON CONFLICT
-- DO NOTHING keeps this idempotent and never overwrites an admin's draft
-- edit or a later-published version.
-- ---------------------------------------------------------------------
INSERT INTO cms_surfaces (surface_id, title)
VALUES
    ('ofl-cms-orders-mfe', 'Orders MFE'),
    ('ofl-cms-users-mfe',  'Users MFE'),
    ('ofl-cms-wallet-mfe', 'Wallet MFE'),
    ('ofl-cms-kyc-mfe',    'KYC MFE')
ON CONFLICT (surface_id) DO NOTHING;

INSERT INTO cms_surface_versions (surface_id, version, config, published_at, published_by)
VALUES
    ('ofl-cms-orders-mfe', 1, '{"surfaceId":"ofl-cms-orders-mfe","enabled":true}'::jsonb, now(), 'seed'),
    ('ofl-cms-users-mfe',  1, '{"surfaceId":"ofl-cms-users-mfe","enabled":true}'::jsonb,  now(), 'seed'),
    ('ofl-cms-wallet-mfe', 1, '{"surfaceId":"ofl-cms-wallet-mfe","enabled":true}'::jsonb, now(), 'seed'),
    ('ofl-cms-kyc-mfe',    1, '{"surfaceId":"ofl-cms-kyc-mfe","enabled":true}'::jsonb,    now(), 'seed')
ON CONFLICT (surface_id, version) DO NOTHING;

INSERT INTO schema_migrations (version)
VALUES ('0032_init_cms_surfaces')
ON CONFLICT (version) DO NOTHING;

COMMIT;
