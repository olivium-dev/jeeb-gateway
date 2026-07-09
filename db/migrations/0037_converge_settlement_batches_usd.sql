-- =====================================================================
-- Migration: 0037_converge_settlement_batches_usd
-- Purpose:   Forward-only, idempotent convergence of settlement_batches (and
--            the settlements.currency default) to the SINGLE canonical
--            cash-USD shape the gateway app actually reads/writes
--            (Financials/WeeklySettlementBatch.cs,
--            Controllers/AdminSettlementsController.cs).
--
--            Re-expresses, as a proper forward migration, the schema delta the
--            wo/integration-4branch branch had introduced by EDITING the
--            already-applied base migration 0015 IN PLACE (renaming
--            settlement_batches.total_*_lbp -> total_*_usd, flipping the
--            currency DEFAULT LBP->USD, and settlements.currency DEFAULT
--            LBP->USD). In-place edits to applied migrations never re-run on
--            deployed DBs, so the delta had to move into this forward file;
--            0015 has been restored to its original content.
--
--            Also RESOLVES a pre-existing base-migration collision:
--            settlement_batches is CREATEd by BOTH 0008 (retired UPG payout
--            shape: total_commission/total_payout/delivery_count/payout_method
--            + settlement_status enum, jeeber_id UUID REFERENCES users) AND
--            0015 (cash shape: total_*_lbp/currency/status TEXT/paid_by,
--            jeeber_id TEXT). Both use CREATE TABLE IF NOT EXISTS with no DROP
--            between them, so on a fresh DB 0008 wins (UPG shape) even though
--            the app needs the cash shape, while the live DB physically
--            carries the cash _lbp shape. This migration branches on ACTUAL
--            column presence and converges every starting state to canonical.
--
-- Canonical settlement_batches (matches WeeklySettlementBatch.cs reader):
--   id UUID PK, jeeber_id TEXT NOT NULL, period_start DATE, period_end DATE,
--   total_gross_usd/total_commission_usd/total_net_usd NUMERIC(20,4) NOT NULL
--   DEFAULT 0, settlement_count INT NOT NULL DEFAULT 0, currency TEXT NOT NULL
--   DEFAULT 'USD', status TEXT NOT NULL DEFAULT 'open', paid_at TIMESTAMPTZ,
--   paid_by TEXT, created_at/updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
--   UNIQUE(jeeber_id, period_start).
--
-- Safety:    Idempotent + guarded (information_schema column-presence + to_
--            regclass + pg_constraint). No-op when already canonical.
--            NON-DESTRUCTIVE: the live cash path only RENAMEs columns / changes
--            DEFAULTs (no data loss); the retired-UPG path only reshapes when
--            the table is EMPTY (fresh DB) and RAISEs otherwise, so populated
--            financial columns are never dropped. Runs under db/apply.sh which
--            re-runs every file each deploy.
-- =====================================================================

BEGIN;

DO $$
DECLARE
    v_row_count BIGINT;
