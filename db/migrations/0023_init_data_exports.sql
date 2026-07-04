-- =====================================================================
-- Migration: 0023_init_data_exports
-- Ticket:    T-backend-042 (jeeb-gateway durability hardening)
-- Purpose:   Durable data_exports table backing PostgresDataExportStore
--            (IDataExportStore). Replaces the in-memory GDPR-like
--            right-of-access export queue (InMemoryDataExportStore) so a
--            queued/processing/ready row — and the packaged payload bytes
--            behind its single-use download link — survives a gateway
--            bounce or a replica move instead of silently evaporating
--            mid-SLA.
-- Notes:     Idempotent. NEW table — data_exports is not one of the
--            already-existing durable tables (account_deletions=0010,
--            prohibited_items/admin_actions=0005, saved_addresses=0006,
--            delivery_requests=0004, jeeber_availability=0003, users=0001,
--            ratings/disputes=0009), so this migration only CREATEs it;
--            it never re-CREATEs an existing table.
--
--            Column notes:
--              * status is free-form TEXT mirroring the DataExportStatus
--                string constants (queued/processing/ready/delivered/
--                expired/failed) rather than a DB enum, so a new status
--                ships without a migration (same convention as
--                admin_escalations.status / settlements.state).
--              * due_by is the 72-hour SLA deadline, stamped at queue
--                time (now + DataExportOptions.Sla) rather than derived
--                from requested_at at read time, so the deadline is
--                stable even if the configured SLA changes later. It is
--                also what DataExportWorker's SLA sweep scans on.
--              * completed_at / expires_at hold the ready-at and
--                download-link-expiry timestamps respectively.
--              * token_used is an explicit single-use guard alongside the
--                ready -> delivered status transition: PostgresDataExportStore
--                flips it TRUE in the very same UPDATE that marks a row
--                delivered (status = 'ready' in the WHERE clause), so a
--                replayed/raced download request against the same token
--                simply matches zero rows on the second attempt.
--              * payload is the packaged export bytes (BYTEA) — present
--                only while the row is `ready`; cleared (not the row)
--                on delivery/failure so PII is not warehoused past
--                hand-off, matching InMemoryDataExportStore's payload
--                lifecycle exactly.
--              * a partial UNIQUE index on (user_id) WHERE status IN the
--                open states is the idempotency device: RequestAsync
--                does INSERT ... ON CONFLICT (user_id) WHERE <same
--                predicate> DO NOTHING, mirroring the UNIQUE(delivery_id)
--                + ON CONFLICT DO NOTHING idempotency shape in
--                0015_init_settlements_batches.sql / PostgresSettlementStore,
--                so a retried POST /users/me/data-export never double-queues.
-- Refs:      T-backend-042, PostgresSettlementStore (idempotency shape).
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Extensions guaranteed by 0001 (pgcrypto for gen_random_uuid()); declared
-- here so this migration is also valid when applied in isolation.
-- ---------------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ---------------------------------------------------------------------
-- Table: data_exports
--
-- One row per data-export request (T-backend-042, GDPR-like right of
-- access). Lifecycle: queued -> processing -> ready -> delivered, with
-- failed / expired as the other terminal states — see
-- JeebGateway.Users.DataExport.DataExportStatus for the authoritative
-- vocabulary this column's values are drawn from.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS data_exports (
    id                   UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id              TEXT        NOT NULL,
    status               TEXT        NOT NULL DEFAULT 'queued',
    format               TEXT        NOT NULL DEFAULT 'json',
    requested_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    due_by               TIMESTAMPTZ NOT NULL,
    started_at           TIMESTAMPTZ NULL,
    -- "Completed" = packaging finished and the row moved to `ready`.
    completed_at         TIMESTAMPTZ NULL,
    delivered_at         TIMESTAMPTZ NULL,
    failed_at            TIMESTAMPTZ NULL,
    failure_reason       TEXT        NULL,
    download_token       TEXT        NULL,
    token_used           BOOLEAN     NOT NULL DEFAULT FALSE,
    -- Download-link validity deadline (independent of due_by / the SLA).
    expires_at           TIMESTAMPTZ NULL,
    payload              BYTEA       NULL,
    payload_content_type TEXT        NULL,
    payload_size_bytes   BIGINT      NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Idempotency device for RequestAsync: at most one OPEN export per user.
-- INSERT ... ON CONFLICT (user_id) WHERE status IN ('queued','processing','ready')
-- DO NOTHING relies on this exact partial index as its arbiter, mirroring the
-- UNIQUE(delivery_id) + ON CONFLICT DO NOTHING idempotency shape in
-- PostgresSettlementStore / 0015_init_settlements_batches.sql.
CREATE UNIQUE INDEX IF NOT EXISTS uq_data_exports_user_open
    ON data_exports (user_id)
    WHERE status IN ('queued', 'processing', 'ready');

-- GetLatestForUserAsync: newest row (any status) for a user.
CREATE INDEX IF NOT EXISTS idx_data_exports_user_requested
    ON data_exports (user_id, requested_at DESC);

-- ClaimNextAsync: oldest queued row, claimed via a
-- `FOR UPDATE SKIP LOCKED` CTE so concurrent gateway replicas never claim
-- the same row twice.
CREATE INDEX IF NOT EXISTS idx_data_exports_queued_requested
    ON data_exports (requested_at)
    WHERE status = 'queued';

-- GetByDownloadTokenAsync: single-use capability-URL lookup.
CREATE UNIQUE INDEX IF NOT EXISTS uq_data_exports_download_token
    ON data_exports (download_token)
    WHERE download_token IS NOT NULL;

-- DataExportWorker SLA sweep: open (queued/processing) rows whose 72h
-- deadline has passed.
CREATE INDEX IF NOT EXISTS idx_data_exports_open_due_by
    ON data_exports (due_by)
    WHERE status IN ('queued', 'processing');

INSERT INTO schema_migrations (version)
VALUES ('0023_init_data_exports')
ON CONFLICT (version) DO NOTHING;

COMMIT;
