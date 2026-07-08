-- =====================================================================
-- Migration: 0035_converge_usd_money_truth
-- Purpose:   Forward-only convergence of the flat 10% commission policy on
--            delivery_tiers for databases that seeded pre-flat-10% rates.
-- Notes:     Idempotent + guarded. Fresh DBs already seed 0.1000 so the
--            UPDATE is a no-op there.
--
--   SCOPE NOTE (2026-07-08): settlement_batches USD/schema convergence was
--   DESCOPED from this migration by owner decision. Reason: settlement_batches
--   is created by BOTH the retired-UPG migration 0008 (payout via
--   unified_payment_gateway; columns total_commission/total_payout/
--   delivery_count/payout_method + settlement_status enum) AND the live
--   cash-era 0015 (total_*_usd/currency/status TEXT). Both use
--   CREATE TABLE IF NOT EXISTS with no DROP between them, so 0008 wins and the
--   live 0015 shape never lands — a PRE-EXISTING collision that predates this
--   branch. Converging it safely needs a canonical-shape decision + a proper
--   forward migration and is tracked as a dedicated settlement_batches fix
--   (start: verify the real shape on the MSI dev DB). This migration therefore
--   only converges delivery_tiers commission, which is this branch's scope.
-- =====================================================================

BEGIN;

-- Forward-converge already-seeded delivery_tiers rows to the flat 10% commission.
-- The 0011 seed uses ON CONFLICT (code) DO NOTHING, so databases provisioned
-- before the flat-10% ruling still carry old per-tier rates. Fresh DBs already
-- seed 0.1000 so this is a no-op there.
DO $$
BEGIN
    IF to_regclass('public.delivery_tiers') IS NOT NULL THEN
        UPDATE delivery_tiers
        SET commission_rate = 0.1000,
            updated_at = now()
        WHERE commission_rate IS DISTINCT FROM 0.1000;
    END IF;
END$$;

INSERT INTO schema_migrations (version)
VALUES ('0035_converge_usd_money_truth')
ON CONFLICT (version) DO NOTHING;

COMMIT;