BEGIN
    -- 0. No table at all -> create the canonical cash-USD shape directly.
    IF to_regclass('public.settlement_batches') IS NULL THEN
        CREATE TABLE settlement_batches (
            id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            jeeber_id            TEXT NOT NULL,
            period_start         DATE NOT NULL,
            period_end           DATE NOT NULL,
            total_gross_usd      NUMERIC(20,4) NOT NULL DEFAULT 0,
            total_commission_usd NUMERIC(20,4) NOT NULL DEFAULT 0,
            total_net_usd        NUMERIC(20,4) NOT NULL DEFAULT 0,
            settlement_count     INT NOT NULL DEFAULT 0,
            currency             TEXT NOT NULL DEFAULT 'USD',
            status               TEXT NOT NULL DEFAULT 'open',
            paid_at              TIMESTAMPTZ,
            paid_by              TEXT,
            created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at           TIMESTAMPTZ NOT NULL DEFAULT now()
        );
    END IF;

    -- 1. Retired UPG shape (0008) detected -> reshape to cash-USD.
    --    Marker: total_payout (UPG-only). Only reshape when EMPTY (fresh DB);
    --    refuse loudly if populated rather than drop financial columns.
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'settlement_batches'
          AND column_name = 'total_payout'
    ) THEN
        SELECT count(*) INTO v_row_count FROM settlement_batches;
        IF v_row_count > 0 THEN
            RAISE EXCEPTION
              'settlement_batches is in the retired UPG shape with % row(s); refusing to auto-reshape (would drop financial columns). Manual convergence required.', v_row_count;
        END IF;

        -- Drop UPG indexes that depend on columns/type being changed.
        DROP INDEX IF EXISTS settlement_batches_open_idx;
        DROP INDEX IF EXISTS settlement_batches_jeeber_period_idx;
        DROP INDEX IF EXISTS settlement_batches_jeeber_period_uniq;

        -- Drop UPG CHECK constraints.
        ALTER TABLE settlement_batches DROP CONSTRAINT IF EXISTS settlement_batches_period_valid;
        ALTER TABLE settlement_batches DROP CONSTRAINT IF EXISTS settlement_batches_amounts_nonneg;
        ALTER TABLE settlement_batches DROP CONSTRAINT IF EXISTS settlement_batches_delivery_count_nonneg;
        ALTER TABLE settlement_batches DROP CONSTRAINT IF EXISTS settlement_batches_paid_consistency;
        ALTER TABLE settlement_batches DROP CONSTRAINT IF EXISTS settlement_batches_failed_consistency;
        ALTER TABLE settlement_batches DROP CONSTRAINT IF EXISTS settlement_batches_cancelled_consistency;

        -- Drop the jeeber_id -> users(id) FK, then widen jeeber_id UUID -> TEXT.
        ALTER TABLE settlement_batches DROP CONSTRAINT IF EXISTS settlement_batches_jeeber_id_fkey;
        ALTER TABLE settlement_batches ALTER COLUMN jeeber_id TYPE TEXT USING jeeber_id::text;

        -- status settlement_status enum -> TEXT DEFAULT 'open'.
        ALTER TABLE settlement_batches ALTER COLUMN status DROP DEFAULT;
        ALTER TABLE settlement_batches ALTER COLUMN status TYPE TEXT USING status::text;
        ALTER TABLE settlement_batches ALTER COLUMN status SET DEFAULT 'open';

        -- Drop UPG-only columns (table is empty here -> no data loss).
        ALTER TABLE settlement_batches DROP COLUMN IF EXISTS payout_method;
        ALTER TABLE settlement_batches DROP COLUMN IF EXISTS total_commission;
        ALTER TABLE settlement_batches DROP COLUMN IF EXISTS total_payout;
        ALTER TABLE settlement_batches DROP COLUMN IF EXISTS delivery_count;
        ALTER TABLE settlement_batches DROP COLUMN IF EXISTS processed_at;
        ALTER TABLE settlement_batches DROP COLUMN IF EXISTS failed_at;
        ALTER TABLE settlement_batches DROP COLUMN IF EXISTS cancelled_at;
        ALTER TABLE settlement_batches DROP COLUMN IF EXISTS failure_reason;
        ALTER TABLE settlement_batches DROP COLUMN IF EXISTS external_reference;
    END IF;

    -- 2. Live cash _lbp shape (0015) detected -> rename to _usd (non-destructive).
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'settlement_batches'
          AND column_name = 'total_gross_lbp'
    ) THEN
        ALTER TABLE settlement_batches RENAME COLUMN total_gross_lbp TO total_gross_usd;
    END IF;
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'settlement_batches'
          AND column_name = 'total_commission_lbp'
    ) THEN
        ALTER TABLE settlement_batches RENAME COLUMN total_commission_lbp TO total_commission_usd;
    END IF;
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'settlement_batches'
          AND column_name = 'total_net_lbp'
    ) THEN
        ALTER TABLE settlement_batches RENAME COLUMN total_net_lbp TO total_net_usd;
    END IF;

    -- 3. Canonical backfill for ALL cash paths (idempotent; no-op when already
    --    canonical). Guarantees every column/default/constraint the app relies
    --    on exists regardless of origin shape.
    ALTER TABLE settlement_batches ADD COLUMN IF NOT EXISTS total_gross_usd      NUMERIC(20,4) NOT NULL DEFAULT 0;
    ALTER TABLE settlement_batches ADD COLUMN IF NOT EXISTS total_commission_usd NUMERIC(20,4) NOT NULL DEFAULT 0;
    ALTER TABLE settlement_batches ADD COLUMN IF NOT EXISTS total_net_usd        NUMERIC(20,4) NOT NULL DEFAULT 0;
    ALTER TABLE settlement_batches ADD COLUMN IF NOT EXISTS settlement_count     INT NOT NULL DEFAULT 0;
    ALTER TABLE settlement_batches ADD COLUMN IF NOT EXISTS currency             TEXT NOT NULL DEFAULT 'USD';
    ALTER TABLE settlement_batches ADD COLUMN IF NOT EXISTS status               TEXT NOT NULL DEFAULT 'open';
    ALTER TABLE settlement_batches ADD COLUMN IF NOT EXISTS paid_at              TIMESTAMPTZ;
    ALTER TABLE settlement_batches ADD COLUMN IF NOT EXISTS paid_by              TEXT;
    ALTER TABLE settlement_batches ADD COLUMN IF NOT EXISTS created_at           TIMESTAMPTZ NOT NULL DEFAULT now();
    ALTER TABLE settlement_batches ADD COLUMN IF NOT EXISTS updated_at           TIMESTAMPTZ NOT NULL DEFAULT now();

    -- Canonical currency default (the _lbp origin's base default was 'LBP').
    ALTER TABLE settlement_batches ALTER COLUMN currency SET DEFAULT 'USD';

    -- Canonical unique + read indexes (same names as base 0015 so no-op on live).
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_settlement_batches_jeeber_period') THEN
        ALTER TABLE settlement_batches
            ADD CONSTRAINT uq_settlement_batches_jeeber_period UNIQUE (jeeber_id, period_start);
    END IF;
    CREATE INDEX IF NOT EXISTS idx_settlement_batches_status    ON settlement_batches(status);
    CREATE INDEX IF NOT EXISTS idx_settlement_batches_jeeber_id ON settlement_batches(jeeber_id);
    CREATE INDEX IF NOT EXISTS idx_settlement_batches_period    ON settlement_batches(period_start, period_end);

    -- 4. settlements.currency default -> USD (0015 in-place edit re-expressed).
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'settlements'
          AND column_name = 'currency'
    ) THEN
        ALTER TABLE settlements ALTER COLUMN currency SET DEFAULT 'USD';
    END IF;
END$$;

-- Ledger consistency: record this convergence AND acknowledge the 0015
-- settlement schema as applied. The live DB physically carries the 0015 tables
-- but recorded only 0008 (base 0015 is a manual-era migration that never
-- self-recorded). Both inserts are truthful in every state reached here.
INSERT INTO schema_migrations (version) VALUES ('0015_init_settlements_batches')
ON CONFLICT (version) DO NOTHING;
INSERT INTO schema_migrations (version) VALUES ('0037_converge_settlement_batches_usd')
ON CONFLICT (version) DO NOTHING;

COMMIT;
