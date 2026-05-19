-- =====================================================================
-- Migration: 0014_cancellation_log
-- Ticket:    T-BE-030 (JEB-66)
-- Purpose:   Persist the per-user cancellation tally that backs the v1
--            cancellation policy (POST /v1/deliveries/{id}/cancel):
--              * client soft-limit (3/week) + hard-limit (5/week)
--              * jeeber strike accumulation (3/30d → 7-day role suspension)
--              * fee-applied audit trail (unified_payment_gateway COD Jeeb)
-- Notes:     Additive (no breaking change to existing tables). The MVP
--            gateway runs against InMemoryCancellationLogStore; this
--            migration is what the Postgres-backed swap reads from once
--            the BFF aggregation lands.
-- =====================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS cancellation_log (
    id                BIGSERIAL    PRIMARY KEY,
    -- user_id is a soft FK to users(id). NOT REFERENCED — the cancellation
    -- log must survive a GDPR-anonymised user (T-backend-035 rewrites the
    -- user row but preserves the historical activity trail).
    user_id           UUID         NOT NULL,
    -- 'client' or 'jeeber'. Distinct from the JWT role claim values
    -- (customer/driver) — see CancellationRoles in the gateway source.
    role              TEXT         NOT NULL
        CHECK (role IN ('client', 'jeeber')),
    delivery_id       UUID         NOT NULL,
    occurred_at       TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    fee_applied       BOOLEAN      NOT NULL DEFAULT FALSE,
    fee_amount        NUMERIC(20,4) NOT NULL DEFAULT 0,
    fee_currency      TEXT         NOT NULL DEFAULT 'LBP',
    -- Idempotency key shipped to unified_payment_gateway. Null when no
    -- fee was posted (jeeber cancels never carry a fee; client cancels
    -- below the soft limit also skip the fee).
    fee_idempotency_key TEXT       NULL,
    strike_issued     BOOLEAN      NOT NULL DEFAULT FALSE,
    reason            TEXT         NULL,
    created_at        TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- Per-(user, week) tally read by the client soft/hard limit guard. The
-- expression index on date_trunc('week', occurred_at AT TIME ZONE 'UTC')
-- backs the WHERE used by CountClientCancellationsInWeekAsync without a
-- table scan once the table grows.
CREATE INDEX IF NOT EXISTS cancellation_log_client_week_idx
    ON cancellation_log (user_id, role,
                         date_trunc('week', occurred_at AT TIME ZONE 'UTC'))
    WHERE role = 'client';

-- Rolling 30-day jeeber-strike scan. Partial because we never query the
-- client rows on this path.
CREATE INDEX IF NOT EXISTS cancellation_log_jeeber_strikes_idx
    ON cancellation_log (user_id, occurred_at DESC)
    WHERE role = 'jeeber' AND strike_issued = TRUE;

-- Fee-posting audit trail — surfaced in the admin finance dashboard
-- (T-backend-033) and used to reconcile against unified_payment_gateway.
CREATE INDEX IF NOT EXISTS cancellation_log_fee_audit_idx
    ON cancellation_log (fee_idempotency_key)
    WHERE fee_idempotency_key IS NOT NULL;

INSERT INTO schema_migrations (version)
VALUES ('0014_cancellation_log')
ON CONFLICT (version) DO NOTHING;

COMMIT;
