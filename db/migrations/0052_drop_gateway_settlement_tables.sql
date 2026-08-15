-- =====================================================================
-- Migration: 0052_drop_gateway_settlement_tables
-- Ticket:    gwdbx W2-R02 (money slice) · owner ruling A23 (2026-08-14)
-- Purpose:   Drop the gateway's four COD settlement tables. The gateway is not
--            the settlement authority; the accounting stops here and is
--            rebuilt on the SETTLEMENT SERVICE (A21 §1; W2-R11 points the
--            gateway at it — wallet-service is NOT the COD home). Writers were
--            neutralised in the SAME PR (Null* settlement stores) so nothing
--            attempts a write against these tables after this file runs.
-- Scope:     TABLE-scoped (G-18). Names exactly four tables and nothing else.
-- Order:     settlements FIRST (0015: batch_id UUID REFERENCES
--            settlement_batches(id)); RESTRICT (the default) on purpose so an
--            unexpected dependent fails the apply rather than being cascaded.
-- Numbering: MUST stay above 0039 — 0038/0039 RAISE EXCEPTION when
--            settlement_batches is absent, and apply.sh re-runs every file.
-- Archive:   NONE. A23 ruling 2 WAIVED the G-07 archive and accepted the data
--            loss; the owner had already dropped these tables on .20 by hand.
-- Rows:      No abort-on-rows assert (0047/0050 carry one). That assert exists
--            to force a review before money rows are destroyed; A23 IS that
--            review, and keeping it would wedge every later migration on any
--            DB still holding rows. The count is RAISEd instead, so the apply
--            log records exactly how much was destroyed.
-- Idempotent in BOTH directions (G-18 + A23): to_regclass skip for the
--            already-dropped state, DROP TABLE IF EXISTS for the rest.
-- =====================================================================

BEGIN;

-- S3.80 D2: apply.sh re-runs every file forever, and the settlement service owns
-- identically-named tables on the same instance. Allow only the live DB and CI's ("jeeb").
DO $$
BEGIN
    IF current_database() NOT IN ('jeeb_gateway', 'jeeb') THEN
        RAISE EXCEPTION
            'W2-R02/0052: refusing to run in database "%" — this migration drops settlement tables and is scoped to jeeb_gateway ONLY.',
            current_database();
    END IF;
END $$;

DO $$
DECLARE
    tbl TEXT;
    n   BIGINT;
BEGIN
    FOREACH tbl IN ARRAY ARRAY[
        'settlements', 'settlement_batches', 'settlement_enqueue', 'settlement_ledger_entries'
    ] LOOP
        IF to_regclass(tbl) IS NULL THEN
            CONTINUE;
        END IF;
        EXECUTE format('SELECT count(*) FROM %I', tbl) INTO n;
        IF n > 0 THEN
            RAISE WARNING
                'W2-R02: dropping % with % row(s) — data loss accepted by owner ruling A23 (no archive).',
                tbl, n;
        END IF;
    END LOOP;
END $$;

DROP TABLE IF EXISTS settlements;
DROP TABLE IF EXISTS settlement_batches;
DROP TABLE IF EXISTS settlement_enqueue;
DROP TABLE IF EXISTS settlement_ledger_entries;

INSERT INTO schema_migrations (version)
VALUES ('0052_drop_gateway_settlement_tables')
ON CONFLICT (version) DO NOTHING;

COMMIT;
