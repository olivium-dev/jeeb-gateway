-- =====================================================================
-- Migration: 0003_init_jeeber_availability
-- Ticket:    T-database-007
-- Purpose:   Persistent availability + last-known-location state for the
--            Jeeber (driver) side of the marketplace. Pairs with the Redis
--            hot path documented in db/JEEBER_LOCATION_DESIGN.md.
-- Notes:     Idempotent. Requires the PostGIS extension for the GEOGRAPHY
--            column and the GIST radius index. The matching service queries
--            this table for fallback / cold-cache reads; the request hot
--            path goes through Redis (see design doc).
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Extension: PostGIS
--   GEOGRAPHY(Point, 4326) and ST_DWithin / ST_Distance require PostGIS.
--   The DB role running migrations must have CREATE on the database;
--   in managed envs (RDS, etc.) PostGIS may already be pre-installed.
-- ---------------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS postgis;

-- ---------------------------------------------------------------------
-- Enum: jeeber_vehicle_type
--   The vehicle a Jeeber is currently driving. Picked at go-online time
--   so the matching service can filter the candidate pool by vehicle.
--   Add values via ALTER TYPE in a follow-up migration; never reorder.
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'jeeber_vehicle_type') THEN
        CREATE TYPE jeeber_vehicle_type AS ENUM (
            'car',
            'motorbike',
            'bicycle',
            'scooter',
            'walk'
        );
    END IF;
END$$;

-- ---------------------------------------------------------------------
-- Table: jeeber_availability
--
-- One row per Jeeber. Updated on go-online, go-offline, and every
-- location heartbeat. The hot path (per-second updates) writes to Redis
-- first; a debounced flusher (see design doc) writes the latest state
-- back here so the table survives Redis loss / restart.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS jeeber_availability (
    user_id        UUID                    PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    is_online      BOOLEAN                 NOT NULL DEFAULT FALSE,
    vehicle_type   jeeber_vehicle_type     NOT NULL,
    -- SRID 4326 = WGS84 lon/lat. GEOGRAPHY (not GEOMETRY) so distance
    -- math is in metres on the ellipsoid; cheap for radius queries.
    last_location  GEOGRAPHY(Point, 4326)  NULL,
    last_seen_at   TIMESTAMPTZ             NULL,
    created_at     TIMESTAMPTZ             NOT NULL DEFAULT NOW(),
    updated_at     TIMESTAMPTZ             NOT NULL DEFAULT NOW(),

    -- An online Jeeber must have at least one heartbeat behind them;
    -- the matching service treats rows missing either field as offline.
    CONSTRAINT jeeber_availability_online_requires_location CHECK (
        is_online = FALSE
        OR (last_location IS NOT NULL AND last_seen_at IS NOT NULL)
    )
);

-- ---------------------------------------------------------------------
-- Indexes
-- ---------------------------------------------------------------------
-- Radius queries: ST_DWithin(last_location, :pt, :radius_metres).
-- GIST is the only viable index type for GEOGRAPHY columns.
CREATE INDEX IF NOT EXISTS jeeber_availability_last_location_gix
    ON jeeber_availability USING GIST (last_location);

-- Matching candidate pool: "online Jeebers of vehicle type X".
-- Partial keeps the index small (only the online subset is hot).
CREATE INDEX IF NOT EXISTS jeeber_availability_online_vehicle_idx
    ON jeeber_availability (vehicle_type) WHERE is_online = TRUE;

-- Stale-online sweeper scan (see design doc, "TTL strategy").
-- Lets the reaper find Jeebers whose heartbeat lapsed without a full
-- table scan.
CREATE INDEX IF NOT EXISTS jeeber_availability_last_seen_idx
    ON jeeber_availability (last_seen_at) WHERE is_online = TRUE;

-- ---------------------------------------------------------------------
-- Trigger: keep updated_at fresh (reuses set_updated_at() from 0001).
-- ---------------------------------------------------------------------
DROP TRIGGER IF EXISTS jeeber_availability_set_updated_at ON jeeber_availability;
CREATE TRIGGER jeeber_availability_set_updated_at
    BEFORE UPDATE ON jeeber_availability
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

INSERT INTO schema_migrations (version)
VALUES ('0003_init_jeeber_availability')
ON CONFLICT (version) DO NOTHING;

COMMIT;
