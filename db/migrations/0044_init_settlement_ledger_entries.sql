-- =====================================================================
-- Migration: 0044_init_settlement_ledger_entries
-- Batch:     b05/GW1 (W1.8 + W3.5(b)) — owner ruling 2026-07-31 "PROMOTE"
-- Purpose:   Durable backing store for the cash-settlement LEDGER
--            (Financials/ISettlementLedgerClient.cs, T-backend-016 / JEEB-34).
--            Replaces InMemorySettlementLedgerClient, whose entries lived ONLY
--            in a gateway-process ConcurrentDictionary keyed on the idempotency
--            key and evaporated on every restart / replica move.
--
-- WHY THIS IS A MONEY PATH, stated precisely, because the old class doc argued
-- the opposite and the argument was subtly wrong:
--
--   The settlements table (migration 0015) is the gateway-side system of record
--   for the settlement ROW. That much of the old rationale holds. What it does
--   NOT cover is the ledger ENTRY, and specifically the IDEMPOTENCY MEMO that
--   made the post safe to replay. InMemorySettlementLedgerClient's whole
--   correctness argument was `_entries.GetOrAdd(request.IdempotencyKey, …)`:
--   the same key replayed returns the ORIGINAL entry rather than posting twice.
--   That memo was in process memory. A restart empties it, so the next replay
--   of the same settlement id mints a SECOND ledger entry id for money that was
--   collected once — and SettlementService/SettlementLedgerReconciler then stamp
--   that second id onto settlements.ledger_entry_id, overwriting the first.
--   The gateway's own books end up disagreeing with themselves about how many
--   entries a single cash collection produced.
--
--   Jeeb is cash-on-delivery only (owner directive, locked). Cash is collected
--   hand-to-hand by the Jeeber, so this ledger is not a stand-in for a remote
--   write — it IS the book. A book whose only anti-double-entry mechanism is a
--   dictionary that a `systemctl restart` clears is not a book.
--
--            One row per idempotency key. idempotency_key is the PRIMARY KEY, so
--            the GetOrAdd contract is enforced at the DB level via
--            INSERT ... ON CONFLICT (idempotency_key) DO NOTHING RETURNING:
--            the FIRST post inserts and returns its freshly minted
--            ledger_entry_id; every replay of the same key inserts nothing, and
--            the client reads back the ORIGINAL ledger_entry_id and posted_at.
--            Byte-for-byte the ConcurrentDictionary.GetOrAdd semantics, and the
--            same DB-level idempotency PostgresSettlementStore (UNIQUE(delivery_id)
--            + ON CONFLICT DO NOTHING, migration 0015) and
--            PostgresSettlementEnqueueStore (delivery_id PK, migration 0034)
--            already use.
--
--            The entry payload columns mirror LedgerEntryRequest exactly. They
--            are recorded rather than discarded because a ledger that stores only
--            "an id existed" cannot be audited or reconciled against the
--            settlements table after the fact — which is the reason the entry is
--            being made durable in the first place. Monetary values are
--            NUMERIC(20,4), matching settlements (migration 0015): no float
--            arithmetic at any layer.
--
--            NOT a payments-gateway surface. There is no unified_payment_gateway
--            dependency here and none may be added: COD collects cash, so there
--            is nothing to settle out and nothing to refund. See AGENTS.md
--            "Payments: Jeeb is cash-on-delivery only".
--
-- Notes:     Idempotent — CREATE TABLE IF NOT EXISTS, safe to re-run. No seed
--            rows: the store starts empty exactly like the in-memory
--            ConcurrentDictionary did. Existing settlements rows that already
--            carry a ledger_entry_id are NOT backfilled — the in-memory entries
--            they refer to are already gone, and inventing rows for them would
--            fabricate a book. Those rows keep their stamp; only posts made from
--            this migration forward are durable.
--
-- Refs:      T-backend-016 / JEEB-34 (cash settlement), JEBV4-47 / M3-R7
--            (settlement ledger reconciler), AUDIT-A durability program
--            (StoreDurabilityGuard.Critical), owner ruling 2026-07-31
--            "GW1: promote ISettlementLedgerClient to StoreDurabilityGuard.Critical".
--
-- ROLLBACK (additive, isolated new table; dropping it reverts the ledger to the
--           in-memory client's restart-volatile behaviour and loses the durable
--           idempotency memo — do NOT drop it while the guard lists
--           ISettlementLedgerClient as Critical, or a prod-like boot fails closed):
--   BEGIN;
--     DROP TABLE IF EXISTS settlement_ledger_entries;
--     DELETE FROM schema_migrations WHERE version = '0044_init_settlement_ledger_entries';
--   COMMIT;
-- =====================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS settlement_ledger_entries (
    idempotency_key  TEXT           PRIMARY KEY,
    ledger_entry_id  TEXT           NOT NULL,
    delivery_id      TEXT           NOT NULL,
    jeeber_id        TEXT           NOT NULL,
    client_id        TEXT           NOT NULL,
    entry_type       TEXT           NOT NULL,
    goods_cost       NUMERIC(20,4)  NOT NULL,
    commission       NUMERIC(20,4)  NOT NULL,
    insurance        NUMERIC(20,4)  NOT NULL,
    total            NUMERIC(20,4)  NOT NULL,
    currency         TEXT           NOT NULL,
    payment_method   TEXT           NOT NULL,
    posted_at        TIMESTAMPTZ    NOT NULL,
    created_at       TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- Audit/reconciliation lookup: "show me the ledger entry for this delivery".
-- Not UNIQUE — the key of record is idempotency_key (the settlement id), and a
-- delivery could in principle be settled under more than one settlement id; a
-- UNIQUE here would turn that into a 500 on a money path instead of a duplicate
-- visible to reconciliation.
CREATE INDEX IF NOT EXISTS idx_settlement_ledger_entries_delivery
    ON settlement_ledger_entries (delivery_id);

INSERT INTO schema_migrations (version)
VALUES ('0044_init_settlement_ledger_entries')
ON CONFLICT (version) DO NOTHING;

COMMIT;
