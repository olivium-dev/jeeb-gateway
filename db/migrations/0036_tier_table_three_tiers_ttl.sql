-- =====================================================================
-- Migration: 0036_tier_table_three_tiers_ttl
-- Ticket:    Q-013
-- Purpose:   Collapse the gateway tier catalog to exactly three tiers and
--            add the per-tier request TTL used by RequestExpirySweeper.
--
-- Notes:     Idempotent + fully guarded. Every table-dependent operation is
--            wrapped in a to_regclass presence check and every constraint add
--            is guarded by a pg_constraint name check, so this is safe to run
--            against a fresh DB, the live drifted DB, and an already-converged
--            DB (no-op when already matching). Adds request_ttl_seconds to the
--            gateway `tiers` table, upserts the canonical three rows, removes
--            rows outside that fixed catalog, and forward-converges
--            `delivery_tiers` to the flat 10% commission.
-- =====================================================================

BEGIN;

DO $$
BEGIN
    -- ---- gateway tiers catalog (created by 0029) --------------------
    IF to_regclass('public.tiers') IS NOT NULL THEN
        ALTER TABLE tiers ADD COLUMN IF NOT EXISTS request_ttl_seconds INT;

        UPDATE tiers
           SET request_ttl_seconds = CASE id
                WHEN 'urgent'    THEN 30 * 60
                WHEN 'same-day'  THEN 2 * 60 * 60
                WHEN 'scheduled' THEN 24 * 60 * 60
                ELSE 24 * 60 * 60
            END
         WHERE request_ttl_seconds IS NULL;

        ALTER TABLE tiers ALTER COLUMN request_ttl_seconds SET DEFAULT (30 * 60);
        ALTER TABLE tiers ALTER COLUMN request_ttl_seconds SET NOT NULL;

        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_tiers_request_ttl_seconds') THEN
            ALTER TABLE tiers
                ADD CONSTRAINT ck_tiers_request_ttl_seconds
                CHECK (request_ttl_seconds BETWEEN 60 AND 2592000);
        END IF;

        INSERT INTO tiers (
            id, name, sla_hours, radius_km, request_ttl_seconds,
            commission_rate, price_hint, created_by, updated_by
        )
        VALUES
            ('urgent',    'Urgent',    1,  3.0,  30 * 60,      0.10, 'Premium — fastest dispatch, top-of-list matching', 'system', 'system'),
            ('same-day',  'Same-Day',  2, 10.0,  2 * 60 * 60,  0.10, 'Standard same-day rate',                           'system', 'system'),
            ('scheduled', 'Scheduled', 24, 25.0, 24 * 60 * 60, 0.10, 'Choose a delivery window up to 24h ahead',         'system', 'system')
        ON CONFLICT (id) DO UPDATE
           SET name                = EXCLUDED.name,
               sla_hours           = EXCLUDED.sla_hours,
               radius_km           = EXCLUDED.radius_km,
               request_ttl_seconds = EXCLUDED.request_ttl_seconds,
               commission_rate     = EXCLUDED.commission_rate,
               price_hint          = EXCLUDED.price_hint,
               updated_by          = 'system',
               updated_at          = now();

        DELETE FROM tiers WHERE id NOT IN ('urgent', 'same-day', 'scheduled');

        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_tiers_fixed_catalog_id') THEN
            ALTER TABLE tiers
                ADD CONSTRAINT ck_tiers_fixed_catalog_id
                CHECK (id IN ('urgent', 'same-day', 'scheduled'));
        END IF;

        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_tiers_commission_rate_flat') THEN
            ALTER TABLE tiers
                ADD CONSTRAINT ck_tiers_commission_rate_flat
                CHECK (commission_rate = 0.10);
        END IF;
    END IF;

    -- ---- delivery_tiers reference catalog (created by 0004/0011) -----
    IF to_regclass('public.delivery_tiers') IS NOT NULL THEN
        -- Forward-converge already-deployed rows to the flat 10% commission.
        -- The 0011 seed uses ON CONFLICT (code) DO NOTHING, so DBs provisioned
        -- before the flat-10% ruling still carry old per-tier rates; fresh DBs
        -- already seed 0.1000 so this is a no-op there.
        UPDATE delivery_tiers
           SET commission_rate = 0.1000
         WHERE commission_rate <> 0.1000;

        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_delivery_tiers_commission_rate_flat') THEN
            ALTER TABLE delivery_tiers
                ADD CONSTRAINT ck_delivery_tiers_commission_rate_flat
                CHECK (commission_rate = 0.1000);
        END IF;
    END IF;
END$$;

INSERT INTO schema_migrations (version)
VALUES ('0036_tier_table_three_tiers_ttl')
ON CONFLICT (version) DO NOTHING;

COMMIT;
