-- =====================================================================
-- Migration: 0034_init_settlement_enqueue
-- Ticket:    JEBV4-124 (AUDIT-A durability guard-gap) — MONEY-ADJACENT
-- Purpose:   Durable backing store for the gateway's pending-COD-settlement
--            ENQUEUE intent (Financials/ISettlementEnqueueStore.cs, JEB-1495).
--            Replaces InMemorySettlementEnqueueStore, a ConcurrentDictionary
--            keyed on delivery_id whose "this delivery reached handover-complete
--            and had a settlement enqueued" intent lived ONLY in gateway process
--            memory and was LOST on every restart / replica move — silently
--            dropping the record of which deliveries had already been enqueued
--            for settlement. Because the store's whole contract is idempotency
--            ("no double-enqueue"), losing it in a money path risks a duplicate
--            settlement enqueue after a restart — hence it is guarded fail-closed
--            in prod-like environments (StoreDurabilityGuard.Critical).
--
--            One row per delivery. delivery_id is the PRIMARY KEY, so the
--            idempotent enqueue is enforced at the DB level via
--            INSERT ... ON CONFLICT (delivery_id) DO NOTHING: the FIRST enqueue
--            inserts and returns the row (TryEnqueueAsync → true); every
--            subsequent enqueue for the same delivery is a no-op and preserves
--            the original enqueued_at timestamp (TryEnqueueAsync → false). This
--            mirrors PostgresSettlementStore's UNIQUE(delivery_id) +
--            ON CONFLICT DO NOTHING idempotency (migration 0015 / JEB-56).
--
--            This is DELIBERATELY a separate, minimal table from the money
--            system-of-record (settlements, migration 0015; delivery_financials
--            / settlement_batches, migration 0008): it records ONLY the enqueue
--            intent + timestamp — no amounts, no fee math, no party ids — exactly
--            the two facts InMemorySettlementEnqueueStore held (a deliveryId set +
--            its first-seen DateTimeOffset).
--
-- Notes:     Idempotent — CREATE TABLE IF NOT EXISTS, safe to re-run. No seed
--            rows: the store starts empty exactly like the in-memory
--            ConcurrentDictionary.
-- Refs:      JEB-1495 (settlement enqueue intent), T-backend-016 / JEEB-34
--            (cash settlement), JEBV4-124 (AUDIT-A durability guard-gap),
--            JEBV4-122 (AUDIT-A umbrella — unblocks once these deploy).
--
-- ROLLBACK (additive, isolated new table — no consumer depends on it yet, so
--           dropping it is safe and loses only the enqueue-intent bookkeeping):
--   BEGIN;
--     DROP TABLE IF EXISTS settlement_enqueue;
--     DELETE FROM schema_migrations WHERE version = '0034_init_settlement_enqueue';
--   COMMIT;
-- =====================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS settlement_enqueue (
    delivery_id  TEXT        PRIMARY KEY,
    enqueued_at  TIMESTAMPTZ NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

INSERT INTO schema_migrations (version)
VALUES ('0034_init_settlement_enqueue')
ON CONFLICT (version) DO NOTHING;

COMMIT;
