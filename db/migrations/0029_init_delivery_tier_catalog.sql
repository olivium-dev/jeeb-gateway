-- =====================================================================
-- Migration: 0029_init_delivery_tier_catalog
-- Ticket:    JEBV4-125 (AUDIT-A IN-MEM-LIVE) durability follow-up
-- Purpose:   Durable backing store for the admin-managed delivery tier
--            catalog (T-backend-009). Replaces InMemoryTiersStore
--            (Tiers/ITiersStore.cs), whose rows lived only in gateway
--            process memory and were LOST on every restart / replica move
--            — an admin's tier edits (add/rename/re-price/remove) silently
--            reverted to the seeded defaults on the next deploy or crash.
--
--            This is the gateway's OWN admin tier catalog (JeebGateway.Tiers,
--            surfaced by AdminTiersController + TiersController, contract
--            DeliveryTier: slug id, name, sla_hours, radius_km,
--            commission_rate, price_hint). It is DELIBERATELY a separate
--            table from `delivery_tiers` (migration 0011), which is a
--            different reference-data catalog with a different shape
--            (code/sla_minutes/radius_metres/sort_order) owned by the
--            request-validation path (JeebGateway.Requests.CatalogBacked-
--            TiersStore). Keeping them separate avoids conflating two
--            distinct consumers on one row shape.
--
-- Notes:     Idempotent — CREATE TABLE/INDEX IF NOT EXISTS + the seed uses
--            ON CONFLICT DO NOTHING, so re-running is a no-op and never
--            stomps an admin's hand-edited row. The five default tiers
--            (urgent, same-day, scheduled, economy, on-the-way) mirror
--            InMemoryTiersStore.DefaultTiers EXACTLY (same ids, SLAs,
--            radii, commission rates, price hints, created_by/updated_by=
--            'system') so first-run behaviour is byte-for-byte the same as
--            the in-memory seed. Because the seed only inserts once (ON
--            CONFLICT DO NOTHING), a tier an admin later DELETES stays
--            deleted across restarts — the durable, correct behaviour
--            (the in-memory store used to resurrect it on every boot).
--
--            Name uniqueness is enforced case-insensitively via a UNIQUE
--            index on LOWER(name), matching InMemoryTiersStore's
--            OrdinalIgnoreCase name-conflict check. commission_rate and
--            radius_km are DOUBLE PRECISION to match the DeliveryTier
--            contract's `double` fields exactly (no decimal<->double
--            coercion at the mapping layer).
-- Refs:      T-backend-009 (tier catalog), FR-4.1/FR-4.2 (tier picker),
--            JEBV4-125 (IN-MEM-LIVE durability).
-- =====================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS tiers (
    id              TEXT             PRIMARY KEY,
    name            TEXT             NOT NULL,
    sla_hours       INT              NOT NULL,
    radius_km       DOUBLE PRECISION NOT NULL,
    commission_rate DOUBLE PRECISION NOT NULL,
    price_hint      TEXT             NOT NULL DEFAULT '',
    created_by      TEXT,
    updated_by      TEXT,
    created_at      TIMESTAMPTZ      NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ      NOT NULL DEFAULT now()
);

-- Case-insensitive name uniqueness — mirrors InMemoryTiersStore's
-- OrdinalIgnoreCase HasNameConflict check. This is the ON CONFLICT target
-- PostgresTiersStore maps to DuplicateTierNameException.
CREATE UNIQUE INDEX IF NOT EXISTS uq_tiers_name_lower
    ON tiers ((LOWER(name)));

-- Primary read path: ListAsync orders by SLA then name (tier-picker order).
CREATE INDEX IF NOT EXISTS idx_tiers_sla_name
    ON tiers (sla_hours, LOWER(name));

-- ---------------------------------------------------------------------
-- Seed the five default tiers — EXACT mirror of
-- InMemoryTiersStore.DefaultTiers(now). ON CONFLICT DO NOTHING keeps this
-- idempotent and never overwrites an admin's edit.
-- ---------------------------------------------------------------------
INSERT INTO tiers (id, name, sla_hours, radius_km, commission_rate, price_hint, created_by, updated_by)
VALUES
    ('urgent',     'Urgent',      1,  5.0,  0.25, 'Premium — fastest dispatch, top-of-list matching', 'system', 'system'),
    ('same-day',   'Same-Day',    8,  15.0, 0.20, 'Standard same-day rate',                           'system', 'system'),
    ('scheduled',  'Scheduled',  24,  25.0, 0.15, 'Choose a delivery window up to 24h ahead',         'system', 'system'),
    ('economy',    'Economy',    48,  50.0, 0.10, 'Lowest price — best for non-urgent items',         'system', 'system'),
    ('on-the-way', 'On-the-Way',  4,  10.0, 0.18, 'Matched to Jeebers already routed near your pickup','system', 'system')
ON CONFLICT (id) DO NOTHING;

INSERT INTO schema_migrations (version)
VALUES ('0029_init_delivery_tier_catalog')
ON CONFLICT (version) DO NOTHING;

COMMIT;
