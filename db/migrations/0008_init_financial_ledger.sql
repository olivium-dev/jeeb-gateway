-- =====================================================================
-- Migration: 0008_init_financial_ledger
-- Ticket:    T-database-006
-- Purpose:   Financial ledger schema — per-delivery financials and the
--            weekly settlement batches that aggregate Jeeber commission
--            for payout. Backs the earnings, commission, and payout
--            reporting paths (FR-12.*, FR-14.*).
-- Notes:     Idempotent. Reuses set_updated_at() from 0001. Money is
--            NUMERIC(12,2) throughout; commission_rate is NUMERIC(5,4)
--            (fraction, e.g. 0.1000 = 10%) — matches delivery_tiers.
--            Settlement-state transitions are enforced at the application
--            layer; enums here constrain the value domain only.
-- Refs:      FR-12.* (earnings dashboard), FR-14.* (payouts),
--            T-database-002 (delivery_tiers), T-database-006.
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Enum: payout_method
--
--   bank_transfer  — IBAN / domestic bank rail
--   mobile_wallet  — e.g. NEC pay, regional mobile money
--   cash           — manual cash payout (admin-confirmed)
--
-- Add new values via ALTER TYPE in a follow-up migration; never reorder.
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'payout_method') THEN
        CREATE TYPE payout_method AS ENUM (
            'bank_transfer',
            'mobile_wallet',
            'cash'
        );
    END IF;
END$$;

-- ---------------------------------------------------------------------
-- Enum: settlement_status
--
--   pending     — batch created; awaiting payout run
--   processing  — payout submitted to unified_payment_gateway
--   paid        — gateway confirmed funds delivered      (terminal)
--   failed      — gateway returned an error              (recoverable)
--   cancelled   — admin voided the batch                 (terminal)
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'settlement_status') THEN
        CREATE TYPE settlement_status AS ENUM (
            'pending',
            'processing',
            'paid',
            'failed',
            'cancelled'
        );
    END IF;
END$$;

-- ---------------------------------------------------------------------
-- Table: settlement_batches
--
-- One row per (jeeber, weekly period). The settlement worker rolls all
-- of a Jeeber's unsettled delivery_financials within [period_start,
-- period_end] into a single batch, then calls unified_payment_gateway
-- to disburse `total_payout` (= sum(delivery_fee) − total_commission)
-- via the chosen payout_method.
--
-- period_start / period_end are stored as DATE (inclusive both sides).
-- The MVP uses Monday-anchored ISO weeks; the unique index below allows
-- any non-overlapping period scheme without requiring a schema change.
--
-- The denormalised totals (total_commission, total_payout,
-- delivery_count) are maintained by the worker in the same transaction
-- that links delivery_financials.settlement_batch_id — never recomputed
-- from a moving source. Application owns the invariant.
--
-- external_reference is the payment-gateway transaction id returned by
-- unified_payment_gateway (see Locked-in payments policy). NULL until
-- the batch is at least `processing`.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS settlement_batches (
    id                  UUID              PRIMARY KEY DEFAULT gen_random_uuid(),
    jeeber_id           UUID              NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    period_start        DATE              NOT NULL,
    period_end          DATE              NOT NULL,
    total_commission    NUMERIC(12,2)     NOT NULL DEFAULT 0,
    total_payout        NUMERIC(12,2)     NOT NULL DEFAULT 0,
    delivery_count      INTEGER           NOT NULL DEFAULT 0,
    payout_method       payout_method     NOT NULL,
    status              settlement_status NOT NULL DEFAULT 'pending',

    processed_at        TIMESTAMPTZ       NULL,
    paid_at             TIMESTAMPTZ       NULL,
    failed_at           TIMESTAMPTZ       NULL,
    cancelled_at        TIMESTAMPTZ       NULL,
    failure_reason      TEXT              NULL,
    external_reference  TEXT              NULL,

    created_at          TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ       NOT NULL DEFAULT NOW(),

    CONSTRAINT settlement_batches_period_valid CHECK (period_end >= period_start),
    CONSTRAINT settlement_batches_amounts_nonneg CHECK (
        total_commission >= 0 AND total_payout >= 0
    ),
    CONSTRAINT settlement_batches_delivery_count_nonneg CHECK (
        delivery_count >= 0
    ),
    -- Terminal/in-flight statuses must carry their transition timestamp.
    CONSTRAINT settlement_batches_paid_consistency CHECK (
        (status = 'paid' AND paid_at IS NOT NULL) OR status <> 'paid'
    ),
    CONSTRAINT settlement_batches_failed_consistency CHECK (
        (status = 'failed' AND failed_at IS NOT NULL AND failure_reason IS NOT NULL)
        OR status <> 'failed'
    ),
    CONSTRAINT settlement_batches_cancelled_consistency CHECK (
        (status = 'cancelled' AND cancelled_at IS NOT NULL) OR status <> 'cancelled'
    )
);

-- One batch per Jeeber per period. Acceptance criterion: weekly
-- collection — the application picks the period boundaries; the index
-- guarantees no duplicate batch is ever opened against the same window.
CREATE UNIQUE INDEX IF NOT EXISTS settlement_batches_jeeber_period_uniq
    ON settlement_batches (jeeber_id, period_start, period_end);

-- Jeeber earnings/history view: "my recent payouts, newest first".
CREATE INDEX IF NOT EXISTS settlement_batches_jeeber_period_idx
    ON settlement_batches (jeeber_id, period_start DESC);

