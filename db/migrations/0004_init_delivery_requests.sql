-- =====================================================================
-- Migration: 0004_init_delivery_requests
-- Ticket:    T-database-002
-- Purpose:   Delivery requests + tiers schema. Owns the request lifecycle
--            from creation (voice/text + pickup/dropoff) through the full
--            delivery state machine.
-- Notes:     Idempotent. Requires PostGIS (created here if missing); the
--            radius-based matching path queries pickup_location via a GIST
--            index. Seed data for the 5 tiers is owned by T-database-008
--            (init_seed_data); this migration defines the tables only.
-- Refs:      FR-3.* (voice request), FR-4.* (tiers), FR-5.* (locations),
--            FR-8.1 (status flow), FR-20.* (cancellation).
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Extension: PostGIS
--   pickup/dropoff are GEOGRAPHY(Point, 4326). Idempotent — already
--   created by 0003 in deployed environments; declared here so this
--   migration is also valid when applied in isolation.
-- ---------------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS postgis;

-- ---------------------------------------------------------------------
-- Table: delivery_tiers
--
-- Per FR-4.1 the MVP ships with five tiers:
--   flash       SLA  30 min, radius  3 km
--   express     SLA  60 min, radius  7 km
--   standard    SLA 180 min, radius 15 km
--   on_the_way  SLA NULL,    radius 25 km   (Jeeber-direction; no SLA)
--   eco         SLA 1440 min, radius 25 km  (cheapest)
--
-- `code` is the stable identifier used by application code and API
-- contracts. `sla_minutes` is NULL for tiers without an SLA (on_the_way).
-- `commission_rate` is stored as a fraction (0.1000 = 10%) so finance
-- math doesn't have to remember to divide by 100.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS delivery_tiers (
    id                UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    code              TEXT          NOT NULL,
    name              TEXT          NOT NULL,
    sla_minutes       INTEGER       NULL,
    radius_metres     INTEGER       NOT NULL,
    commission_rate   NUMERIC(5,4)  NOT NULL,
    sort_order        INTEGER       NOT NULL DEFAULT 0,
    is_active         BOOLEAN       NOT NULL DEFAULT TRUE,
    created_at        TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT delivery_tiers_code_format     CHECK (code ~ '^[a-z][a-z0-9_]{1,31}$'),
    CONSTRAINT delivery_tiers_radius_positive CHECK (radius_metres > 0),
    CONSTRAINT delivery_tiers_sla_positive    CHECK (sla_minutes IS NULL OR sla_minutes > 0),
    CONSTRAINT delivery_tiers_commission_range CHECK (
        commission_rate >= 0 AND commission_rate <= 1
    )
);

CREATE UNIQUE INDEX IF NOT EXISTS delivery_tiers_code_uniq
    ON delivery_tiers (code);

-- ---------------------------------------------------------------------
-- Enum: delivery_request_status
--
-- The full delivery state machine (FR-8.1) plus the terminal states
-- reachable from cancellation, timeout, and dispute flows (FR-6.6 / FR-20).
--
--   pending     — created; matching not yet kicked off
--   matched     — candidate Jeebers notified; awaiting offers
--   accepted    — Client accepted one offer; auction closed
--   picked_up   — Jeeber confirmed pickup; live tracking begins
--   heading_off — Jeeber en route to drop-off
--   delivered   — OTP confirmed (FR-9.4); receipt generated
--   rated       — both parties rated OR 7-day window elapsed (terminal)
--   cancelled   — cancelled by client/jeeber/admin    (terminal)
--   expired     — no offers within 10 minutes (FR-6.6) (terminal)
--   disputed    — escalated to admin (FR-13 / OTP lockout) (terminal)
--
-- Add new values via ALTER TYPE in a follow-up migration; never reorder.
-- Status transitions are enforced at the application layer (see the
-- offer-service / gateway delivery controller); this enum only constrains
-- the value domain.
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'delivery_request_status') THEN
        CREATE TYPE delivery_request_status AS ENUM (
            'pending',
            'matched',
            'accepted',
            'picked_up',
            'heading_off',
            'delivered',
            'rated',
            'cancelled',
            'expired',
            'disputed'
        );
    END IF;
END$$;

