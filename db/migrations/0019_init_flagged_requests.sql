-- =====================================================================
-- Migration: 0019_init_flagged_requests
-- Ticket:    T-backend-048 follow-up (jeeb-gateway durability hardening)
-- Purpose:   Durable flagged_requests table backing
--            PostgresFlaggedRequestStore (IFlaggedRequestStore). Replaces
--            the in-memory admin moderation queue
--            (InMemoryFlaggedRequestStore) so rows opened by
--            ProhibitedItemsScanController.Scan survive a gateway bounce
--            and are visible across replicas / AdminFlaggedRequestsController
--            instances.
-- Notes:     Idempotent — CREATE TABLE/INDEX IF NOT EXISTS, safe to re-run.
--            NEW table — flagged_requests is distinct from the moderated
--            item lexicon (prohibited_items) and the generic audit log
--            (admin_actions), both created in migration 0005; this
--            migration does not touch either.
--            Columns beyond the minimal (id, request_id, reason, status,
--            flagged_at, reviewed_at, reviewer_id) shape are additive so
--            the store is a strict superset of the in-memory
--            implementation it replaces:
--              * user_id       — FlaggedRequest.UserId (required — every
--                                 scan is user-attributed; not nullable).
--              * reason        — holds FlaggedRequest.Description: the
--                                 scanned request text that triggered the
--                                 flag (i.e. the "reason" an admin reviews).
--              * matches       — JSONB snapshot of the scanner's
--                                 ProhibitedItemMatch[] evidence, so
--                                 GetAsync/ListAsync can round-trip the
--                                 full admin-queue DTO without re-scanning.
--              * decision_note — FlaggedRequest.DecisionNote (admin's
--                                 free-text note recorded alongside the
--                                 clear/uphold decision; validated
--                                 <=1000 chars at the controller).
--            No UNIQUE constraint: like admin_escalations (0021), the
--            store does not enforce per-request uniqueness — every
--            scanner hit above the review threshold gets its own row,
--            matching InMemoryFlaggedRequestStore's always-insert
--            Guid.NewGuid() semantics (PostgresFlaggedRequestStore.CreateAsync
--            is a plain INSERT with no ON CONFLICT branch).
-- Refs:      T-backend-048 (prohibited-item NLP scanner + admin review
--            queue). Sibling of migration 0017 (device_tokens) and 0021
--            (admin_escalations) from the same gateway durability
--            hardening pass.
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Extensions guaranteed by 0001 (pgcrypto for gen_random_uuid()); declared
-- here so this migration is also valid when applied in isolation.
-- ---------------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ---------------------------------------------------------------------
-- Table: flagged_requests
--
-- One row per scanner hit that cleared the review threshold
-- (IProhibitedItemScanner, invoked from ProhibitedItemsScanController.Scan).
-- Admins clear (false positive) or uphold (genuine prohibited item) each
-- row via AdminFlaggedRequestsController. request_id is nullable — the
-- scan can run pre-submission, before a delivery request exists yet.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS flagged_requests (
    id             UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id     TEXT        NULL,
    user_id        TEXT        NOT NULL,
    reason         TEXT        NOT NULL,
    matches        JSONB       NOT NULL DEFAULT '[]'::jsonb,
    status         TEXT        NOT NULL DEFAULT 'pending',
    flagged_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    reviewed_at    TIMESTAMPTZ NULL,
    reviewer_id    TEXT        NULL,
    decision_note  TEXT        NULL
);

-- Admin triage queue default view: unfiltered, newest-first
-- (AdminFlaggedRequestsController.List with no status query param).
CREATE INDEX IF NOT EXISTS idx_flagged_requests_flagged_at
    ON flagged_requests (flagged_at DESC);

-- AdminFlaggedRequestsController.List(status=...) filtered + paginated read,
-- and the pending-only admin default view.
CREATE INDEX IF NOT EXISTS idx_flagged_requests_status_flagged_at
    ON flagged_requests (status, flagged_at DESC);

INSERT INTO schema_migrations (version)
VALUES ('0019_init_flagged_requests')
ON CONFLICT (version) DO NOTHING;

COMMIT;
