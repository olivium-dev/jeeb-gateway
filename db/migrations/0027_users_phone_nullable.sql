-- =====================================================================
-- Migration: 0027_users_phone_nullable
-- Ticket:    gateway-durability (F1 — phone-less UM profile projection)
-- Purpose:   Let the durable users projection persist a phone-LESS UM
--            profile. The 0001 schema declared `phone VARCHAR(20) NOT NULL`
--            with CHECK (phone ~ '^\+?[0-9]{7,15}$'). The email-login,
--            super-login and UM-cold-hydration projection paths carry NO
--            phone (they bind the empty string), which satisfies NOT NULL
--            but FAILS the regex CHECK — so the INSERT threw and those
--            users' durable projection never persisted (evaporated on
--            restart; admin user-search + token-mint active_role read cold).
--
--            Fix (gateway-owned Postgres only): drop the NOT NULL on
--            users.phone. A NULL phone trivially SATISFIES the regex CHECK
--            (a CHECK passes when its predicate is NULL/unknown) and is
--            distinct under the users_phone_uniq UNIQUE index (SQL NULLs do
--            not collide), so multiple phone-less users coexist. The
--            projection store now binds NULL (not '') on the INSERT and the
--            existing blank-preserving COALESCE on the ON CONFLICT path
--            backfills the real phone on a later phone-OTP login.
--
-- Notes:     Idempotent. `ALTER COLUMN ... DROP NOT NULL` is a no-op when the
--            column is already nullable; guarded in a DO block so a re-run is
--            silent. The regex CHECK is intentionally LEFT IN PLACE — it still
--            rejects a malformed non-empty phone; it just also admits NULL.
-- =====================================================================

BEGIN;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'users'
          AND column_name = 'phone'
          AND is_nullable = 'NO'
    ) THEN
        ALTER TABLE users ALTER COLUMN phone DROP NOT NULL;
    END IF;
END$$;

INSERT INTO schema_migrations (version)
VALUES ('0027_users_phone_nullable')
ON CONFLICT (version) DO NOTHING;

COMMIT;
