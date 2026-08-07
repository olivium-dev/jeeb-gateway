-- =====================================================================
-- Migration: 0035_converge_usd_money_truth
-- Purpose:   Forward-only convergence of the flat 10% commission policy on
--            delivery_tiers for databases that seeded pre-flat-10% rates.
-- Notes:     Idempotent + guarded. This is NOT a no-op on a fresh DB: base
--            0011 seeds flash/express 0.1500, standard 0.1200, on_the_way/eco
--            0.1000, so this UPDATE actively flattens the 15/15/12 rows to
--            0.1000 on first apply (it is a genuine no-op only once every row
--            already reads 0.1000).
--
--   SCOPE NOTE: this migration owns only delivery_tiers reference-data
--   convergence. COD settlement schemas are deliberately absent from the
--   stateless gateway; unified_payment_gateway owns durable COD records.
-- =====================================================================

BEGIN;

-- Forward-converge every delivery_tiers row to the flat 10% commission.
-- The 0011 seed inserts flash/express 0.1500, standard 0.1200, on_the_way/eco
-- 0.1000 via ON CONFLICT (code) DO NOTHING, so a fresh DB starts at 15/15/12/
-- 10/10 and pre-flat-10% DBs carry whatever they were seeded — this UPDATE
-- flattens them all to 0.1000. (NOT a no-op on a fresh DB.)
DO $$
BEGIN
    IF to_regclass('public.delivery_tiers') IS NOT NULL THEN
        UPDATE delivery_tiers
        SET commission_rate = 0.1000,
            updated_at = now()
        WHERE commission_rate IS DISTINCT FROM 0.1000;
    END IF;
END$$;

INSERT INTO schema_migrations (version)
VALUES ('0035_converge_usd_money_truth')
ON CONFLICT (version) DO NOTHING;

COMMIT;
