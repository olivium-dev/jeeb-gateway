-- =====================================================================
-- Migration: 0007_init_offers
-- Ticket:    T-database-003
-- Purpose:   Offers schema — one row per Jeeber bid on a delivery request.
--            Backs the reverse-auction flow (FR-6.*): Jeebers see a
--            matched request and submit a fee + ETA; the Client accepts
--            exactly one offer, which closes the auction.
-- Notes:     Idempotent. Reuses set_updated_at() from 0001. Status
--            transitions (pending → accepted | rejected | withdrawn) are
--            enforced at the application layer (offer-service); the enum
--            here constrains the value domain only.
-- Refs:      FR-6.* (offer flow), FR-6.6 (10-min auction window),
--            FR-8.1 (request state machine), T-database-002.
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Enum: offer_status
--
--   pending    — submitted by Jeeber; visible to Client; awaiting decision
--   accepted   — Client accepted this offer; all sibling offers should be
--                rejected by the application in the same transaction
--   rejected   — Client rejected (explicitly, or implicitly because a
--                sibling was accepted, or because the auction expired)
--   withdrawn  — Jeeber retracted the offer before Client decision
--
-- Add new values via ALTER TYPE in a follow-up migration; never reorder.
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'offer_status') THEN
        CREATE TYPE offer_status AS ENUM (
            'pending',
            'accepted',
            'rejected',
            'withdrawn'
        );
    END IF;
END$$;

-- ---------------------------------------------------------------------
-- Table: offers
--
-- One row per (request, jeeber) bid. `fee` is the gross amount the
-- Jeeber is asking the Client to pay; commission math happens at
-- settlement time against delivery_tiers.commission_rate.
-- `eta_minutes` is the Jeeber's quoted pickup-to-dropoff estimate
-- (NOT pickup-from-now), per FR-6.2.
-- `note` is an optional free-text message from Jeeber to Client.
--
-- Status-transition timestamps (accepted_at, rejected_at, withdrawn_at)
-- are filled by the application as the status advances; the precise
-- timestamp must match the event published to the realtime fanout, so
-- we don't maintain them by trigger.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS offers (
    id              UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id      UUID         NOT NULL REFERENCES delivery_requests (id) ON DELETE CASCADE,
    jeeber_id       UUID         NOT NULL REFERENCES users (id)             ON DELETE RESTRICT,
    fee             NUMERIC(12,2) NOT NULL,
    eta_minutes     INTEGER      NOT NULL,
    note            TEXT         NULL,
    status          offer_status NOT NULL DEFAULT 'pending',

    accepted_at     TIMESTAMPTZ  NULL,
    rejected_at     TIMESTAMPTZ  NULL,
    withdrawn_at    TIMESTAMPTZ  NULL,

    created_at      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT offers_fee_positive         CHECK (fee > 0),
    CONSTRAINT offers_eta_positive         CHECK (eta_minutes > 0),
    CONSTRAINT offers_note_length          CHECK (note IS NULL OR char_length(note) <= 500),
    -- A terminal status must carry its transition timestamp.
    CONSTRAINT offers_accepted_consistency CHECK (
        (status = 'accepted'  AND accepted_at  IS NOT NULL)
        OR status <> 'accepted'
    ),
    CONSTRAINT offers_rejected_consistency CHECK (
        (status = 'rejected'  AND rejected_at  IS NOT NULL)
        OR status <> 'rejected'
    ),
    CONSTRAINT offers_withdrawn_consistency CHECK (
        (status = 'withdrawn' AND withdrawn_at IS NOT NULL)
        OR status <> 'withdrawn'
    )
);

-- ---------------------------------------------------------------------
-- Uniqueness: one live bid per Jeeber per request.
-- Prevents a Jeeber from accidentally double-submitting, and gives the
-- offer-service a clean upsert key.
-- ---------------------------------------------------------------------
CREATE UNIQUE INDEX IF NOT EXISTS offers_request_jeeber_uniq
    ON offers (request_id, jeeber_id);

-- ---------------------------------------------------------------------
-- Indexes
-- ---------------------------------------------------------------------
-- Acceptance criterion: fast offer listing for the Client "see all bids"
-- view. Newest-first matches the mobile UI's default sort.
CREATE INDEX IF NOT EXISTS offers_request_created_idx
    ON offers (request_id, created_at DESC);

-- Jeeber dashboard: "my open offers", "my offer history".
CREATE INDEX IF NOT EXISTS offers_jeeber_created_idx
    ON offers (jeeber_id, created_at DESC);

-- Auction sweep: matching/expiry workers query for live offers only.
-- Partial keeps the index small once a request closes.
CREATE INDEX IF NOT EXISTS offers_pending_by_request_idx
    ON offers (request_id)
    WHERE status = 'pending';

-- ---------------------------------------------------------------------
-- Trigger: keep updated_at fresh (reuses set_updated_at() from 0001).
-- ---------------------------------------------------------------------
DROP TRIGGER IF EXISTS offers_set_updated_at ON offers;
CREATE TRIGGER offers_set_updated_at
    BEFORE UPDATE ON offers
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

INSERT INTO schema_migrations (version)
VALUES ('0007_init_offers')
ON CONFLICT (version) DO NOTHING;

COMMIT;
