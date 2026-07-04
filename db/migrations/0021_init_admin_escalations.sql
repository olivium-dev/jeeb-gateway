-- =====================================================================
-- Migration: 0021_init_admin_escalations
-- Ticket:    T-backend-015 / JEEB-33 (jeeb-gateway durability hardening)
-- Purpose:   Durable admin_escalations table backing
--            PostgresAdminEscalationStore (IAdminEscalationStore).
--            Replaces the in-memory OTP-handover / client-unreachable
--            escalation queue (InMemoryAdminEscalationStore) so rows
--            survive a gateway bounce and are visible across replicas.
-- Notes:     Idempotent. NEW table — admin_escalations is distinct from
--            the generic admin_actions audit log created in migration
--            0005; the two are not related and this migration does not
--            touch admin_actions.
--            No UNIQUE(delivery_id): the store deliberately does not
--            enforce per-delivery uniqueness — callers
--            (InMemoryRequestsStore.TryVerifyOtpAsync / OtpHandoverSweeper)
--            rely on the write-once DeliveryRequest.OtpEscalationId field
--            for that instead (see IAdminEscalationStore.CreateAsync xmldoc).
--            updated_at has no BEFORE UPDATE trigger — no code path
--            updates a row after insert today, so the column simply
--            carries its NOW() default forward (kept for parity with the
--            other durable tables and to leave room for a future
--            admin-resolve endpoint without another migration).
-- Refs:      T-backend-015 (OTP handover), JEEB-33.
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Extensions guaranteed by 0001 (pgcrypto for gen_random_uuid()); declared
-- here so this migration is also valid when applied in isolation.
-- ---------------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ---------------------------------------------------------------------
-- Table: admin_escalations
--
-- One row per opened escalation (OTP lockout / client-unreachable — see
-- the application-layer EscalationReason constants in
-- JeebGateway.Requests.OtpHandover). jeeber_id is nullable because a
-- delivery can be escalated before a jeeber has been matched.
-- reason/status are free-form TEXT (mirroring the EscalationReason /
-- EscalationStatus string constants) rather than a DB enum so a new
-- reason or status ships without a migration.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS admin_escalations (
    id                 UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    delivery_id        TEXT        NOT NULL,
    client_id          TEXT        NOT NULL,
    jeeber_id          TEXT        NULL,
    reason             TEXT        NOT NULL,
    status             TEXT        NOT NULL,
    otp_attempt_count  INT         NOT NULL DEFAULT 0,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- GetForDeliveryAsync(deliveryId, reason) lookup + the sweeper's
-- per-delivery escalation checks. ListAsync() (admin triage queue) is an
-- intentional full scan at this table's expected low volume — no
-- created_at index is added for it.
CREATE INDEX IF NOT EXISTS idx_admin_escalations_delivery_id
    ON admin_escalations (delivery_id);

INSERT INTO schema_migrations (version)
VALUES ('0021_init_admin_escalations')
ON CONFLICT (version) DO NOTHING;

COMMIT;
