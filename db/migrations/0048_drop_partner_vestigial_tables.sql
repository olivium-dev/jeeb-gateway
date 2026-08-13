-- =====================================================================
-- Migration: 0048_drop_partner_vestigial_tables
-- Ticket:    gwdbx W0-08 (partner residue) · refs playbook money/partner-bff-drop#s5
-- Purpose:   Drop the two vestigial PARTNER tables. Both authorities are already
--            live elsewhere: StateServicePartnerWalletOperationStore and
--            StateServicePartnerOtpChallengeStore hold the idempotency KV, and the
--            money legs run through wallet-service via PartnerWalletService.
-- Scope:     TABLE-scoped (G-18); 0040/0041 own exactly these two tables.
-- Safety:    Row-count assert stays intact. Live carried 5+2 self-labelled QA rows
--            from the 2026-07-17 partner tick run; those were archived (W0-07) and
--            disposed of explicitly BEFORE this migration, never by weakening the
--            assert. Any future row aborts the apply, which is the point.
-- =====================================================================

BEGIN;

DO $$
DECLARE
    tbl TEXT;
    n   BIGINT;
BEGIN
    FOREACH tbl IN ARRAY ARRAY['partner_wallet_operations', 'partner_otp_challenges'] LOOP
        IF to_regclass(tbl) IS NULL THEN
            CONTINUE;
        END IF;
        EXECUTE format('SELECT count(*) FROM %I', tbl) INTO n;
        IF n > 0 THEN
            RAISE EXCEPTION
                'W0-08 abort: % holds % row(s) — archive and rule on the rows before dropping; do not relax this assert.',
                tbl, n;
        END IF;
    END LOOP;
END $$;

DROP TABLE IF EXISTS partner_wallet_operations;
DROP TABLE IF EXISTS partner_otp_challenges;

INSERT INTO schema_migrations (version)
VALUES ('0048_drop_partner_vestigial_tables')
ON CONFLICT (version) DO NOTHING;

COMMIT;
