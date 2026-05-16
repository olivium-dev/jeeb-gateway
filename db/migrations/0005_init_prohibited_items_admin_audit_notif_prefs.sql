-- =====================================================================
-- Migration: 0005_init_prohibited_items_admin_audit_notif_prefs
-- Ticket:    T-database-008
-- Purpose:   Admin-config + per-user notification toggles:
--              * prohibited_items    — moderated catalog of disallowed
--                                      items with admin CRUD support
--              * admin_actions       — append-only audit log of every
--                                      admin mutation across the system
--              * notification_preferences
--                                    — per-user per-category opt-in flags
-- Notes:     Idempotent. Reuses set_updated_at() from 0001.
--            Audit log is append-only; no UPDATE/DELETE path is provided
--            and the application is expected to INSERT only.
-- Refs:      FR-prohibited-items (admin moderation), FR-audit-trail
--            (admin accountability), FR-notifications (per-category
--            user toggles).
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Extensions guaranteed by 0001 (pgcrypto for gen_random_uuid()); declared
-- here so this migration is also valid when applied in isolation.
-- ---------------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- =====================================================================
-- 1. prohibited_items
--
-- The moderated catalog of items that may not be sent through Jeeb
-- (e.g. weapons, drugs, hazardous materials). Admins manage this list
-- through the jeeb-admin microfrontend; the client app reads the active
-- subset when composing a request so it can warn the user pre-submit.
--
-- `name` is the canonical user-facing label; uniqueness is enforced
-- case-insensitively so "Knife" and "knife" can't both be live at once.
-- `category` is free-form text (kept as TEXT, not an enum, so legal/ops
-- can add categories without a migration) but constrained to a slug
-- shape so it's safe in URLs and grouping.
-- `active = FALSE` is preferred over hard delete so the audit log keeps
-- a stable foreign-key anchor.
-- =====================================================================
CREATE TABLE IF NOT EXISTS prohibited_items (
    id           UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    name         TEXT         NOT NULL,
    category     TEXT         NOT NULL,
    description  TEXT         NULL,
    active       BOOLEAN      NOT NULL DEFAULT TRUE,
    created_by   UUID         NULL REFERENCES users (id) ON DELETE SET NULL,
    updated_by   UUID         NULL REFERENCES users (id) ON DELETE SET NULL,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT prohibited_items_name_nonblank
        CHECK (char_length(btrim(name)) > 0),
    CONSTRAINT prohibited_items_category_format
        CHECK (category ~ '^[a-z][a-z0-9_]{1,47}$')
);

-- Case-insensitive uniqueness on name keeps the catalog clean.
CREATE UNIQUE INDEX IF NOT EXISTS prohibited_items_name_lower_uniq
    ON prohibited_items (LOWER(name));

-- Client read path: "give me the active list, grouped by category".
CREATE INDEX IF NOT EXISTS prohibited_items_category_active_idx
    ON prohibited_items (category) WHERE active = TRUE;

CREATE INDEX IF NOT EXISTS prohibited_items_active_idx
    ON prohibited_items (active);

