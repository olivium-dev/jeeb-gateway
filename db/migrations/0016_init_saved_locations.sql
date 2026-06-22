-- ============================================================================
-- Migration: 0016_init_saved_locations
-- Ticket:    iter6 — durable saved delivery addresses (ACCT-04 / REQ-02)
-- Purpose:   Make the gateway's per-user Saved Locations BFF DURABLE.
--            Previously SavedLocationsController backed onto an in-memory
--            ISavedLocationStore, so a gateway restart WIPED every saved
--            address (the "can't choose address" symptom). This table is the
--            gateway-owned, restart-durable store keyed by the caller's JWT
--            identity (sub), mirroring the durable settlements table (0015).
--
-- Self-contained: user_id is TEXT (the JWT `sub`), NOT a FK to a users table —
-- the gateway resolves identity from the token and does not own a users table
-- in its own DB. This keeps the migration applicable to a standalone gateway DB.
--
-- Notes:     Idempotent (CREATE … IF NOT EXISTS, DO $$ guards). Additive only —
--            no DROP, no ALTER COLUMN TYPE. Mirrors the "exactly one default
--            per user" invariant via a partial unique index (REQ-02).
-- ============================================================================

BEGIN;

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ---------------------------------------------------------------------------
-- saved_locations: one row per labelled drop-off destination a user reuses
-- (e.g. "Home", "Office"). Flat shape — addresses are user-private, no POI
-- de-duplication across users. `is_default` is at-most-one-per-user via a
-- partial unique index; the application uses an UPDATE-then-INSERT pattern.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS saved_locations (
    id           UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      TEXT         NOT NULL,
    label        VARCHAR(80)  NOT NULL,
    address      TEXT         NULL,
    latitude     DOUBLE PRECISION NOT NULL,
    longitude    DOUBLE PRECISION NOT NULL,
    is_default   BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),

    CONSTRAINT saved_locations_label_nonblank
        CHECK (char_length(btrim(label)) > 0),
    CONSTRAINT saved_locations_lat_range
        CHECK (latitude  >= -90  AND latitude  <= 90),
    CONSTRAINT saved_locations_lng_range
        CHECK (longitude >= -180 AND longitude <= 180)
);

-- List query: per-user, default first then oldest first (matches store order).
CREATE INDEX IF NOT EXISTS saved_locations_user_idx
    ON saved_locations (user_id, created_at ASC);

-- REQ-02: at most one default per user.
CREATE UNIQUE INDEX IF NOT EXISTS saved_locations_one_default_per_user
    ON saved_locations (user_id)
    WHERE is_default = TRUE;

-- ---------------------------------------------------------------------------
-- set_updated_at(): keep updated_at fresh on UPDATE. Defined here with a guard
-- so this migration is self-contained even when 0001 (which also defines it)
-- has not been applied to a standalone gateway DB.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS saved_locations_set_updated_at ON saved_locations;
CREATE TRIGGER saved_locations_set_updated_at
    BEFORE UPDATE ON saved_locations
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- schema_migrations ledger (idempotent; table may not exist in a standalone DB).
CREATE TABLE IF NOT EXISTS schema_migrations (
    version    TEXT PRIMARY KEY,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

INSERT INTO schema_migrations (version)
VALUES ('0016_init_saved_locations')
ON CONFLICT (version) DO NOTHING;

COMMIT;
