-- =====================================================================
-- Migration: 0018_init_prohibited_item_acks
-- Ticket:    jeeb-gateway durability hardening (PostgresProhibitedItemsStore)
-- Purpose:   Durable backing for IProhibitedItemsStore:
--              * prohibited_items — ALTERed (NEVER re-created; owned by
--                migration 0005) to add the `severity` column (JEB-63).
--              * prohibited_item_acks — NEW child table backing
--                AcknowledgeAsync / GetAcknowledgmentAsync.
-- Notes:     Idempotent. Safe to re-run.
--
--            ── Why `severity` needs an ALTER ──
--            ProhibitedItem.Severity / ProhibitedItemCreate.Severity (JEB-63)
--            have existed at the C# application layer since JEB-63 shipped,
--            but migration 0005 (and the 0011 seed INSERTs, which only set
--            name/category/description/active) never carried a column for
--            it — InMemoryProhibitedItemsStore was the only place the value
--            actually persisted. Backfilled to 'block' for every existing
--            row, matching ProhibitedItem.Severity's C# default exactly, so
--            no pre-existing catalog entry silently gets a laxer
--            classification after this migration runs.
--
--            ── Why prohibited_item_acks is NOT keyed by "item_id (fk/uuid)" ──
--            The application's ack contract (IProhibitedItemsStore.
--            AcknowledgeAsync/GetAcknowledgmentAsync, the UserAcknowledgment
--            model) is "user acknowledged the ACTIVE LEXICON AS OF VERSION V",
--            NOT "user acknowledged catalog item X". V comes from
--            ModerationGate.ComputeLexiconVersion / ProhibitedItemsController.
--            ComputeVersion — the max updated_at across the ACTIVE set,
--            rendered "O"-format (e.g. '2026-07-03T12:34:56.7890123Z'), or the
--            literal string 'empty' when the catalog is empty. That token is
--            never a single row's id and can never be cast to uuid, so a
--            literal `item_id UUID REFERENCES prohibited_items(id)` column
--            would reject every AcknowledgeAsync call at the type level. This
--            migration keeps the requested composite-PRIMARY-KEY(user,
--            acknowledged-entity) shape but names/types the second column for
--            what it actually holds: `lexicon_version TEXT`. See
--            PostgresProhibitedItemsStore's class remarks for the full
--            rationale.
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Extensions guaranteed by 0001 (pgcrypto for gen_random_uuid()); declared
-- here so this migration is also valid when applied in isolation.
-- ---------------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ---------------------------------------------------------------------
-- 1. prohibited_items — ALTER only. Table + its name-uniqueness index are
--    owned by migration 0005; this migration must never re-CREATE it (a
--    second CREATE TABLE IF NOT EXISTS would silently no-op and the new
--    column would never land on an already-migrated database).
-- ---------------------------------------------------------------------
ALTER TABLE prohibited_items
    ADD COLUMN IF NOT EXISTS severity TEXT NOT NULL DEFAULT 'block';

-- ADD CONSTRAINT has no IF NOT EXISTS in Postgres; guard via pg_constraint
-- so this migration stays safe to re-run (same idiom 0005 uses via pg_type
-- for the notification_category enum).
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'prohibited_items_severity_format'
    ) THEN
        ALTER TABLE prohibited_items
            ADD CONSTRAINT prohibited_items_severity_format
            CHECK (severity IN ('warn', 'block'));
    END IF;
END$$;

-- =====================================================================
-- 2. prohibited_item_acks  (NEW child table)
--
-- One row per (user, lexicon-version-they-acknowledged). GetAcknowledgmentAsync
-- reads the newest row for a user (ORDER BY acknowledged_at DESC LIMIT 1),
-- reproducing InMemoryProhibitedItemsStore's "last ack wins" read exactly.
-- Retaining prior-version ack rows (rather than overwriting in place) is a
-- strict superset of that in-memory behaviour — nothing the application
-- reads today is lost, and the history is available for future audit needs.
--
-- AcknowledgeAsync is an upsert on (user_id, lexicon_version): re-acking the
-- SAME version twice (double-tap / client retry) is idempotent and only
-- refreshes acknowledged_at, matching the ON CONFLICT ... DO UPDATE
-- idempotency shape used elsewhere in this schema (e.g. device_tokens).
--
-- No FK to prohibited_items: the ack target is a derived multi-row snapshot
-- token (see header), not any single catalog row's id.
-- =====================================================================
CREATE TABLE IF NOT EXISTS prohibited_item_acks (
    user_id         TEXT        NOT NULL,
    lexicon_version TEXT        NOT NULL,
    acknowledged_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    PRIMARY KEY (user_id, lexicon_version)
);

-- GetAcknowledgmentAsync's read path: "newest ack for user X". The PK
-- already covers (user_id, lexicon_version) equality lookups; this index
-- serves the ORDER BY acknowledged_at DESC LIMIT 1 scan.
CREATE INDEX IF NOT EXISTS prohibited_item_acks_user_latest_idx
    ON prohibited_item_acks (user_id, acknowledged_at DESC);

INSERT INTO schema_migrations (version)
VALUES ('0018_init_prohibited_item_acks')
ON CONFLICT (version) DO NOTHING;

COMMIT;
