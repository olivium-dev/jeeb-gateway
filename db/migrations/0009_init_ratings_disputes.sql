-- =====================================================================
-- Migration: 0009_init_ratings_disputes
-- Ticket:    T-database-005
-- Purpose:   Post-delivery feedback + escalation schema:
--              * ratings   — two-sided 1-5 stars with blind reveal
--                            (revealed_at NULL until both sides have
--                            rated OR the 7-day window has elapsed).
--              * disputes  — admin-handled escalations against a
--                            delivery (FR-13). Categorised, with a
--                            human-readable resolution and the admin
--                            who closed it.
-- Notes:     Idempotent. Reuses set_updated_at() from 0001. The
--            denormalised `users.rating` / `users.rating_count` columns
--            (added in 0006) remain the read path for the BFF — they
--            are recomputed by the score-taking-service from the rows
--            in `ratings`. This migration owns the source of truth only.
-- Refs:      FR-10.* (post-delivery rating), FR-11.* (blind reveal),
--            FR-13.* (dispute resolution), T-database-005.
-- =====================================================================

BEGIN;

CREATE EXTENSION IF NOT EXISTS "pgcrypto";   -- gen_random_uuid()

-- ---------------------------------------------------------------------
-- Table: ratings
--
-- One row per (delivery, rater). Either party of a delivery may rate
-- the other exactly once; the unique index on (delivery_id, rater_id)
-- enforces it. `ratee_id` is denormalised from the delivery so the
-- aggregation path (rating average per user) hits a single index.
--
-- Blind-reveal model:
--   - A row is INSERTed with `revealed_at = NULL` the moment the rater
--     submits. The score-taking-service sweeps every minute (and on
--     each new insert) and stamps `revealed_at = NOW()` on BOTH rows
--     of a delivery as soon as the second party rates — OR on any
--     remaining unrevealed row whose delivery has crossed the 7-day
--     post-delivery boundary (FR-11.2).
--   - The BFF read endpoint filters `WHERE revealed_at IS NOT NULL`
--     before returning a rating to either party. Until reveal, the
--     star value is private to the rater (and admins).
--
-- The denormalised average lives on `users` (column added in 0006);
-- the score-taking-service writes it. This table is the audit-trail
-- and source of truth.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS ratings (
    id            UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    delivery_id   UUID         NOT NULL REFERENCES delivery_requests (id) ON DELETE RESTRICT,
    rater_id      UUID         NOT NULL REFERENCES users (id)             ON DELETE RESTRICT,
    ratee_id      UUID         NOT NULL REFERENCES users (id)             ON DELETE RESTRICT,
    stars         SMALLINT     NOT NULL,
    comment       TEXT         NULL,
    revealed_at   TIMESTAMPTZ  NULL,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT ratings_stars_range     CHECK (stars BETWEEN 1 AND 5),
    CONSTRAINT ratings_rater_not_ratee CHECK (rater_id <> ratee_id),
    CONSTRAINT ratings_comment_length  CHECK (
        comment IS NULL OR char_length(comment) <= 1000
    )
);

-- Acceptance criterion: one rating per (delivery, rater).
CREATE UNIQUE INDEX IF NOT EXISTS ratings_delivery_rater_uniq
    ON ratings (delivery_id, rater_id);

-- Acceptance criterion: aggregate-rating queries by ratee.
-- Powers the "average rating for user X" recomputation and any
-- "show me this user's recent reviews" read path.
CREATE INDEX IF NOT EXISTS ratings_ratee_created_idx
    ON ratings (ratee_id, created_at DESC);

-- Reveal-sweep worker queue: pick up still-blind ratings, newest first.
-- Partial index keeps the hot set tiny once a rating is revealed.
CREATE INDEX IF NOT EXISTS ratings_unrevealed_idx
    ON ratings (created_at)
    WHERE revealed_at IS NULL;

-- Per-delivery lookup: "fetch both sides of this delivery's ratings".
CREATE INDEX IF NOT EXISTS ratings_delivery_idx
    ON ratings (delivery_id);

-- ---------------------------------------------------------------------
-- Enum: dispute_category
--
--   item_not_delivered  — Client claims package never arrived.
--   item_damaged        — Goods arrived damaged.
--   wrong_item          — Wrong item delivered.
--   late_delivery       — SLA breach claimed by Client.
--   payment_issue       — Disagreement over fee, commission, goods cost.
--   harassment          — Behavioural complaint against the other party.
--   fraud               — Suspected fraudulent activity.
--   other               — Free-text fallback (description is mandatory).
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'dispute_category') THEN
        CREATE TYPE dispute_category AS ENUM (
            'item_not_delivered',
            'item_damaged',
            'wrong_item',
            'late_delivery',
            'payment_issue',
            'harassment',
            'fraud',
            'other'
        );
    END IF;
END$$;

