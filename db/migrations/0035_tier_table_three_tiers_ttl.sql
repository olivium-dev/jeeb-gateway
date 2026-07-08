-- =====================================================================
-- Migration: 0035_tier_table_three_tiers_ttl
-- Ticket:    Q-013
-- Purpose:   Collapse the gateway tier catalog to exactly three tiers and
--            add the per-tier request TTL used by RequestExpirySweeper.
--
-- Notes:     Idempotent. Adds request_ttl_seconds to the existing gateway
--            tiers table, upserts the canonical three rows, and removes rows
--            outside that fixed catalog so existing DBs converge to the same
--            catalog as InMemoryTiersStore.
-- =====================================================================

BEGIN;

ALTER TABLE tiers
    ADD COLUMN IF NOT EXISTS request_ttl_seconds INT;

UPDATE tiers
   SET request_ttl_seconds = CASE id
        WHEN 'urgent' THEN 30 * 60
        WHEN 'same-day' THEN 2 * 60 * 60
        WHEN 'scheduled' THEN 24 * 60 * 60
        ELSE 24 * 60 * 60
    END
 WHERE request_ttl_seconds IS NULL;

ALTER TABLE tiers
    ALTER COLUMN request_ttl_seconds SET DEFAULT (30 * 60),
    ALTER COLUMN request_ttl_seconds SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
          FROM pg_constraint
         WHERE conname = 'ck_tiers_request_ttl_seconds'
    ) THEN
        ALTER TABLE tiers
            ADD CONSTRAINT ck_tiers_request_ttl_seconds
            CHECK (request_ttl_seconds BETWEEN 60 AND 2592000);
    END IF;
END$$;

INSERT INTO tiers (
    id,
    name,
    sla_hours,
    radius_km,
    request_ttl_seconds,
    commission_rate,
    price_hint,
    created_by,
    updated_by
)
VALUES
    ('urgent',    'Urgent',    1,  3.0,  30 * 60,       0.10, 'Premium — fastest dispatch, top-of-list matching', 'system', 'system'),
    ('same-day',  'Same-Day',  2, 10.0,  2 * 60 * 60,   0.10, 'Standard same-day rate',                           'system', 'system'),
    ('scheduled', 'Scheduled', 24, 25.0, 24 * 60 * 60,  0.10, 'Choose a delivery window up to 24h ahead',         'system', 'system')
ON CONFLICT (id) DO UPDATE
   SET name                = EXCLUDED.name,
       sla_hours           = EXCLUDED.sla_hours,
       radius_km           = EXCLUDED.radius_km,
       request_ttl_seconds = EXCLUDED.request_ttl_seconds,
       commission_rate     = EXCLUDED.commission_rate,
       price_hint          = EXCLUDED.price_hint,
       updated_by          = 'system',
       updated_at          = now();

DELETE FROM tiers
 WHERE id NOT IN ('urgent', 'same-day', 'scheduled');

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
          FROM pg_constraint
         WHERE conname = 'ck_tiers_fixed_catalog_id'
    ) THEN
        ALTER TABLE tiers
            ADD CONSTRAINT ck_tiers_fixed_catalog_id
            CHECK (id IN ('urgent', 'same-day', 'scheduled'));
    END IF;
END$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
          FROM pg_constraint
         WHERE conname = 'ck_tiers_commission_rate_flat'
    ) THEN
        ALTER TABLE tiers
            ADD CONSTRAINT ck_tiers_commission_rate_flat
            CHECK (commission_rate = 0.10);
    END IF;
END$$;

-- Forward-converge already-deployed delivery_tiers rows to the flat 10% commission.
-- The 0011 seed uses ON CONFLICT (code) DO NOTHING, so databases provisioned before
-- the flat-10% ruling still carry the old per-tier rates (15%/20%/12%). This UPDATE
-- brings them to 0.1000; fresh databases already seed 0.1000 so it is a no-op there.
UPDATE delivery_tiers
   SET commission_rate = 0.1000
 WHERE commission_rate <> 0.1000;

-- Enforce the flat-rate invariant on delivery_tiers going forward (idempotent add).
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
          FROM pg_constraint
         WHERE conname = 'ck_delivery_tiers_commission_rate_flat'
    ) THEN
        ALTER TABLE delivery_tiers
            ADD CONSTRAINT ck_delivery_tiers_commission_rate_flat
            CHECK (commission_rate = 0.1000);
    END IF;
END$$;

INSERT INTO schema_migrations (version)
VALUES ('0035_tier_table_three_tiers_ttl')
ON CONFLICT (version) DO NOTHING;

COMMIT;
