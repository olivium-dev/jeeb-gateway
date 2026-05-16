-- =====================================================================
-- Migration: 0010_init_account_deletions
-- Ticket:    T-backend-035
-- Purpose:   Account-deletion queue + audit trail (GDPR-like).
--            Backs DELETE /users/me.
-- Notes:     Idempotent. Reuses set_updated_at() from 0001. The 30-day
--            SLA is encoded by the worker, not the schema, so legal can
--            tweak the value via config without a migration.
-- Refs:      AC for T-backend-035 —
--              * Deletion queued (not immediate if active delivery exists)
--              * PII hard-deleted within 30 days
--              * Order records anonymized (user_id → SHA-256 pseudonym)
--              * Financial records retained for accounting (anonymized)
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Enum: account_deletion_status
--
--   pending_active_delivery  — request accepted; 30-day timer NOT
--                              started; user still has at least one
--                              row in delivery_request_status::ActiveStates
--   scheduled                — timer running; orders + ledger anonymized;
--                              PII hard-delete due at scheduled_purge_at
--   completed                — PII hard-deleted (terminal)
--
-- Add new values via ALTER TYPE in a follow-up migration; never reorder.
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'account_deletion_status') THEN
        CREATE TYPE account_deletion_status AS ENUM (
            'pending_active_delivery',
            'scheduled',
            'completed'
        );
    END IF;
END$$;

-- ---------------------------------------------------------------------
-- Table: account_deletions
--
-- One open row per user. A user re-asking for deletion while a row
-- already exists is idempotent — the existing row is returned and no
-- new timer is started. Once completed, the row remains as the audit
-- trail (CompletedAt + AnonymizedUserHash) so finance can answer
-- "which pseudonym replaced which past user id".
--
-- anonymized_user_hash is the deterministic SHA-256(user_id) hex digest
-- written onto every retained order + financial-ledger row in place of
-- the original user id. Same input → same hash, so cross-table analytics
-- joins still work after deletion.
--
-- The user_id FK is ON DELETE CASCADE because the only way the user row
-- itself ever disappears is the operator-initiated hard-purge that runs
-- separately from this flow (left to a follow-up migration / operator
-- script — the AC retains the row as an anonymized stub).
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS account_deletions (
    user_id              UUID                    PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    status               account_deletion_status NOT NULL,
    anonymized_user_hash TEXT                    NOT NULL,
    requested_at         TIMESTAMPTZ             NOT NULL DEFAULT NOW(),
    scheduled_purge_at   TIMESTAMPTZ             NULL,
    completed_at         TIMESTAMPTZ             NULL,
    created_at           TIMESTAMPTZ             NOT NULL DEFAULT NOW(),
    updated_at           TIMESTAMPTZ             NOT NULL DEFAULT NOW(),

    -- SHA-256 hex digest is always 64 lowercase hex chars.
    CONSTRAINT account_deletions_hash_format
        CHECK (anonymized_user_hash ~ '^[0-9a-f]{64}$'),

    -- Pending rows have no timer; scheduled/completed rows must have one.
    CONSTRAINT account_deletions_timer_consistency CHECK (
        (status = 'pending_active_delivery' AND scheduled_purge_at IS NULL)
        OR (status <> 'pending_active_delivery' AND scheduled_purge_at IS NOT NULL)
    ),

    -- Completed rows must record when PII was actually purged.
    CONSTRAINT account_deletions_completed_consistency CHECK (
        (status = 'completed' AND completed_at IS NOT NULL)
        OR status <> 'completed'
    )
);

-- Worker sweep: "which rows are past their SLA?" — partial keeps the
-- index small once a row is completed (terminal).
CREATE INDEX IF NOT EXISTS account_deletions_due_idx
    ON account_deletions (scheduled_purge_at)
    WHERE status = 'scheduled';

-- Worker sweep: "which pending-active rows can be advanced now?" — the
-- worker re-checks the user's active-delivery count for each row.
CREATE INDEX IF NOT EXISTS account_deletions_pending_idx
    ON account_deletions (user_id)
    WHERE status = 'pending_active_delivery';

-- Reverse-lookup: given an anonymized pseudonym from an order row, find
-- the deletion record that minted it. Used by ops / finance audits.
CREATE UNIQUE INDEX IF NOT EXISTS account_deletions_hash_uniq
    ON account_deletions (anonymized_user_hash);

-- ---------------------------------------------------------------------
-- Trigger: keep updated_at fresh (reuses set_updated_at() from 0001).
-- ---------------------------------------------------------------------
DROP TRIGGER IF EXISTS account_deletions_set_updated_at ON account_deletions;
CREATE TRIGGER account_deletions_set_updated_at
    BEFORE UPDATE ON account_deletions
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

INSERT INTO schema_migrations (version)
VALUES ('0010_init_account_deletions')
ON CONFLICT (version) DO NOTHING;

COMMIT;
