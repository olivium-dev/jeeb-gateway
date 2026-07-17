-- =====================================================================
-- Migration: 0040_init_partner_wallet_operations
-- Ticket:    partner-wallet-bff money-safety blocker set — MONEY (double-spend
--            guard on BOTH partner-portal money paths).
-- Purpose:   Durable idempotency / dedup store for the Jeeb Partner Portal's two
--            money-moving BFF paths — partner→jeeber top-up
--            (POST v1/partner/wallet/transfers) and admin system→partner cash-credit
--            (POST v1/admin/partners/{id}/wallet/credits). Backs
--            Partner/IPartnerWalletOperationStore.
--
--            The reused wallet-service TransactionRequest carries NO idempotency
--            field (ServiceName/Tag/Notes only), so a retried confirm (client
--            timeout after the first commit, a double-tap, or a Back+resubmit)
--            would re-run the initiate→execute saga and DOUBLE-MOVE real money.
--            Before this table the client "idempotency key" was only string-
--            concatenated into the transaction Notes — decorative, never enforced.
--            This table makes it a REAL dedup key: the FIRST claim inserts and runs
--            the saga; any duplicate is short-circuited to the stored prior result
--            (replay) or refused as in-flight (409) — the move happens exactly once.
--
--            It DOUBLES as the immutable cash-in/move audit record the operator
--            surface requires (operator/actor id, partner id, amount, evidence note,
--            wallet-service transaction id, timestamps) — replacing the prior
--            log-line-only "audit trail" (a log rotation lost it).
--
--            DB-level idempotency: UNIQUE (operation_type, actor_id, idempotency_key)
--            + INSERT ... ON CONFLICT DO NOTHING, exactly the shape
--            PostgresSettlementEnqueueStore (migration 0034) and PostgresSettlementStore
--            (migration 0015) use for their own no-double money invariants. The key is
--            scoped to the acting principal (caller partner for a top-up, acting admin
--            for a cash-credit) so one actor's key can never collide with another's.
--
--            Because this is a MONEY dedup store, it is guarded fail-closed in
--            prod-like environments (StoreDurabilityGuard.Critical): a gateway with
--            GatewayPostgres unset refuses to boot rather than silently serve the
--            in-memory fallback and risk a double-spend on restart.
--
-- Notes:     Idempotent — CREATE TABLE IF NOT EXISTS, safe to re-run. No seed rows:
--            the store starts empty exactly like the in-memory ConcurrentDictionary.
--            actor_id / partner_id / counterparty_id / transaction_id are UUID; amount
--            and fees are NUMERIC(18,4) to match the money precision used across the
--            gateway financial tables.
-- Refs:      Partner/IPartnerWalletOperationStore.cs, Partner/PostgresPartnerWalletOperationStore.cs,
--            migration 0034 (settlement_enqueue — sibling money idempotency store),
--            migration 0005 (admin_actions — sibling admin audit trail).
--
-- ROLLBACK (additive, isolated new table — no other object depends on it, so dropping
--           it is safe and loses only the partner-portal dedup + cash-in audit rows;
--           after rollback the code path falls back to the in-memory store which the
--           durability guard then refuses in prod-like envs — intended):
--   BEGIN;
--     DROP TABLE IF EXISTS partner_wallet_operations;
--     DELETE FROM schema_migrations WHERE version = '0040_init_partner_wallet_operations';
--   COMMIT;
-- =====================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS partner_wallet_operations (
    id               UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    operation_type   TEXT          NOT NULL,            -- 'topup' | 'cash-credit'
    actor_id         UUID          NOT NULL,            -- caller partner (topup) / acting admin (cash-credit)
    idempotency_key  TEXT          NOT NULL,
    partner_id       UUID          NOT NULL,            -- partner wallet holder (dest of credit / source of topup)
    counterparty_id  UUID          NULL,                -- jeeber (topup dest); NULL for cash-credit
    amount           NUMERIC(18,4) NOT NULL,
    fees             NUMERIC(18,4) NOT NULL DEFAULT 0,
    evidence_note    TEXT          NULL,                -- cash-credit audit evidence (mandatory upstream)
    status           TEXT          NOT NULL DEFAULT 'pending',  -- 'pending' | 'completed' | 'uncertain'
    transaction_id   UUID          NULL,                -- wallet-service tx header id once committed
    created_at       TIMESTAMPTZ   NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ   NOT NULL DEFAULT now(),
    CONSTRAINT uq_partner_wallet_operations_key
        UNIQUE (operation_type, actor_id, idempotency_key)
);

-- Audit read path (operator timeline for a partner's money-in/move events, newest first).
CREATE INDEX IF NOT EXISTS ix_partner_wallet_operations_partner
    ON partner_wallet_operations (partner_id, created_at DESC);

INSERT INTO schema_migrations (version)
VALUES ('0040_init_partner_wallet_operations')
ON CONFLICT (version) DO NOTHING;

COMMIT;
