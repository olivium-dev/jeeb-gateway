-- =====================================================================
-- Migration: 0030_init_financial_ledger_anonymization
-- Ticket:    JEBV4-154 (AUDIT-A IN-MEM-LIVE) durability follow-up
-- Purpose:   Durable backing store for the gateway's financial-ledger
--            anonymization bookkeeping (GDPR account-deletion seam,
--            Users/IFinancialLedgerAnonymizer.cs). Replaces
--            InMemoryFinancialLedger, whose per-owner retained-row counters
--            lived ONLY in gateway process memory and were LOST on every
--            restart / replica move — silently dropping the record of which
--            financial rows had already been pseudonymized for a deleted
--            user (money + GDPR state, the highest-risk remaining in-memory
--            store per AUDIT-A).
--
--            This is the gateway's OWN anonymization ledger: a single
--            integer row-count keyed by "owner" — where an owner key is
--            either a live user id (rows still carrying the user's id) or an
--            anonymized user hash (rows already pseudonymized). The
--            account-deletion flow (InMemoryAccountDeletionStore /
--            PostgresAccountDeletionStore -> AnonymizeForUserAsync) MOVES a
--            user's row count from the user-id key to the hash key,
--            accumulating onto any existing hash total. It is DELIBERATELY a
--            separate table from the payment/finance tables (delivery_financials
--            / settlement_batches, migration 0008; settlements, migration 0015),
--            which are the money system-of-record owned by the settlement path
--            — this table only records the gateway's anonymization tallies so
--            the deletion store + tests can assert "financial records retained
--            for accounting" without exposing the ledger schema.
--
-- Notes:     Idempotent — CREATE TABLE/INDEX IF NOT EXISTS, safe to re-run.
--            No seed rows: the ledger starts empty exactly like
--            InMemoryFinancialLedger's ConcurrentDictionary. row_count is a
--            plain INTEGER counter (no money amounts, no rounding) — the
--            in-memory store held `int` row counts and accumulated them with
--            integer addition; PostgresFinancialLedger preserves that exactly
--            (owner_key TEXT identity, row_count = row_count + delta). The
--            column is named row_count (not `rows`) to avoid the SQL ROWS
--            keyword; PostgresFinancialLedger maps it back to the row count.
-- Refs:      T-backend-035 (account deletion, GDPR 30-day purge), FR (right
--            to erasure), JEBV4-154 (IN-MEM-LIVE durability).
-- =====================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS financial_ledger_anonymization (
    owner_key   TEXT        PRIMARY KEY,
    row_count   INTEGER     NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

INSERT INTO schema_migrations (version)
VALUES ('0030_init_financial_ledger_anonymization')
ON CONFLICT (version) DO NOTHING;

COMMIT;
