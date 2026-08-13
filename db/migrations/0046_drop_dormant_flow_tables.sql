-- =====================================================================
-- Migration: 0046_drop_dormant_flow_tables
-- Ticket:    gwdbx W0-08 (flow slice) · refs playbook §3-A, flow/dormant-drops#s2
-- Purpose:   Drop the two dormant FLOW tables. Authority moved upstream:
--            chat_messages -> Firebase project jeeb-5a293 (A2: archive+DROP only,
--            never re-import), offers -> offer-service via UpstreamPendingOffersStore.
-- Scope:     TABLE-scoped (G-18); 0002/0007 both carry live siblings.
-- Safety:    Row-count assert aborts the whole apply if any target holds rows.
-- =====================================================================

BEGIN;

DO $$
DECLARE
    tbl TEXT;
    n   BIGINT;
BEGIN
    FOREACH tbl IN ARRAY ARRAY['chat_messages', 'offers'] LOOP
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

DROP TABLE IF EXISTS chat_messages;
DROP TABLE IF EXISTS offers;

INSERT INTO schema_migrations (version)
VALUES ('0046_drop_dormant_flow_tables')
ON CONFLICT (version) DO NOTHING;

COMMIT;
