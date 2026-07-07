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
    ('urgent',    'Urgent',    1,  3.0,  30 * 60,       0.25, 'Premium — fastest dispatch, top-of-list matching', 'system', 'system'),
    ('same-day',  'Same-Day',  2, 10.0,  2 * 60 * 60,   0.20, 'Standard same-day rate',                           'system', 'system'),
    ('scheduled', 'Scheduled', 24, 25.0, 24 * 60 * 60,  0.15, 'Choose a delivery window up to 24h ahead',         'system', 'system')
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

INSERT INTO schema_migrations (version)
VALUES ('0035_tier_table_three_tiers_ttl')
ON CONFLICT (version) DO NOTHING;

COMMIT;
