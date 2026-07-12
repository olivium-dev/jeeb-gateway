-- =====================================================================
-- Migration: 0039_harden_settlement_batches_sentinel
-- Ticket:    JEBV4-262 (fast-follow hardening of PR #253's 0038 sentinel)
-- Purpose:   Close the TWO false-negative gaps two independent reviewers
--            (Opus + Principal Data Engineer) flagged against
--            db/migrations/0038_assert_settlement_batches_canonical_usd.sql:
--
--            Gap A — 0038 checks a column literally named `status` EXISTS but
--            never inspects its data_type or column_default. 0037 converges
--            status from the retired-UPG enum settlement_status (default
--            'pending') to TEXT (default 'open') — the exact same-name/
--            different-type collision 0038 DOES guard for jeeber_id. A
--            regression that re-widens the enum back in, or merely flips the
--            default back to the retired 'pending', is invisible to 0038.
--
--            Gap B — 0038 asserts the three _usd money columns exist by name
--            but never asserts their NUMERIC precision/scale. The canonical
--            shape (0037's CREATE TABLE branch) is NUMERIC(20,4), consistent
--            with every money column in this schema (settlements.* per 0015).
--            A silent narrowing to e.g. NUMERIC(12,2) would round future
--            sub-cent (4-decimal) writes — precisely the class of financial-
--            correctness regression the sentinel exists to catch.
--
--            Both are additive false-negative closures ONLY: no change to
--            0038's existing checks, no risk to the deploy-safety property
--            PR #253 established. 0038 is already applied/tested; per this
--            repo's convention (0037's own header) an applied migration is
--            never edited in place — this ships as a new forward sentinel.
--
-- Ordering:  Sorts AFTER 0037 (converger) and 0038, so in every reachable
--            post-0037 state — fresh, _lbp-stuck, or already-canonical — this
--            migration is a PASS. It fails ONLY when settlement_batches has
--            genuinely drifted from the canonical NUMERIC(20,4) / TEXT-status
--            shape, which MUST block a deploy.
--
-- Safety:    Pure information_schema assertions — NO DDL, no data change.
--            Idempotent and re-runnable: the CI idempotency job applies
--            apply.sh twice; this file is a stable PASS on both passes once
--            canonical.
-- =====================================================================

BEGIN;

DO $$
DECLARE
    v_status_type       TEXT;
    v_status_default    TEXT;
    v_usd_cols          TEXT[] := ARRAY[
        'total_gross_usd', 'total_commission_usd', 'total_net_usd'
    ];
    v_bad_precision     TEXT[];
BEGIN
    -- 0. Table must exist (0037 guarantees it; runs before this file).
    IF to_regclass('public.settlement_batches') IS NULL THEN
        RAISE EXCEPTION
          'settlement-shape guard (0039): settlement_batches does not exist after 0037 — convergence did not run. Check db/apply.sh ordering.';
    END IF;

    -- A. status must be TEXT with default 'open' (0037 converts the retired-UPG
    --    settlement_status enum, default 'pending', to TEXT default 'open').
    --    Mirrors 0038's jeeber_id type check — a re-widening back to the enum,
    --    or a default flip back to 'pending', must fail LOUDLY here.
    SELECT data_type, column_default
      INTO v_status_type, v_status_default
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'settlement_batches'
      AND column_name = 'status';

    IF v_status_type IS DISTINCT FROM 'text' THEN
        RAISE EXCEPTION
          'settlement-shape guard (0039): settlement_batches.status is % but the app requires TEXT (retired-UPG settlement_status enum leaking back in — 0037 converts it to TEXT default ''open'').',
          v_status_type;
    END IF;

    IF v_status_default IS NULL OR v_status_default NOT LIKE '%open%' THEN
        RAISE EXCEPTION
          'settlement-shape guard (0039): settlement_batches.status default is % but the canonical cash shape defaults to ''open'' (0037 flips the retired-UPG ''pending'').',
          COALESCE(v_status_default, '(none)');
    END IF;

    -- B. The three _usd money columns must be NUMERIC(20,4) — consistent with
    --    every money column in this schema (settlements.* per 0015). A silent
    --    narrowing (e.g. NUMERIC(12,2)) would round future 4-decimal writes.
    --    0038 only asserts these columns exist by name.
    SELECT array_agg(
             column_name || ' NUMERIC(' ||
             COALESCE(numeric_precision::text, '?') || ',' ||
             COALESCE(numeric_scale::text, '?') || ')'
             ORDER BY column_name)
      INTO v_bad_precision
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'settlement_batches'
      AND column_name = ANY (v_usd_cols)
      AND (data_type IS DISTINCT FROM 'numeric'
           OR numeric_precision IS DISTINCT FROM 20
           OR numeric_scale     IS DISTINCT FROM 4);

    IF v_bad_precision IS NOT NULL THEN
        RAISE EXCEPTION
          'settlement-shape guard (0039): settlement_batches USD money column(s) drifted from canonical NUMERIC(20,4): %. Every money column in this schema is NUMERIC(20,4); a narrower type silently rounds sub-cent (4-decimal) writes.',
          array_to_string(v_bad_precision, ', ');
    END IF;

    RAISE NOTICE 'settlement-shape guard (0039): settlement_batches status TEXT/open and USD columns NUMERIC(20,4) are canonical. OK.';
END$$;

INSERT INTO schema_migrations (version)
VALUES ('0039_harden_settlement_batches_sentinel')
ON CONFLICT (version) DO NOTHING;

COMMIT;
