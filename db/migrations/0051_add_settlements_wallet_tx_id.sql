-- =====================================================================
-- Migration: 0051_add_settlements_wallet_tx_id
-- Purpose:   gwdbx W2-05 — wallet-service mirror stamp: the holder-earning
--            id once a settlement is mirrored (NULL = not yet mirrored).
-- Notes:     Idempotent, additive, no constraint touched, no backfill.
-- =====================================================================

BEGIN;

ALTER TABLE settlements ADD COLUMN IF NOT EXISTS wallet_tx_id TEXT NULL;

INSERT INTO schema_migrations (version)
VALUES ('0051_add_settlements_wallet_tx_id')
ON CONFLICT (version) DO NOTHING;

COMMIT;
