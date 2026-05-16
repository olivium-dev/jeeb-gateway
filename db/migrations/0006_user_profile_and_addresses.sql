-- =====================================================================
-- Migration: 0006_user_profile_and_addresses
-- Ticket:    T-backend-029
-- Purpose:   Extend user identity for the profile API:
--              * users.language   — preferred language for notifications
--              * users.rating     — denormalised average rating (computed
--                                   by the score-taking-service nightly,
--                                   read-only from the gateway perspective)
--              * users.rating_count
--                                — number of ratings backing the average,
--                                   exposed so clients can show "(34)"
--              * saved_addresses  — repeated drop-off locations per user
-- Notes:     Idempotent. Reuses set_updated_at() from 0001. Backfills
--            existing rows with 'en' (the MVP default).
-- Refs:      FR-user-profile, FR-saved-addresses, FR-admin-user-search.
-- =====================================================================

BEGIN;

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ---------------------------------------------------------------------
-- users: language preference + denormalised rating
-- ---------------------------------------------------------------------
ALTER TABLE users
    ADD COLUMN IF NOT EXISTS language     VARCHAR(8)    NOT NULL DEFAULT 'en',
    ADD COLUMN IF NOT EXISTS rating       NUMERIC(3,2)  NULL,
    ADD COLUMN IF NOT EXISTS rating_count INTEGER       NOT NULL DEFAULT 0;

-- BCP-47 shape; permissive enough to accept "en", "ar", "en-US", "ar-SA".
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'users_language_format'
    ) THEN
        ALTER TABLE users
            ADD CONSTRAINT users_language_format
            CHECK (language ~ '^[a-z]{2}(-[A-Z]{2})?$');
    END IF;
END$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'users_rating_range'
    ) THEN
        ALTER TABLE users
            ADD CONSTRAINT users_rating_range
            CHECK (rating IS NULL OR (rating >= 0 AND rating <= 5));
    END IF;
END$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'users_rating_count_nonneg'
    ) THEN
        ALTER TABLE users
            ADD CONSTRAINT users_rating_count_nonneg
            CHECK (rating_count >= 0);
    END IF;
END$$;

-- Admin search uses pg_trgm on name; ensure the extension is available.
CREATE EXTENSION IF NOT EXISTS "pg_trgm";

-- Indexes that back GET /admin/users/search.
CREATE INDEX IF NOT EXISTS users_name_trgm_idx
    ON users USING GIN (name gin_trgm_ops);

CREATE INDEX IF NOT EXISTS users_email_trgm_idx
    ON users USING GIN ((email::text) gin_trgm_ops)
    WHERE email IS NOT NULL;

CREATE INDEX IF NOT EXISTS users_phone_trgm_idx
    ON users USING GIN (phone gin_trgm_ops);

-- ---------------------------------------------------------------------
-- saved_addresses
--
-- Each row is one labelled drop-off destination a user reuses (e.g.
-- "Home", "Office"). The shape is intentionally flat — we don't model
-- a Place/POI table yet because addresses are user-private and have no
-- de-duplication value across users.
--
-- `is_default` is enforced at most-one-per-user via a partial unique
-- index; the application uses an UPDATE-then-INSERT pattern to flip it.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS saved_addresses (
    id           UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      UUID         NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    label        VARCHAR(64)  NOT NULL,
    line1        TEXT         NOT NULL,
    line2        TEXT         NULL,
    city         TEXT         NULL,
    country      VARCHAR(2)   NULL,
    latitude     NUMERIC(9,6) NULL,
    longitude    NUMERIC(9,6) NULL,
    is_default   BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT saved_addresses_label_nonblank
        CHECK (char_length(btrim(label)) > 0),
    CONSTRAINT saved_addresses_line1_nonblank
        CHECK (char_length(btrim(line1)) > 0),
    CONSTRAINT saved_addresses_country_format
        CHECK (country IS NULL OR country ~ '^[A-Z]{2}$'),
    CONSTRAINT saved_addresses_latlng_paired
        CHECK ((latitude IS NULL) = (longitude IS NULL)),
    CONSTRAINT saved_addresses_lat_range
        CHECK (latitude IS NULL OR (latitude >= -90 AND latitude <= 90)),
    CONSTRAINT saved_addresses_lng_range
        CHECK (longitude IS NULL OR (longitude >= -180 AND longitude <= 180))
);

CREATE INDEX IF NOT EXISTS saved_addresses_user_idx
    ON saved_addresses (user_id, created_at DESC);

CREATE UNIQUE INDEX IF NOT EXISTS saved_addresses_one_default_per_user
    ON saved_addresses (user_id)
    WHERE is_default = TRUE;

-- Case-insensitive uniqueness on label keeps labels stable per user.
CREATE UNIQUE INDEX IF NOT EXISTS saved_addresses_user_label_uniq
    ON saved_addresses (user_id, LOWER(label));

DROP TRIGGER IF EXISTS saved_addresses_set_updated_at ON saved_addresses;
CREATE TRIGGER saved_addresses_set_updated_at
    BEFORE UPDATE ON saved_addresses
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

INSERT INTO schema_migrations (version)
VALUES ('0006_user_profile_and_addresses')
ON CONFLICT (version) DO NOTHING;

COMMIT;
