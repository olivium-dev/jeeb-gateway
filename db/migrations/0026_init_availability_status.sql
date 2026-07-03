-- =====================================================================
-- Migration: 0026_init_availability_status
-- Ticket:    T-backend-023 / T-backend-051 (jeeb-gateway durability
--            hardening — PostgresAvailabilityStore)
-- Purpose:   PostgresAvailabilityStore (src/JeebGateway/Availability/
--            PostgresAvailabilityStore.cs) replaces InMemoryAvailabilityStore
--            in production. It REUSES the EXISTING jeeber_availability
--            table (migration 0003, PostGIS) rather than creating a new
--            one — is_online, vehicle_type, last_location, and
--            last_seen_at are all reused as-is. This migration ALTERs
--            that table to add the two columns 0003 never carried, which
--            the gateway's JeeberAvailability model (Availability/
--            JeeberAvailability.cs) needs in order to round-trip without
--            losing state across a gateway restart:
--
--              * zone                — the free-text zone key from the
--                                       go-online request
--                                       (GoOnlineRequest.Zone). Echoed
--                                       back on every read; NULL for a
--                                       Jeeber who has never gone online.
--              * last_interaction_at — the auto-offline sweeper's
--                                       activity watermark
--                                       (T-backend-023): the most recent
--                                       go-online / GPS heartbeat /
--                                       GET-availability poll. Distinct
--                                       from last_seen_at, which only
--                                       advances on an actual location
--                                       fix (see AutoOfflineSweeper.cs:
--                                       "LastInteractionAt ?? LastSeenAt").
--
-- Notes:     Idempotent — ADD COLUMN IF NOT EXISTS / CREATE INDEX IF NOT
--            EXISTS, wrapped in BEGIN/COMMIT. Does NOT re-CREATE
--            jeeber_availability: a second CREATE TABLE IF NOT EXISTS
--            against an already-existing table silently no-ops and these
--            columns would never land on the real table.
--
--            Deliberately does NOT add a `status` column. The admin
--            ops-map / auto-offline sweeper's "list online Jeebers" read
--            (PostgresAvailabilityStore.ListOnlineAsync) is expressed as
--            `WHERE is_online = TRUE` — reusing the SAME boolean the two
--            0003 partial indexes below are already built on
--            (jeeber_availability_online_vehicle_idx,
--            jeeber_availability_last_seen_idx) — rather than introducing
--            a second, competing online/offline representation on a
--            table that could drift from is_online.
-- Refs:      0003_init_jeeber_availability.sql (base table + PostGIS).
-- =====================================================================

BEGIN;

ALTER TABLE jeeber_availability
    ADD COLUMN IF NOT EXISTS zone                TEXT        NULL,
    ADD COLUMN IF NOT EXISTS last_interaction_at  TIMESTAMPTZ NULL;

-- ---------------------------------------------------------------------
-- Index: auto-offline sweeper scan
--
-- Mirrors the 0003 "stale-online sweeper scan" index
-- (jeeber_availability_last_seen_idx) for the same purpose, but keyed on
-- the watermark AutoOfflineSweeper actually reads first
-- (LastInteractionAt ?? LastSeenAt). Partial on is_online = TRUE keeps it
-- small — only the online subset is ever swept.
-- ---------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS jeeber_availability_last_interaction_idx
    ON jeeber_availability (last_interaction_at) WHERE is_online = TRUE;

INSERT INTO schema_migrations (version)
VALUES ('0026_init_availability_status')
ON CONFLICT (version) DO NOTHING;

COMMIT;
