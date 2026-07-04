-- =====================================================================
-- Migration: 0017_init_device_tokens
-- Ticket:    T-backend-022 durability follow-up
-- Purpose:   Durable backing store for push device-token registrations.
--            Replaces InMemoryDeviceTokenStore (Push/IDeviceTokenStore.cs),
--            whose rows evaporated on every gateway restart / replica move —
--            silently dropping push delivery for every previously
--            registered device until the app happened to re-register.
-- Notes:     Idempotent — CREATE TABLE/INDEX IF NOT EXISTS, safe to re-run.
--            UNIQUE(user_id, token) is both the natural registration
--            idempotency key (the same device re-registering upserts its
--            one row rather than accumulating duplicates) and the
--            ON CONFLICT target PostgresDeviceTokenStore.RegisterAsync
--            upserts against. Unregister is a soft-delete (revoked_at
--            stamped, row retained) so revocation history survives and a
--            reused token string can't silently reappear as "always was
--            active"; GetForUserAsync only ever surfaces non-revoked rows,
--            matching the in-memory store's semantics of returning
--            whatever wasn't removed.
-- =====================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS device_tokens (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     TEXT        NOT NULL,
    token       TEXT        NOT NULL,
    platform    TEXT        NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    revoked_at  TIMESTAMPTZ NULL,

    CONSTRAINT uq_device_tokens_user_token UNIQUE (user_id, token)
);

-- Primary read path: PushNotificationService.SendInternalAsync resolves a
-- user's active (non-revoked) devices on every push fan-out.
CREATE INDEX IF NOT EXISTS idx_device_tokens_user_active
    ON device_tokens (user_id)
    WHERE revoked_at IS NULL;

INSERT INTO schema_migrations (version)
VALUES ('0017_init_device_tokens')
ON CONFLICT (version) DO NOTHING;

COMMIT;
