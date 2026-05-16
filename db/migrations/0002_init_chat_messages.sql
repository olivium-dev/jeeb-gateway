-- =====================================================================
-- Migration: 0002_init_chat_messages
-- Ticket:    T-database-004
-- Purpose:   Chat messages schema for the Jeeb conversational layer.
-- Notes:     Idempotent. media_url references object storage (S3-compatible);
--            never store inline binary content. A 90-day retention policy
--            is documented in db/CHAT_RETENTION.md and implemented by an
--            external reaper job (see that doc for ownership and cadence).
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Enum: chat_message_type
--   text       — plain text body in content
--   image      — image stored at media_url (object storage)
--   voice      — voice note stored at media_url (object storage)
--   location   — lat/lng payload in content (JSON)
--   system     — server-generated notice (e.g. "driver accepted"); sender_id NULL
--   offer_card — structured offer payload in content (JSON)
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'chat_message_type') THEN
        CREATE TYPE chat_message_type AS ENUM (
            'text',
            'image',
            'voice',
            'location',
            'system',
            'offer_card'
        );
    END IF;
END$$;

-- ---------------------------------------------------------------------
-- Table: chat_messages
--
-- thread_id is intentionally not FK-constrained in this migration —
-- chat_threads is owned by a separate ticket. A follow-up migration will
-- add the FK once that table lands.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS chat_messages (
    id            UUID              PRIMARY KEY DEFAULT gen_random_uuid(),
    thread_id     UUID              NOT NULL,
    sender_id     UUID              NULL REFERENCES users (id) ON DELETE SET NULL,
    message_type  chat_message_type NOT NULL,
    content       TEXT              NULL,
    -- media_url MUST point at object storage (e.g. S3 / R2 / MinIO).
    -- Never inline binary payloads in this column.
    media_url     TEXT              NULL,
    read_at       TIMESTAMPTZ       NULL,
    created_at    TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ       NOT NULL DEFAULT NOW(),

    -- A message must carry at least a content body or a media reference.
    CONSTRAINT chat_messages_content_or_media CHECK (
        content IS NOT NULL OR media_url IS NOT NULL
    ),
    -- System messages are server-emitted and have no human sender.
    CONSTRAINT chat_messages_system_no_sender CHECK (
        (message_type = 'system' AND sender_id IS NULL)
        OR (message_type <> 'system')
    ),
    -- Media-bearing types must reference object storage.
    CONSTRAINT chat_messages_media_required CHECK (
        message_type NOT IN ('image','voice') OR media_url IS NOT NULL
    ),
    -- media_url, when present, must look like an absolute URL (object storage).
    CONSTRAINT chat_messages_media_url_format CHECK (
        media_url IS NULL OR media_url ~ '^(https?|s3)://'
    )
);

-- ---------------------------------------------------------------------
-- Indexes
-- ---------------------------------------------------------------------
-- Primary read path: "load page N of messages in thread X, newest first".
CREATE INDEX IF NOT EXISTS chat_messages_thread_created
    ON chat_messages (thread_id, created_at DESC);

-- Sender activity lookups.
CREATE INDEX IF NOT EXISTS chat_messages_sender_idx
    ON chat_messages (sender_id) WHERE sender_id IS NOT NULL;

-- Unread badge / inbox counters.
CREATE INDEX IF NOT EXISTS chat_messages_unread_idx
    ON chat_messages (thread_id) WHERE read_at IS NULL;

-- Retention reaper scan: "delete where created_at < now() - 90 days".
CREATE INDEX IF NOT EXISTS chat_messages_created_at_idx
    ON chat_messages (created_at);

-- ---------------------------------------------------------------------
-- Trigger: keep updated_at fresh (reuses set_updated_at() from 0001).
-- ---------------------------------------------------------------------
DROP TRIGGER IF EXISTS chat_messages_set_updated_at ON chat_messages;
CREATE TRIGGER chat_messages_set_updated_at
    BEFORE UPDATE ON chat_messages
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- ---------------------------------------------------------------------
-- Retention helper (documented, not auto-scheduled).
-- The reaper job (cron/Oban/k8s CronJob) should execute:
--
--   DELETE FROM chat_messages
--   WHERE created_at < NOW() - INTERVAL '90 days';
--
-- See db/CHAT_RETENTION.md for ownership, cadence, and media tombstoning.
-- ---------------------------------------------------------------------

INSERT INTO schema_migrations (version)
VALUES ('0002_init_chat_messages')
ON CONFLICT (version) DO NOTHING;

COMMIT;
