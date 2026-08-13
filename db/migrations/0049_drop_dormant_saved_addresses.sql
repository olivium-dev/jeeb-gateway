-- =====================================================================
-- Migration: 0049_drop_dormant_saved_addresses
-- Ticket:    gwdbx W0-06
-- Purpose:   Drop the dormant saved_addresses table. Address authority is
--            user-management; the gateway has no reader or writer for it.
-- Scope:     TABLE-scoped (G-18).
-- Safety:    O3 gated this on "rows were never migrated, so dropping is data
--            abandonment". Live now holds ZERO rows, so there is nothing to
--            abandon — the gate dissolved on evidence, not on a ruling. The
--            row-count assert below keeps that true: if rows ever reappear,
--            the apply aborts instead of destroying them.
-- Archive:   backups/gwdbx-w006-w010-prefs-20260813T213825Z/saved_addresses.dump
-- =====================================================================

BEGIN;

DO $$
DECLARE
    n BIGINT;
BEGIN
    IF to_regclass('saved_addresses') IS NULL THEN
        RETURN;
    END IF;
    SELECT count(*) INTO n FROM saved_addresses;
    IF n > 0 THEN
        RAISE EXCEPTION
            'W0-06 abort: saved_addresses holds % row(s) — O3 (data abandonment) is live again; re-archive and get an owner ruling before dropping.',
            n;
    END IF;
END $$;

-- RESTRICT (the default) on purpose: an unexpected dependent must fail the
-- apply rather than be silently cascaded away.
DROP TABLE IF EXISTS saved_addresses;

INSERT INTO schema_migrations (version)
VALUES ('0049_drop_dormant_saved_addresses')
ON CONFLICT (version) DO NOTHING;

COMMIT;
