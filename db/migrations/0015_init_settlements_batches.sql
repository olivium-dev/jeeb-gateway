-- JEB-56/JEB-57: additive migration — durable settlement ledger + batch store
-- Applied as an explicit deploy-pipeline step (never auto-run at startup).
-- All changes are additive (no DROP, no ALTER COLUMN TYPE).
-- ============================================================================

-- ---------------------------------------------------------------------------
-- settlement_batches: one row per (jeeber_id, period_start)
-- Created by the weekly cron via WeeklySettlementBatch; admin marks paid via
-- POST /v1/admin/settlements/batches/{id}/mark-paid.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS settlement_batches (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    jeeber_id            TEXT NOT NULL,
    period_start         DATE NOT NULL,
    period_end           DATE NOT NULL,
    total_gross_usd      NUMERIC(20,4) NOT NULL DEFAULT 0,
    total_commission_usd NUMERIC(20,4) NOT NULL DEFAULT 0,
    total_net_usd        NUMERIC(20,4) NOT NULL DEFAULT 0,
    settlement_count     INT NOT NULL DEFAULT 0,
    currency             TEXT NOT NULL DEFAULT 'USD',
    status               TEXT NOT NULL DEFAULT 'open',  -- open | closed | paid
    paid_at              TIMESTAMPTZ,
    paid_by              TEXT,                           -- admin userId who marked paid
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT uq_settlement_batches_jeeber_period UNIQUE (jeeber_id, period_start)
);

CREATE INDEX IF NOT EXISTS idx_settlement_batches_status
    ON settlement_batches(status);

CREATE INDEX IF NOT EXISTS idx_settlement_batches_jeeber_id
    ON settlement_batches(jeeber_id);

CREATE INDEX IF NOT EXISTS idx_settlement_batches_period
    ON settlement_batches(period_start, period_end);

-- ---------------------------------------------------------------------------
-- settlements: the durable COD settlement ledger.
-- One row per delivery — UNIQUE(delivery_id) enforces idempotency.
-- States: recorded → batched → paid
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS settlements (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    delivery_id          TEXT NOT NULL,
    jeeber_id            TEXT NOT NULL,
    client_id            TEXT NOT NULL,
    tier_id              TEXT NOT NULL DEFAULT '',
    goods_cost           NUMERIC(20,4) NOT NULL,
    commission_rate      NUMERIC(6,4) NOT NULL,
    commission           NUMERIC(20,4) NOT NULL,
    insurance            NUMERIC(20,4) NOT NULL,
    total                NUMERIC(20,4) NOT NULL,
    min_fee_applied      BOOLEAN NOT NULL DEFAULT false,
    currency             TEXT NOT NULL DEFAULT 'USD',
    payment_method       TEXT NOT NULL DEFAULT 'cash',
    state                TEXT NOT NULL DEFAULT 'pending_settlement',  -- pending_settlement | settled | receipt_generated
    cod_state            TEXT NOT NULL DEFAULT 'recorded',            -- recorded | batched | paid
    settled_at           TIMESTAMPTZ NOT NULL,
    receipt_generated_at TIMESTAMPTZ,
    ledger_entry_id      TEXT,                              -- UPG external_ref (flag-ON path)
    batch_id             UUID REFERENCES settlement_batches(id),
    batched_at           TIMESTAMPTZ,
    paid_at              TIMESTAMPTZ,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT uq_settlements_delivery_id UNIQUE (delivery_id)
);

-- Primary earnings query: jeeber + state + time range
CREATE INDEX IF NOT EXISTS idx_settlements_jeeber_state_settled
    ON settlements(jeeber_id, state, settled_at);

-- Batch window sweep: find all pending settlements for batching
CREATE INDEX IF NOT EXISTS idx_settlements_state_settled
    ON settlements(state, settled_at)
    WHERE state = 'recorded';

-- Admin reporting: filter by paid_at range
CREATE INDEX IF NOT EXISTS idx_settlements_paid_at
    ON settlements(paid_at)
    WHERE paid_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_settlements_batch_id
    ON settlements(batch_id)
    WHERE batch_id IS NOT NULL;