DROP TRIGGER IF EXISTS prohibited_items_set_updated_at ON prohibited_items;
CREATE TRIGGER prohibited_items_set_updated_at
    BEFORE UPDATE ON prohibited_items
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- =====================================================================
-- 2. admin_actions  (audit log)
--
-- One row per admin mutation. The shape is deliberately generic so any
-- service performing an admin-authenticated write can emit an audit row
-- without coupling to a per-entity table.
--
--   action       — verb (create/update/delete/approve/reject/...)
--   entity_type  — string discriminator (e.g. 'prohibited_item',
--                  'kyc_submission', 'delivery_request')
--   entity_id    — UUID of the touched row when available; nullable so
--                  bulk or non-row actions (e.g. "rotated keys") can be
--                  logged too
--   before/after — JSONB snapshots; let dashboards diff state without
--                  joining back to the source table (which may have moved on)
--
-- This table is INSERT-only by convention. There is no updated_at and no
-- update trigger; tampering protection is left to backups + row-level
-- access control at the application layer.
-- =====================================================================
CREATE TABLE IF NOT EXISTS admin_actions (
    id            UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    admin_user_id UUID         NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    action        TEXT         NOT NULL,
    entity_type   TEXT         NOT NULL,
    entity_id     UUID         NULL,
    before_state  JSONB        NULL,
    after_state   JSONB        NULL,
    -- INET handles both IPv4 and IPv6; nullable because some admin
    -- mutations originate inside the cluster (cron, system jobs).
    ip_address    INET         NULL,
    user_agent    TEXT         NULL,
    request_id    TEXT         NULL,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT admin_actions_action_format
        CHECK (action ~ '^[a-z][a-z0-9_]{1,47}$'),
    CONSTRAINT admin_actions_entity_type_format
        CHECK (entity_type ~ '^[a-z][a-z0-9_]{1,47}$'),
    CONSTRAINT admin_actions_before_is_object
        CHECK (before_state IS NULL OR jsonb_typeof(before_state) = 'object'),
    CONSTRAINT admin_actions_after_is_object
        CHECK (after_state IS NULL OR jsonb_typeof(after_state) = 'object')
);

-- Admin activity timeline: "what did admin X do, newest first".
CREATE INDEX IF NOT EXISTS admin_actions_admin_created_idx
    ON admin_actions (admin_user_id, created_at DESC);

-- Per-entity history: "show me every admin action on row Y".
CREATE INDEX IF NOT EXISTS admin_actions_entity_idx
    ON admin_actions (entity_type, entity_id, created_at DESC)
    WHERE entity_id IS NOT NULL;

-- Global audit scan / time-range queries.
CREATE INDEX IF NOT EXISTS admin_actions_created_at_idx
    ON admin_actions (created_at DESC);

-- =====================================================================
-- 3. notification_preferences  (per-user per-category)
--
-- One row per (user, category) pair. The wide-format toggles used by the
-- in-memory store today are a presentation concern; the storage shape is
-- normalised so adding a new category is an INSERT, not an ALTER TABLE.
--
-- Critical channels — `otp` and `system_critical` — are NOT modelled here.
-- They are always-on and live in
-- NotificationPreferencesDefaults.AlwaysOnChannels at the application
-- layer; the API rejects any PATCH that tries to disable them.
--
-- The category enum lists user-toggleable categories only. Add values
-- via ALTER TYPE in a follow-up migration; never reorder.
-- =====================================================================
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'notification_category') THEN
        CREATE TYPE notification_category AS ENUM (
            'offers',
            'chat',
            'status_changes',
            'rating_reminders'
        );
    END IF;
END$$;

CREATE TABLE IF NOT EXISTS notification_preferences (
    user_id     UUID                  NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    category    notification_category NOT NULL,
    enabled     BOOLEAN               NOT NULL DEFAULT TRUE,
    created_at  TIMESTAMPTZ           NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ           NOT NULL DEFAULT NOW(),

    PRIMARY KEY (user_id, category)
);

-- Per-user fetch: "load all categories for user X" hits the PK already,
-- but an explicit index on user_id helps planners on JOINs / FK checks
-- when the table grows.
CREATE INDEX IF NOT EXISTS notification_preferences_user_idx
    ON notification_preferences (user_id);

-- Reverse lookup: "every user who opted out of offers" for marketing
-- segmentation / fanout filters.
CREATE INDEX IF NOT EXISTS notification_preferences_category_disabled_idx
    ON notification_preferences (category) WHERE enabled = FALSE;

DROP TRIGGER IF EXISTS notification_preferences_set_updated_at ON notification_preferences;
CREATE TRIGGER notification_preferences_set_updated_at
    BEFORE UPDATE ON notification_preferences
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

INSERT INTO schema_migrations (version)
VALUES ('0005_init_prohibited_items_admin_audit_notif_prefs')
ON CONFLICT (version) DO NOTHING;

COMMIT;
