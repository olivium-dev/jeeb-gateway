-- =====================================================================
-- Migration: 0038_assert_settlement_batches_canonical_usd
-- Ticket:    settlement-shape convergence lane (SETTLEMENT-SHAPE-FINDING.md)
-- Purpose:   FORWARD SENTINEL that LOCKS the canonical cash-USD
--            settlement_batches shape which 0037 converges to, so any future
--            re-collision or drift fails LOUDLY here in db/apply.sh (and in the
--            CI "Database migrations (idempotency + seed)" job, which runs
--            apply.sh) instead of silently shipping the wrong shape.
--
--            This is the durable guard for the exact failure mode behind
--            SETTLEMENT-SHAPE-FINDING.md: settlement_batches is CREATEd by BOTH
--            0008 (retired UPG payout shape) and 0015 (cash _lbp shape) under
--            CREATE TABLE IF NOT EXISTS, so a fresh DB silently lands the WRONG
--            shape and the live dev DB physically carried the _lbp shape while
--            recording only 0008. 0037 branches on actual column presence and
--            converges every starting state to the single canonical shape the
--            gateway app reads/writes (Financials/WeeklySettlementBatch.cs:
--            PostgresSettlementBatchStore, Controllers/AdminSettlementsController).
--            0038 asserts that convergence actually held.
--
-- Canonical settlement_batches (must match WeeklySettlementBatch.cs reader/writer):
--   id UUID, jeeber_id TEXT, period_start DATE, period_end DATE,
--   total_gross_usd / total_commission_usd / total_net_usd NUMERIC,
--   settlement_count INT, currency TEXT (default 'USD'), status TEXT,
--   paid_at TIMESTAMPTZ, paid_by TEXT, created_at/updated_at TIMESTAMPTZ.
--
-- Ordering:  Sorts AFTER 0037 (and after 0008's re-created indexes on any
--            re-apply), so in EVERY reachable post-0037 state — fresh,
--            _lbp-stuck, or already-canonical — this migration is a PASS. It
--            fails ONLY when settlement_batches is genuinely non-canonical,
--            which MUST block a deploy. This applies the durable money-branch
--            lesson: the shape guard sorts AFTER the convergence it protects,
--            never before it.
--
-- Safety:    Pure information_schema assertions — NO DDL, no data change.
--            Idempotent and re-runnable: the CI idempotency job applies apply.sh
--            twice; this file is a stable PASS on both passes once canonical.
-- =====================================================================

BEGIN;

DO $$
DECLARE
    -- Columns the app REQUIRES (canonical cash-USD shape).
    v_required TEXT[] := ARRAY[
        'id', 'jeeber_id', 'period_start', 'period_end',
        'total_gross_usd', 'total_commission_usd', 'total_net_usd',
        'settlement_count', 'currency', 'status',
        'paid_at', 'paid_by', 'created_at', 'updated_at'
    ];
    -- Columns that MUST be gone: the cash _lbp originals (renamed by 0037) and
    -- the retired-UPG payout columns (dropped by 0037). Their presence means a
    -- collision/drift re-landed the wrong shape.
    v_forbidden TEXT[] := ARRAY[
        'total_gross_lbp', 'total_commission_lbp', 'total_net_lbp',   -- cash _lbp originals
        'total_payout', 'total_commission', 'delivery_count',          -- retired UPG payout cols
        'payout_method', 'processed_at', 'failed_at', 'cancelled_at',  -- retired UPG state cols
        'failure_reason', 'external_reference'
    ];
    v_missing        TEXT[];
    v_present_bad    TEXT[];
    v_jeeber_type    TEXT;
BEGIN
    -- 0. Table must exist (0037 guarantees it; runs before this file).
    IF to_regclass('public.settlement_batches') IS NULL THEN
        RAISE EXCEPTION
          'settlement-shape guard (0038): settlement_batches does not exist after 0037 — convergence did not run. Check db/apply.sh ordering.';
    END IF;

    -- 1. All required canonical columns present?
    SELECT array_agg(c ORDER BY c) INTO v_missing
    FROM unnest(v_required) AS c
    WHERE NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'settlement_batches'
          AND column_name = c
    );
    IF v_missing IS NOT NULL THEN
        RAISE EXCEPTION
          'settlement-shape guard (0038): settlement_batches is MISSING canonical USD column(s): %. The 0008/0015 CREATE-IF-NOT-EXISTS collision has re-landed a non-canonical shape; 0037 must converge to the cash-USD shape the gateway reads (WeeklySettlementBatch.cs).',
          array_to_string(v_missing, ', ');
    END IF;

    -- 2. No forbidden (_lbp / retired-UPG) columns left behind?
    SELECT array_agg(c ORDER BY c) INTO v_present_bad
    FROM unnest(v_forbidden) AS c
    WHERE EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'settlement_batches'
          AND column_name = c
    );
    IF v_present_bad IS NOT NULL THEN
        RAISE EXCEPTION
          'settlement-shape guard (0038): settlement_batches still carries stale column(s): %. This is the cash _lbp shape and/or the retired UPG payout shape leaking through — 0037 convergence did not fully apply.',
          array_to_string(v_present_bad, ', ');
    END IF;

    -- 3. jeeber_id must be TEXT (the app reads it via reader.GetString; the
    --    retired-UPG shape had jeeber_id UUID REFERENCES users(id)).
    SELECT data_type INTO v_jeeber_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'settlement_batches'
      AND column_name = 'jeeber_id';
    IF v_jeeber_type IS DISTINCT FROM 'text' THEN
        RAISE EXCEPTION
          'settlement-shape guard (0038): settlement_batches.jeeber_id is % but the app requires TEXT (retired-UPG UUID shape leaking through).',
          v_jeeber_type;
    END IF;

    -- 4. currency default must be USD (0037 flips the base 0015 LBP default).
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'settlement_batches'
          AND column_name = 'currency'
          AND column_default LIKE '%USD%'
    ) THEN
        RAISE EXCEPTION
          'settlement-shape guard (0038): settlement_batches.currency default is not USD — 0037 currency convergence did not apply (base 0015 defaulted LBP).';
    END IF;

    -- All good: the canonical cash-USD shape is locked in.
    RAISE NOTICE 'settlement-shape guard (0038): settlement_batches is canonical cash-USD. OK.';
END$$;

INSERT INTO schema_migrations (version)
VALUES ('0038_assert_settlement_batches_canonical_usd')
ON CONFLICT (version) DO NOTHING;

COMMIT;
