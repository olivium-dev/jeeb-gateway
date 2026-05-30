-- =====================================================================
-- Migration: 0013_scheduled_delivery
-- Ticket:    T-backend-046 (Phase 2 — scheduled delivery)
-- Purpose:   Allow a Client to schedule a delivery for a future moment.
--            Matching kicks off at scheduled_at - 30 min (the buffer
--            lives in the gateway's ScheduledDeliveryOptions; the DB
--            only stores the wall-clock target).
-- Notes:     Idempotent. Adds the 'scheduled' enum value, the
--            scheduled_at column, and a supporting partial index so the
--            activator's "due scheduled rows" scan is O(due) rather than
--            O(all-scheduled).
-- Refs:      FR-3.* (request creation), FR-4.* (matching), 0004 base.
-- =====================================================================

-- ---------------------------------------------------------------------
-- Enum: extend delivery_request_status with 'scheduled'
--
-- Postgres forbids USING a newly-ADDed enum value in the SAME transaction
-- that added it ("unsafe use of new value"). The CHECK constraint and the
-- partial index below both reference 'scheduled', so the ADD VALUE must be
-- committed first. It therefore runs in autocommit (NO surrounding BEGIN);
-- the column/constraint/index then run in their own transaction below.
-- Idempotent — ADD VALUE IF NOT EXISTS is a no-op if the value exists, so
-- the non-atomic split is safe to re-run.
-- ---------------------------------------------------------------------
ALTER TYPE delivery_request_status ADD VALUE IF NOT EXISTS 'scheduled' BEFORE 'pending';

BEGIN;

-- ---------------------------------------------------------------------
-- Column: delivery_requests.scheduled_at
--
-- Future moment the Client wants the delivery to happen. NULL = immediate
-- delivery (existing behavior — status starts as 'pending'). When set the
-- gateway initialises status='scheduled'; the activator flips to 'pending'
-- at scheduled_at - MatchingBuffer.
-- ---------------------------------------------------------------------
ALTER TABLE delivery_requests
    ADD COLUMN IF NOT EXISTS scheduled_at  TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS activated_at  TIMESTAMPTZ NULL;

-- Once a row has been activated by the gateway's ScheduledDeliveryActivator
-- the timestamp is the wall-clock moment matching opened — useful for
-- post-hoc SLA reporting ("did we open matching exactly 30 min before
-- scheduled_at?"). Filled by the application, NOT a trigger.

-- ---------------------------------------------------------------------
-- Consistency check: scheduled rows must carry a scheduled_at; non-scheduled
-- rows must not (well, except for the brief post-activation state where the
-- row has moved to 'pending' but scheduled_at is retained for audit).
--
-- The DB-level invariant is the weaker form: scheduled status implies a
-- scheduled_at value. The application is free to retain scheduled_at on
-- the row after activation as audit history.
-- ---------------------------------------------------------------------
ALTER TABLE delivery_requests
    DROP CONSTRAINT IF EXISTS delivery_requests_scheduled_consistency;
ALTER TABLE delivery_requests
    ADD CONSTRAINT delivery_requests_scheduled_consistency CHECK (
        status <> 'scheduled' OR scheduled_at IS NOT NULL
    );

-- ---------------------------------------------------------------------
-- Index: activator scan
--
-- The activator runs every 30 seconds and asks "which scheduled rows
-- have scheduled_at <= now + buffer". Partial index keeps the scanned
-- set tiny even when historical scheduled rows pile up.
-- ---------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS delivery_requests_scheduled_due_idx
    ON delivery_requests (scheduled_at)
    WHERE status = 'scheduled';

INSERT INTO schema_migrations (version)
VALUES ('0013_scheduled_delivery')
ON CONFLICT (version) DO NOTHING;

COMMIT;
