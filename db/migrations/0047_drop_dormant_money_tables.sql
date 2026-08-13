-- =====================================================================
-- Migration: 0047_drop_dormant_money_tables
-- Ticket:    gwdbx W0-08 (money slice) · refs playbook §3-A, money §5 note
-- Purpose:   Drop the dormant MONEY table delivery_financials. Money truth is
--            wallet-service; the surviving settlement_* tables belong to W5-09.
-- Scope:     TABLE-scoped (G-18). 0008 also carries settlement_batches (live) —
--            this file must never grow to touch it.
-- Safety:    Row-count assert aborts the whole apply if the target holds rows.
-- =====================================================================

BEGIN;

DO $$
DECLARE
    n BIGINT;
BEGIN
    IF to_regclass('delivery_financials') IS NOT NULL THEN
        SELECT count(*) INTO n FROM delivery_financials;
        IF n > 0 THEN
            RAISE EXCEPTION
                'W0-08 abort: delivery_financials holds % row(s) — money rows are never dropped unreviewed.',
                n;
        END IF;
    END IF;
END $$;

DROP TABLE IF EXISTS delivery_financials;

INSERT INTO schema_migrations (version)
VALUES ('0047_drop_dormant_money_tables')
ON CONFLICT (version) DO NOTHING;

COMMIT;