-- Settlement worker queue: open batches awaiting or in payout.
-- Partial keeps the index small once batches reach a terminal state.
CREATE INDEX IF NOT EXISTS settlement_batches_open_idx
    ON settlement_batches (status, period_end)
    WHERE status IN ('pending', 'processing', 'failed');

-- ---------------------------------------------------------------------
-- Table: delivery_financials
--
-- One row per completed delivery. Written by the settlement-finalising
-- step of the delivery state machine (status → 'delivered'), so the
-- presence of a row is itself a "delivery is financially settled-able"
-- signal.
--
-- jeeber_id and tier_id are denormalised snapshots from the delivery
-- so earnings-aggregation queries (jeeber_id + created_at range) can
-- hit a single index without joining delivery_requests. They are NOT
-- the source of truth — that remains delivery_requests / delivery_tiers.
--
-- Money columns:
--   goods_cost        — what the goods themselves cost the Client (NEC
--                       reimbursable component; the Jeeber fronts it
--                       and the Client repays at handoff). May be 0.
--   delivery_fee      — what the Jeeber charged for the trip itself
--                       (the offer.fee that was accepted).
--   commission_rate   — snapshot of delivery_tiers.commission_rate at
--                       delivery time; NEVER re-derive from the tier
--                       after settlement (rate changes are forward-only).
--   commission_amount — delivery_fee * commission_rate, rounded to
--                       cents by the application. Stored explicitly so
--                       reports never have to redo the math.
--   jeeber_payout     — generated column; delivery_fee − commission_amount.
--                       The Client always pays delivery_fee + goods_cost;
--                       Jeeber receives jeeber_payout + goods_cost.
--
-- settlement_batch_id is NULL until the weekly batch builder claims the
-- row. ON DELETE SET NULL means cancelling a batch does not delete the
-- delivery's financial record — it just unlinks it for the next run.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS delivery_financials (
    id                   UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    delivery_id          UUID          NOT NULL REFERENCES delivery_requests (id) ON DELETE RESTRICT,
    jeeber_id            UUID          NOT NULL REFERENCES users (id)             ON DELETE RESTRICT,
    tier_id              UUID          NULL     REFERENCES delivery_tiers (id)    ON DELETE RESTRICT,

    goods_cost           NUMERIC(12,2) NOT NULL DEFAULT 0,
    delivery_fee         NUMERIC(12,2) NOT NULL,
    commission_rate      NUMERIC(5,4)  NOT NULL,
    commission_amount    NUMERIC(12,2) NOT NULL,
    jeeber_payout        NUMERIC(12,2) GENERATED ALWAYS AS (delivery_fee - commission_amount) STORED,

    settlement_batch_id  UUID          NULL REFERENCES settlement_batches (id) ON DELETE SET NULL,
    settled_at           TIMESTAMPTZ   NULL,

    created_at           TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    updated_at           TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT delivery_financials_amounts_nonneg CHECK (
        goods_cost >= 0 AND delivery_fee >= 0 AND commission_amount >= 0
    ),
    CONSTRAINT delivery_financials_commission_range CHECK (
        commission_rate >= 0 AND commission_rate <= 1
    ),
    -- The platform never owes more than the Jeeber charged.
    CONSTRAINT delivery_financials_commission_le_fee CHECK (
        commission_amount <= delivery_fee
    ),
    -- settled_at and settlement_batch_id move together.
    CONSTRAINT delivery_financials_settled_consistency CHECK (
        (settlement_batch_id IS NULL AND settled_at IS NULL)
        OR (settlement_batch_id IS NOT NULL AND settled_at IS NOT NULL)
    )
);

-- One financial row per delivery. Settlement is a single ledger event.
CREATE UNIQUE INDEX IF NOT EXISTS delivery_financials_delivery_uniq
    ON delivery_financials (delivery_id);

-- ---------------------------------------------------------------------
-- Indexes
-- ---------------------------------------------------------------------
-- Acceptance criterion: earnings aggregation queries by jeeber + date
-- range. Powers the Jeeber dashboard ("earnings this week/month") and
-- the admin commission reports.
CREATE INDEX IF NOT EXISTS delivery_financials_jeeber_created_idx
    ON delivery_financials (jeeber_id, created_at DESC);

-- Batch-builder sweep: pick up this Jeeber's unsettled deliveries in
-- the upcoming period window. Partial keeps the index hot-set small
-- (once a row is settled, it never re-enters this query).
CREATE INDEX IF NOT EXISTS delivery_financials_unsettled_idx
    ON delivery_financials (jeeber_id, created_at)
    WHERE settlement_batch_id IS NULL;

-- Batch-contents view: list every delivery rolled into a batch
-- (admin drill-down, Jeeber payslip rendering).
CREATE INDEX IF NOT EXISTS delivery_financials_batch_idx
    ON delivery_financials (settlement_batch_id)
    WHERE settlement_batch_id IS NOT NULL;

-- ---------------------------------------------------------------------
-- Triggers: keep updated_at fresh (reuses set_updated_at() from 0001).
-- ---------------------------------------------------------------------
DROP TRIGGER IF EXISTS settlement_batches_set_updated_at ON settlement_batches;
CREATE TRIGGER settlement_batches_set_updated_at
    BEFORE UPDATE ON settlement_batches
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

DROP TRIGGER IF EXISTS delivery_financials_set_updated_at ON delivery_financials;
CREATE TRIGGER delivery_financials_set_updated_at
    BEFORE UPDATE ON delivery_financials
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

INSERT INTO schema_migrations (version)
VALUES ('0008_init_financial_ledger')
ON CONFLICT (version) DO NOTHING;

COMMIT;