-- ---------------------------------------------------------------------
-- Enum: dispute_status
--
--   open       — filed; awaiting admin pickup
--   in_review  — an admin has claimed it (admin_id set)
--   resolved   — admin closed with a resolution (terminal)
--   rejected   — admin dismissed the claim                (terminal)
--   cancelled  — reporter withdrew the dispute            (terminal)
--
-- State transitions are enforced at the application layer (the
-- admin/dispute controller). The enum constrains the value domain only.
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'dispute_status') THEN
        CREATE TYPE dispute_status AS ENUM (
            'open',
            'in_review',
            'resolved',
            'rejected',
            'cancelled'
        );
    END IF;
END$$;

-- ---------------------------------------------------------------------
-- Table: disputes
--
-- One row per filed dispute. A delivery may collect multiple disputes
-- (one per reporter, or multiple from the same reporter over distinct
-- issues), so no unique constraint on delivery_id alone. The
-- application enforces the "one open dispute per (delivery, reporter)"
-- rule via the partial unique index below — that mirrors the offer/KYC
-- "one active per X" pattern.
--
-- `description` is mandatory at file-time; the resolver writes
-- `resolution` when transitioning the row to a terminal status.
-- `admin_id` is set the moment an admin claims the dispute
-- (status → 'in_review') and stays set through every terminal state.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS disputes (
    id            UUID              PRIMARY KEY DEFAULT gen_random_uuid(),
    delivery_id   UUID              NOT NULL REFERENCES delivery_requests (id) ON DELETE RESTRICT,
    reporter_id   UUID              NOT NULL REFERENCES users (id)             ON DELETE RESTRICT,
    category      dispute_category  NOT NULL,
    description   TEXT              NOT NULL,
    status        dispute_status    NOT NULL DEFAULT 'open',
    resolution    TEXT              NULL,
    admin_id      UUID              NULL REFERENCES users (id) ON DELETE SET NULL,

    resolved_at   TIMESTAMPTZ       NULL,
    rejected_at   TIMESTAMPTZ       NULL,
    cancelled_at  TIMESTAMPTZ       NULL,

    created_at    TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ       NOT NULL DEFAULT NOW(),

    CONSTRAINT disputes_description_nonblank CHECK (
        char_length(btrim(description)) > 0
    ),
    CONSTRAINT disputes_description_length CHECK (
        char_length(description) <= 4000
    ),
    CONSTRAINT disputes_resolution_length CHECK (
        resolution IS NULL OR char_length(resolution) <= 4000
    ),
    -- An in-review or terminal-by-admin dispute must carry the admin id.
    CONSTRAINT disputes_admin_required_for_review CHECK (
        (status IN ('in_review', 'resolved', 'rejected') AND admin_id IS NOT NULL)
        OR status IN ('open', 'cancelled')
    ),
    -- Resolved disputes must record both the resolution text and timestamp.
    CONSTRAINT disputes_resolved_consistency CHECK (
        (status = 'resolved' AND resolution IS NOT NULL AND resolved_at IS NOT NULL)
        OR status <> 'resolved'
    ),
    CONSTRAINT disputes_rejected_consistency CHECK (
        (status = 'rejected' AND rejected_at IS NOT NULL) OR status <> 'rejected'
    ),
    CONSTRAINT disputes_cancelled_consistency CHECK (
        (status = 'cancelled' AND cancelled_at IS NOT NULL) OR status <> 'cancelled'
    )
);

-- Admin queue: list newest open / in_review disputes first.
-- Partial keeps the index small as terminal disputes accumulate.
CREATE INDEX IF NOT EXISTS disputes_open_queue_idx
    ON disputes (status, created_at DESC)
    WHERE status IN ('open', 'in_review');

-- Reporter dashboard: "my disputes".
CREATE INDEX IF NOT EXISTS disputes_reporter_idx
    ON disputes (reporter_id, created_at DESC);

-- Per-delivery view: "every dispute on this delivery".
CREATE INDEX IF NOT EXISTS disputes_delivery_idx
    ON disputes (delivery_id);

-- Admin workload view: "disputes currently assigned to admin X".
CREATE INDEX IF NOT EXISTS disputes_admin_open_idx
    ON disputes (admin_id, created_at DESC)
    WHERE admin_id IS NOT NULL AND status = 'in_review';

-- At most one open/in_review dispute per (delivery, reporter). The
-- application uses upsert semantics: re-filing the same complaint is a
-- no-op; filing a distinct complaint must wait for the first to close.
CREATE UNIQUE INDEX IF NOT EXISTS disputes_one_open_per_reporter_delivery
    ON disputes (delivery_id, reporter_id)
    WHERE status IN ('open', 'in_review');

-- ---------------------------------------------------------------------
-- Triggers: keep updated_at fresh (reuses set_updated_at() from 0001).
-- ---------------------------------------------------------------------
DROP TRIGGER IF EXISTS ratings_set_updated_at ON ratings;
CREATE TRIGGER ratings_set_updated_at
    BEFORE UPDATE ON ratings
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

DROP TRIGGER IF EXISTS disputes_set_updated_at ON disputes;
CREATE TRIGGER disputes_set_updated_at
    BEFORE UPDATE ON disputes
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

INSERT INTO schema_migrations (version)
VALUES ('0009_init_ratings_disputes')
ON CONFLICT (version) DO NOTHING;

COMMIT;
