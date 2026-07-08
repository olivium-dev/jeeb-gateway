-- =====================================================================
-- Migration: 0035_converge_usd_money_truth
-- Purpose:   Forward-only convergence for databases that already applied
--            the historical idempotent money migrations before this branch
--            renamed LBP settlement-batch totals and changed seeded
--            commission policy to flat 10% USD.
-- Notes:     Idempotent. Guarded catalog checks avoid touching fresh
--            databases where the USD schema/seed values already exist.
-- =====================================================================

BEGIN;

DO $$
BEGIN
    IF to_regclass('public.settlement_batches') IS NOT NULL THEN
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'settlement_batches'
              AND column_name = 'total_gross_lbp'
        ) AND NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'settlement_batches'
              AND column_name = 'total_gross_usd'
        ) THEN
            ALTER TABLE settlement_batches RENAME COLUMN total_gross_lbp TO total_gross_usd;
        END IF;

        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'settlement_batches'
              AND column_name = 'total_commission_lbp'
        ) AND NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'settlement_batches'
              AND column_name = 'total_commission_usd'
        ) THEN
            ALTER TABLE settlement_batches RENAME COLUMN total_commission_lbp TO total_commission_usd;
        END IF;

        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'settlement_batches'
              AND column_name = 'total_net_lbp'
        ) AND NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'settlement_batches'
              AND column_name = 'total_net_usd'
        ) THEN
            ALTER TABLE settlement_batches RENAME COLUMN total_net_lbp TO total_net_usd;
        END IF;
    END IF;
END$$;

DO $$
BEGIN
    IF to_regclass('public.delivery_tiers') IS NOT NULL THEN
        UPDATE delivery_tiers
        SET commission_rate = 0.1000,
            updated_at = now()
        WHERE code IN ('flash', 'express', 'standard', 'on_the_way', 'eco')
          AND commission_rate IS DISTINCT FROM 0.1000;
    END IF;

    IF to_regclass('public.settlement_batches') IS NOT NULL
       AND EXISTS (
           SELECT 1 FROM information_schema.columns
           WHERE table_schema = 'public'
             AND table_name = 'settlement_batches'
             AND column_name = 'currency'
       ) THEN
        UPDATE settlement_batches
        SET currency = 'USD',
            updated_at = now()
        WHERE currency IS DISTINCT FROM 'USD';
    END IF;

    IF to_regclass('public.settlements') IS NOT NULL
       AND EXISTS (
           SELECT 1 FROM information_schema.columns
           WHERE table_schema = 'public'
             AND table_name = 'settlements'
             AND column_name = 'currency'
       ) THEN
        UPDATE settlements
        SET currency = 'USD',
            updated_at = now()
        WHERE currency IS DISTINCT FROM 'USD';
    END IF;
END$$;

INSERT INTO schema_migrations (version)
VALUES ('0035_converge_usd_money_truth')
ON CONFLICT (version) DO NOTHING;

COMMIT;
