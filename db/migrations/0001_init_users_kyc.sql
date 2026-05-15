-- =====================================================================
-- Migration: 0001_init_users_kyc
-- Ticket:    T-database-001
-- Purpose:   Core identity schema — users, roles, KYC submissions.
-- Notes:     Idempotent. Safe to re-run; uses IF NOT EXISTS and guarded
--            DO blocks for type/constraint creation.
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Extensions
-- ---------------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS "pgcrypto";   -- gen_random_uuid()
CREATE EXTENSION IF NOT EXISTS "citext";     -- case-insensitive email

-- ---------------------------------------------------------------------
-- Enum: kyc_status
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'kyc_status') THEN
        CREATE TYPE kyc_status AS ENUM (
            'pending',
            'in_review',
            'approved',
            'rejected',
            'expired'
        );
    END IF;
END$$;

-- ---------------------------------------------------------------------
-- Table: users
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS users (
    id           UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    phone        VARCHAR(20)  NOT NULL,
    email        CITEXT       NULL,
    name         VARCHAR(255) NOT NULL,
    avatar_url   TEXT         NULL,
    -- roles stored as JSONB array, e.g. ["customer","driver","admin"]
    roles        JSONB        NOT NULL DEFAULT '[]'::jsonb,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT users_roles_is_array CHECK (jsonb_typeof(roles) = 'array'),
    CONSTRAINT users_phone_format   CHECK (phone ~ '^\+?[0-9]{7,15}$')
);

CREATE UNIQUE INDEX IF NOT EXISTS users_phone_uniq ON users (phone);
CREATE UNIQUE INDEX IF NOT EXISTS users_email_uniq ON users (email) WHERE email IS NOT NULL;
CREATE INDEX        IF NOT EXISTS users_roles_gin  ON users USING GIN (roles);
CREATE INDEX        IF NOT EXISTS users_created_at ON users (created_at DESC);

-- ---------------------------------------------------------------------
-- Table: kyc_submissions
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS kyc_submissions (
    id            UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id       UUID         NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    status        kyc_status   NOT NULL DEFAULT 'pending',
    -- documents stored as JSONB array of {type, url, uploaded_at}
    documents     JSONB        NOT NULL DEFAULT '[]'::jsonb,
    reviewer_id   UUID         NULL REFERENCES users (id) ON DELETE SET NULL,
    review_notes  TEXT         NULL,
    submitted_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    reviewed_at   TIMESTAMPTZ  NULL,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT kyc_documents_is_array CHECK (jsonb_typeof(documents) = 'array'),
    CONSTRAINT kyc_reviewed_consistency CHECK (
        (status IN ('approved','rejected') AND reviewed_at IS NOT NULL)
        OR status NOT IN ('approved','rejected')
    )
);

CREATE INDEX IF NOT EXISTS kyc_user_id           ON kyc_submissions (user_id);
CREATE INDEX IF NOT EXISTS kyc_status_idx        ON kyc_submissions (status);
CREATE INDEX IF NOT EXISTS kyc_submitted_at_desc ON kyc_submissions (submitted_at DESC);
-- Only one submission may be actively pending/in_review per user.
CREATE UNIQUE INDEX IF NOT EXISTS kyc_one_active_per_user
    ON kyc_submissions (user_id)
    WHERE status IN ('pending', 'in_review');

-- ---------------------------------------------------------------------
-- Trigger: keep updated_at fresh
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS users_set_updated_at ON users;
CREATE TRIGGER users_set_updated_at
    BEFORE UPDATE ON users
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

DROP TRIGGER IF EXISTS kyc_set_updated_at ON kyc_submissions;
CREATE TRIGGER kyc_set_updated_at
    BEFORE UPDATE ON kyc_submissions
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- ---------------------------------------------------------------------
-- Migration ledger — records that this script has been applied.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS schema_migrations (
    version     TEXT        PRIMARY KEY,
    applied_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO schema_migrations (version)
VALUES ('0001_init_users_kyc')
ON CONFLICT (version) DO NOTHING;

COMMIT;
