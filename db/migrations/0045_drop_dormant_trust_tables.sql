-- =====================================================================
-- Migration: 0045_drop_dormant_trust_tables
-- Ticket:    gwdbx W0-08 (trust slice) · refs playbook §3-A, trust/dormant-drops#s1
-- Purpose:   Drop the four dormant TRUST tables. Authority moved upstream:
--            ratings -> feedback-service, disputes -> generic case authority,
--            kyc_submissions -> user-management, strikes -> retired.
-- Scope:     TABLE-scoped (G-18). 0001 also carries `users`; 0009 also carried
--            nothing else live. Never drop by migration file.
-- Safety:    Row-count assert aborts the whole apply if any target holds rows.
-- =====================================================================

BEGIN;

-- Dormancy is a precondition, not an assumption: abort loudly rather than
-- destroy rows that appeared after the W0-07 archive was taken.
DO $$
DECLARE
    tbl TEXT;
    n   BIGINT;
BEGIN
    FOREACH tbl IN ARRAY ARRAY[
        'ratings', 'disputes', 'kyc_submissions', 'jeeb_cancellation_strikes'
    ] LOOP
        IF to_regclass(tbl) IS NULL THEN
            CONTINUE;
        END IF;
        EXECUTE format('SELECT count(*) FROM %I', tbl) INTO n;
        IF n > 0 THEN
            RAISE EXCEPTION
                'W0-08 abort: % holds % row(s) — dormancy claim is false; re-archive and get an owner ruling before dropping.',
                tbl, n;
        END IF;
    END LOOP;
END $$;

-- RESTRICT (the default) on purpose: an unexpected dependent must fail the
-- apply rather than be silently cascaded away.
DROP TABLE IF EXISTS ratings;
DROP TABLE IF EXISTS disputes;
DROP TABLE IF EXISTS kyc_submissions;
DROP TABLE IF EXISTS jeeb_cancellation_strikes;

INSERT INTO schema_migrations (version)
VALUES ('0045_drop_dormant_trust_tables')
ON CONFLICT (version) DO NOTHING;

COMMIT;