-- ---------------------------------------------------------------------
-- Table: delivery_requests
--
-- One row per Client-initiated delivery. `description` always holds the
-- final, user-confirmed text (FR-3.4); `transcription` retains the raw
-- STT result so admins can audit edits. `audio_url` points at object
-- storage (S3/R2/MinIO); never inline binary content.
--
-- pickup/dropoff are GEOGRAPHY(Point, 4326) so radius math is in metres
-- on the WGS84 ellipsoid — matches the cost model the matching engine
-- already uses against jeeber_availability.last_location (0003).
--
-- The transition-timestamp columns (matched_at, accepted_at, …) are
-- filled by the application as the status advances; they are NOT
-- maintained by trigger because the precise transition timestamp must
-- match the event published to the realtime/notification fanout.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS delivery_requests (
    id                UUID                       PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id         UUID                       NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    description       TEXT                       NOT NULL,
    -- audio_url MUST point at object storage; never inline payload bytes.
    audio_url         TEXT                       NULL,
    transcription     TEXT                       NULL,
    tier_id           UUID                       NULL REFERENCES delivery_tiers (id) ON DELETE RESTRICT,
    -- SRID 4326 = WGS84 lon/lat; GEOGRAPHY gives metre-accurate ST_DWithin.
    pickup_location   GEOGRAPHY(Point, 4326)     NOT NULL,
    dropoff_location  GEOGRAPHY(Point, 4326)     NOT NULL,
    pickup_address    TEXT                       NULL,
    dropoff_address   TEXT                       NULL,
    status            delivery_request_status    NOT NULL DEFAULT 'pending',

    -- Status-transition timestamps. Filled by the application as the
    -- delivery advances; useful for SLA reporting and incident forensics.
    matched_at        TIMESTAMPTZ                NULL,
    accepted_at       TIMESTAMPTZ                NULL,
    picked_up_at      TIMESTAMPTZ                NULL,
    heading_off_at    TIMESTAMPTZ                NULL,
    delivered_at      TIMESTAMPTZ                NULL,
    rated_at          TIMESTAMPTZ                NULL,
    cancelled_at      TIMESTAMPTZ                NULL,
    expired_at        TIMESTAMPTZ                NULL,

    cancelled_by      UUID                       NULL REFERENCES users (id) ON DELETE SET NULL,
    cancellation_reason TEXT                     NULL,

    created_at        TIMESTAMPTZ                NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ                NOT NULL DEFAULT NOW(),

    -- audio_url, when present, must look like an absolute URL.
    CONSTRAINT delivery_requests_audio_url_format CHECK (
        audio_url IS NULL OR audio_url ~ '^(https?|s3)://'
    ),
    -- Description has a real body (FR-3.4 ships an editable text field).
    CONSTRAINT delivery_requests_description_nonblank CHECK (
        char_length(btrim(description)) > 0
    ),
    -- Cancellation columns move together: either both set or both null.
    CONSTRAINT delivery_requests_cancelled_consistency CHECK (
        (status = 'cancelled' AND cancelled_at IS NOT NULL)
        OR (status <> 'cancelled' AND cancelled_at IS NULL)
    ),
    -- A delivery cannot be 'delivered' without a delivered_at stamp.
    CONSTRAINT delivery_requests_delivered_consistency CHECK (
        status NOT IN ('delivered','rated') OR delivered_at IS NOT NULL
    ),
    -- Tier is required once matching begins; pre-match it may be null
    -- (the Client picks the tier after dictating the request — FR-4.2).
    CONSTRAINT delivery_requests_tier_required_after_pending CHECK (
        status = 'pending' OR tier_id IS NOT NULL
    )
);

-- ---------------------------------------------------------------------
-- Indexes
-- ---------------------------------------------------------------------
-- Acceptance criterion: GIST on pickup_location for radius queries.
-- The matching engine runs ST_DWithin(pickup_location, :pt, :radius).
CREATE INDEX IF NOT EXISTS delivery_requests_pickup_gix
    ON delivery_requests USING GIST (pickup_location);

-- Symmetric index on dropoff for "deliveries heading to area X" admin
-- queries and the ETA/route lookups used by the tracking screen.
CREATE INDEX IF NOT EXISTS delivery_requests_dropoff_gix
    ON delivery_requests USING GIST (dropoff_location);

-- Client history feed: "my recent requests, newest first" (FR-11.3,
-- mobile order list).
CREATE INDEX IF NOT EXISTS delivery_requests_client_created
    ON delivery_requests (client_id, created_at DESC);

-- Admin operations queue and matching-engine scans across active states.
-- Partial keeps the index small (terminal-state rows are cold).
CREATE INDEX IF NOT EXISTS delivery_requests_status_active_idx
    ON delivery_requests (status, created_at DESC)
    WHERE status IN ('pending','matched','accepted','picked_up','heading_off');

-- Tier reporting (e.g. "Flash deliveries last 24h") and the matching
-- candidate-pool query that joins tier → radius.
CREATE INDEX IF NOT EXISTS delivery_requests_tier_idx
    ON delivery_requests (tier_id) WHERE tier_id IS NOT NULL;

-- ---------------------------------------------------------------------
-- Triggers: keep updated_at fresh (reuses set_updated_at() from 0001).
-- ---------------------------------------------------------------------
DROP TRIGGER IF EXISTS delivery_tiers_set_updated_at ON delivery_tiers;
CREATE TRIGGER delivery_tiers_set_updated_at
    BEFORE UPDATE ON delivery_tiers
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

DROP TRIGGER IF EXISTS delivery_requests_set_updated_at ON delivery_requests;
CREATE TRIGGER delivery_requests_set_updated_at
    BEFORE UPDATE ON delivery_requests
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

INSERT INTO schema_migrations (version)
VALUES ('0004_init_delivery_requests')
ON CONFLICT (version) DO NOTHING;

COMMIT;
