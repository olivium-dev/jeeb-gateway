-- =====================================================================
-- Migration: 0050_drop_dormant_prefs_tables
-- Ticket:    gwdbx W0-10
-- Purpose:   Drop the two vestigial preference tables. Authority is
--            remote-user-preferences, reached via the namespaced blob keys
--            "jeeb.saved_locations" / notification prefs. The gateway's own
--            Postgres stores for both were DELETED in the RUP migration.
-- Scope:     TABLE-scoped (G-18).
-- Safety:    O6 previously gated this because dropping looked like it would
--            foreclose re-hosting prefs in the gateway. That option no longer
--            exists: a 2026-08-14 boot with the flag off aborted FAIL-CLOSED
--            with ISavedLocationStore / INotificationPreferencesStore resolving
--            to in-memory, proving the local stores are gone. Dropping empty
--            tables therefore forecloses nothing.
-- Archive:   backups/gwdbx-w006-w010-prefs-20260813T213825Z/ (both dumps, checksums OK)
-- =====================================================================

BEGIN;

DO $$
DECLARE
    tbl TEXT;
    n   BIGINT;
BEGIN
    FOREACH tbl IN ARRAY ARRAY['notification_preferences', 'saved_locations'] LOOP
        IF to_regclass(tbl) IS NULL THEN
            CONTINUE;
        END IF;
        EXECUTE format('SELECT count(*) FROM %I', tbl) INTO n;
        IF n > 0 THEN
            RAISE EXCEPTION
                'W0-10 abort: % holds % row(s) — archive and rule on the rows before dropping; do not relax this assert.',
                tbl, n;
        END IF;
    END LOOP;
END $$;

-- RESTRICT (the default) on purpose: an unexpected dependent must fail the
-- apply rather than be silently cascaded away.
DROP TABLE IF EXISTS notification_preferences;
DROP TABLE IF EXISTS saved_locations;

INSERT INTO schema_migrations (version)
VALUES ('0050_drop_dormant_prefs_tables')
ON CONFLICT (version) DO NOTHING;

COMMIT;
