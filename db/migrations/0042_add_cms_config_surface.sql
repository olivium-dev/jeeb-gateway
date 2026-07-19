-- =====================================================================
-- Migration: 0042_add_cms_config_surface
-- Ticket:    JEBV4-315 (CMS config-authoring shell surface) — follow-up to
--            0032_init_cms_surfaces (JEBV4-132 durable CMS authoring plane).
--
-- Numbering:  0040/0041 were already taken by the partner-portal lane
--             (0040_init_partner_wallet_operations, 0041_init_partner_otp_
--             challenges) and are recorded in the jeeb_gateway schema_migrations
--             ledger, so this CMS follow-up takes the next free slot, 0042. Gaps
--             in the sequence are expected and harmless — apply.sh walks
--             db/migrations/*.sql lexicographically and every migration is
--             idempotent (this file is behind the partner ones on this branch).
-- Purpose:   Provision the FIFTH canonical CMS surface — the config-authoring
--            shell's OWN surface, 'ofl-cms-config-mfe' — into the gateway-owned
--            CMS authoring plane (cms_surfaces + cms_surface_versions, 0032).
--
--            The admin portal shell (ofc-cms-shell/src/config/ConfigRouteHost)
--            fetches the PUBLISHED config for SURFACE_ID 'ofl-cms-config-mfe'
--            on mount (defaultSurface pre-select, history page size). That
--            surface was never seeded by 0032 (which seeded only the four W2
--            domain surfaces: orders/users/wallet/kyc), so GET
--            /gateway/admin/v1/cms/config/ofl-cms-config-mfe/published returned
--            404 and the shell could not load its own config. This migration
--            makes that surface exist durably via the SAME mechanism as its four
--            siblings.
--
-- Notes:     Idempotent — the seed uses ON CONFLICT DO NOTHING, so re-running is
--            a no-op and never stomps an admin's later draft edit or a version
--            they publish (e.g. the schema-valid config the shell authors via
--            PUT draft + POST publish, which lands as v2). The surface + its
--            published v1 envelope { surfaceId, enabled:true } (published_by=
--            'seed') mirror InMemoryCmsSurfaceStore's constructor seed for this
--            surface EXACTLY (same id, title, v1 config, published_by), so the
--            in-memory (dev/CI/test) and Postgres (prod) paths stay byte-for-byte
--            identical — the invariant 0032 established for the four W2 surfaces.
--
--            The v1 envelope intentionally matches the sibling placeholder shape
--            rather than the shell's ConfigConfigSchema. That schema
--            (apps/ofl-cms-config-mfe/src/config/schema.ts) gives every field a
--            .default() and strips unknown keys, so the shell's deep-merge +
--            validate coerces this envelope to valid defaults
--            { schemaVersion:1, defaultSurface:"", historyPageSize:10 }; the real
--            authored config is published as v2 by an admin via the CMS UI/API.
--
--            Tables, PK/index, and draft-nullability semantics are unchanged from
--            0032 — this migration only inserts one surface + its v1 version.
-- Refs:      WS-01 (CMS authoring plane), 0032_init_cms_surfaces,
--            ofc-cms-shell ConfigRouteHost SURFACE_ID.
-- =====================================================================

BEGIN;

-- The config-authoring shell's own surface. Draft stays NULL until the first
-- PUT draft, exactly like the four W2 surfaces seeded by 0032.
INSERT INTO cms_surfaces (surface_id, title)
VALUES
    ('ofl-cms-config-mfe', 'Config MFE')
ON CONFLICT (surface_id) DO NOTHING;

-- Published v1 envelope — byte-for-byte mirror of InMemoryCmsSurfaceStore's
-- seed for this surface ({ surfaceId, enabled:true }, published_by='seed').
INSERT INTO cms_surface_versions (surface_id, version, config, published_at, published_by)
VALUES
    ('ofl-cms-config-mfe', 1, '{"surfaceId":"ofl-cms-config-mfe","enabled":true}'::jsonb, now(), 'seed')
ON CONFLICT (surface_id, version) DO NOTHING;

INSERT INTO schema_migrations (version)
VALUES ('0042_add_cms_config_surface')
ON CONFLICT (version) DO NOTHING;

COMMIT;
