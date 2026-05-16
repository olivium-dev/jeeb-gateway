-- =====================================================================
-- Migration: 0012_user_suspension
-- Ticket:    T-backend-030
-- Purpose:   Persist suspension state on the users row. The gateway
--            already records suspend/unsuspend actions in admin_actions
--            (0005); this migration adds the columns the gateway reads to
--            decide whether to short-circuit a Client/Jeeber mutation
--            with 403.
-- Notes:     Idempotent — uses ADD COLUMN IF NOT EXISTS / DROP CONSTRAINT
--            IF EXISTS so it can be re-applied. Reuses set_updated_at()
--            trigger from 0001 (already attached to users).
-- =====================================================================

BEGIN;

ALTER TABLE users
    ADD COLUMN IF NOT EXISTS is_suspended      BOOLEAN     NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS suspension_reason TEXT        NULL,
    ADD COLUMN IF NOT EXISTS suspended_at      TIMESTAMPTZ NULL,
    -- suspended_by is the admin user id at the time of suspension.
    -- ON DELETE SET NULL matches the policy used for created_by /
    -- updated_by elsewhere — we never lose the user row because an
    -- admin account was removed.
    ADD COLUMN IF NOT EXISTS suspended_by      UUID        NULL REFERENCES users (id) ON DELETE SET NULL;

-- An is_suspended=TRUE row must have a reason and a timestamp. Keeps the
-- column trio self-consistent so the gateway never has to defensively
-- check "suspended but reason missing".
ALTER TABLE users DROP CONSTRAINT IF EXISTS users_suspension_state_consistency;
ALTER TABLE users ADD CONSTRAINT users_suspension_state_consistency CHECK (
    (is_suspended = FALSE AND suspension_reason IS NULL AND suspended_at IS NULL AND suspended_by IS NULL)
    OR
    (is_suspended = TRUE  AND suspension_reason IS NOT NULL AND suspended_at IS NOT NULL)
);

-- Partial index covers the common "list currently suspended users"
-- admin query; keeps the index small (no row written until someone
-- is suspended).
CREATE INDEX IF NOT EXISTS users_is_suspended_idx
    ON users (is_suspended) WHERE is_suspended = TRUE;

INSERT INTO schema_migrations (version)
VALUES ('0012_user_suspension')
ON CONFLICT (version) DO NOTHING;

COMMIT;
