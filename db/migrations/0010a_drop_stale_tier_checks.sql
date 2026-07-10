-- =====================================================================
-- Migration: 0010a_drop_stale_tier_checks
-- Ticket:    Q-013 (money convergence — pre-flight for 0036)
-- Purpose:   PRE-FLIGHT cleanup. Drop the stale hard CHECK constraints that an
--            earlier build of the money branch (wo/tier-table..6bd9112) added to
--            the `tiers` and `delivery_tiers` catalogs. A DB that ran that build
--            once carries them DURABLY; because db/apply.sh re-runs EVERY
--            migration in filename order on every deploy under ON_ERROR_STOP=1,
--            the reference seeds re-propose their original (non-flat, non-catalog)
--            rows every deploy:
--              * 0011_init_seed_reference_data.sql re-inserts delivery_tiers
--                flash/express 0.1500 + standard 0.1200 (ON CONFLICT DO NOTHING),
--              * 0029_init_delivery_tier_catalog.sql re-inserts tiers
--                urgent 0.25 + economy + on-the-way (ON CONFLICT DO NOTHING).
--            Postgres evaluates a table CHECK on the proposed row BEFORE ON
--            CONFLICT can skip it, so a surviving CHECK bricks apply.sh with
--            23514 at 0011 (and, past that, at 0029) on EVERY subsequent deploy —
--            long before 0036 (which owns the catalog/commission convergence) is
--            ever reached.
--
--            THEREFORE these DROPs MUST run BEFORE 0011/0029. This file sorts as
--            0010a (> 0010_, < 0011_: they differ at the 4th char '0' < '1' in
--            every locale), so it runs after delivery_tiers is created (@ 0004)
--            but before the first seed that would trip a constraint.
--
--            Forward-only + idempotent. Table-level `ALTER TABLE IF EXISTS` is
--            REQUIRED: on a FRESH DB the `tiers` table does not exist yet at this
--            position (created by 0029), so a bare `ALTER TABLE tiers` would brick
--            the fresh path. `DROP CONSTRAINT IF EXISTS` makes each statement a
--            no-op on any DB that never carried the stale CHECK. Base seeds
--            0011/0029 stay byte-identical (NOT edited). The ongoing invariant is
--            enforced by the app write-path (AdminTiersController:
--            ValidateCreatableTierId + ValidateCommission) and 0036's per-deploy
--            DELETE + upsert + UPDATE convergence.
-- =====================================================================

BEGIN;

ALTER TABLE IF EXISTS tiers          DROP CONSTRAINT IF EXISTS ck_tiers_fixed_catalog_id;
ALTER TABLE IF EXISTS tiers          DROP CONSTRAINT IF EXISTS ck_tiers_commission_rate_flat;
ALTER TABLE IF EXISTS delivery_tiers DROP CONSTRAINT IF EXISTS ck_delivery_tiers_commission_rate_flat;

INSERT INTO schema_migrations (version)
VALUES ('0010a_drop_stale_tier_checks')
ON CONFLICT (version) DO NOTHING;

COMMIT;
