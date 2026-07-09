-- =====================================================================
-- Migration: 0036_tier_table_three_tiers_ttl
-- Ticket:    Q-013
-- Purpose:   Collapse the gateway tier catalog to exactly three tiers and
--            add the per-tier request TTL used by RequestExpirySweeper.
--
-- Notes:     Idempotent + fully guarded. Every table-dependent operation is
--            wrapped in a to_regclass presence check, so this is safe to run
--            against a fresh DB, the live drifted DB, and an already-converged
--            DB (no-op when already matching). Adds request_ttl_seconds to the
--            gateway `tiers` table, collapses it to the canonical three rows,
--            and forward-converges `delivery_tiers` to the flat 10% commission.
--
--            RE-RUN SAFETY: db/apply.sh re-runs EVERY migration on every deploy
--            under ON_ERROR_STOP=1, and the base seeds 0011/0029 (which stay
--            byte-identical to their original applied form) re-insert the
--            pre-convergence rows/rates on each run via ON CONFLICT DO NOTHING.
--            This migration therefore does NOT add hard CHECK constraints that
--            would forbid those seeded values: Postgres evaluates a table CHECK
--            on the proposed row BEFORE ON CONFLICT skips it, so such a CHECK
--            would brick the 2nd+ deploy (23514) on the very rows this migration
--            immediately re-converges. The catalog/commission invariants are
--            instead enforced by the app write-path (AdminTiersController:
--            ValidateCreatableTierId + ValidateCommission) + this per-deploy
--            convergence (DELETE + upsert + UPDATE below). See the inline NOTEs.
-- =====================================================================

BEGIN;

DO $$
BEGIN
    -- ---- gateway tiers catalog (created by 0029) --------------------
    IF to_regclass('public.tiers') IS NOT NULL THEN
        ALTER TABLE tiers ADD COLUMN IF NOT EXISTS request_ttl_seconds INT;

        UPDATE tiers
           SET request_ttl_seconds = CASE id
                WHEN 'urgent'    THEN 30 * 60
                WHEN 'same-day'  THEN 2 * 60 * 60
                WHEN 'scheduled' THEN 24 * 60 * 60
                ELSE 24 * 60 * 60
            END
         WHERE request_ttl_seconds IS NULL;

        ALTER TABLE tiers ALTER COLUMN request_ttl_seconds SET DEFAULT (30 * 60);
        ALTER TABLE tiers ALTER COLUMN request_ttl_seconds SET NOT NULL;

        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_tiers_request_ttl_seconds') THEN
            ALTER TABLE tiers
                ADD CONSTRAINT ck_tiers_request_ttl_seconds
                CHECK (request_ttl_seconds BETWEEN 60 AND 2592000);
        END IF;

        -- Drop any stale CHECK constraints left behind by an earlier build of
        -- this branch (wo/tier-table..6bd9112) that added hard commission/catalog
        -- CHECKs. A DB that ran that build once has them installed durably; since
        -- apply.sh re-runs the 0011/0029 seeds every deploy (re-inserting non-0.10
        -- rates + non-catalog ids that Postgres CHECK-evaluates BEFORE ON CONFLICT
        -- skips them), those CHECKs brick every subsequent deploy (23514). DROP
        -- them so this migration converges from that state too; no-op otherwise.
        ALTER TABLE tiers DROP CONSTRAINT IF EXISTS ck_tiers_fixed_catalog_id;
        ALTER TABLE tiers DROP CONSTRAINT IF EXISTS ck_tiers_commission_rate_flat;

        -- Collapse to the fixed three-tier catalog BEFORE upserting the
        -- canonical rows. DELETE-before-UPSERT avoids a uq_tiers_name_lower
        -- (LOWER(name)) unique collision in the edge case where a row outside
        -- the canonical set carries a name that LOWER()-matches a canonical
        -- tier: deleting the stray row first frees the name the upsert then sets.
        DELETE FROM tiers WHERE id NOT IN ('urgent', 'same-day', 'scheduled');

        -- Neutralize any canonical row whose current name does not match its
        -- target canonical name (e.g. an admin renamed 'urgent'->"X" and
        -- 'same-day'->"Urgent"): the upsert below would otherwise hit a
        -- uq_tiers_name_lower (LOWER(name)) collision (23505) when it sets
        -- urgent.name='Urgent' while same-day still holds 'Urgent'. Park the
        -- mismatched names on unique 'zzmig:<id>' placeholders first; the upsert
        -- then sets each canonical name with no in-flight collision. Idempotent
        -- no-op once names already match (IS DISTINCT FROM guard).
        UPDATE tiers t
           SET name = 'zzmig:' || t.id
          FROM (VALUES ('urgent','Urgent'),('same-day','Same-Day'),('scheduled','Scheduled')) c(id, cname)
         WHERE t.id = c.id
           AND t.name IS DISTINCT FROM c.cname;

        INSERT INTO tiers (
            id, name, sla_hours, radius_km, request_ttl_seconds,
            commission_rate, price_hint, created_by, updated_by
        )
        VALUES
            ('urgent',    'Urgent',    1,  3.0,  30 * 60,      0.10, 'Premium — fastest dispatch, top-of-list matching', 'system', 'system'),
            ('same-day',  'Same-Day',  2, 10.0,  2 * 60 * 60,  0.10, 'Standard same-day rate',                           'system', 'system'),
            ('scheduled', 'Scheduled', 24, 25.0, 24 * 60 * 60, 0.10, 'Choose a delivery window up to 24h ahead',         'system', 'system')
        ON CONFLICT (id) DO UPDATE
           SET name                = EXCLUDED.name,
               sla_hours           = EXCLUDED.sla_hours,
               radius_km           = EXCLUDED.radius_km,
               request_ttl_seconds = EXCLUDED.request_ttl_seconds,
               commission_rate     = EXCLUDED.commission_rate,
               price_hint          = EXCLUDED.price_hint,
               updated_by          = 'system',
               updated_at          = now()
         WHERE tiers.name                IS DISTINCT FROM EXCLUDED.name
            OR tiers.sla_hours           IS DISTINCT FROM EXCLUDED.sla_hours
            OR tiers.radius_km           IS DISTINCT FROM EXCLUDED.radius_km
            OR tiers.request_ttl_seconds IS DISTINCT FROM EXCLUDED.request_ttl_seconds
            OR tiers.commission_rate     IS DISTINCT FROM EXCLUDED.commission_rate
            OR tiers.price_hint          IS DISTINCT FROM EXCLUDED.price_hint;

        -- NOTE: hard CHECK constraints ck_tiers_fixed_catalog_id
        -- (id IN urgent/same-day/scheduled) and ck_tiers_commission_rate_flat
        -- (commission_rate = 0.10) were intentionally REMOVED. apply.sh re-runs
        -- the 0029 seed every deploy, re-inserting economy/on-the-way (ids
        -- outside the catalog) and their non-0.10 rates via ON CONFLICT DO
        -- NOTHING; Postgres checks the constraint on those proposed rows BEFORE
        -- the conflict is skipped, so either CHECK would brick the 2nd+ deploy
        -- (23514) even though the DELETE above removes those rows microseconds
        -- later. The invariant is enforced by the app write-path
        -- (AdminTiersController: ValidateCreatableTierId + ValidateCommission
        -- reject any non-catalog id or rate <> 0.10) and by this per-deploy
        -- DELETE+upsert convergence.
    END IF;

    -- ---- delivery_tiers reference catalog (created by 0004/0011) -----
    IF to_regclass('public.delivery_tiers') IS NOT NULL THEN
        -- Drop the stale flat-commission CHECK if an earlier build of this branch
        -- installed it (same rationale as the tiers CHECKs above): the 0011 seed
        -- re-inserts flash/express 0.1500 + standard 0.1200 every deploy, which a
        -- CHECK evaluates BEFORE ON CONFLICT (code) DO NOTHING skips them, bricking
        -- apply.sh (23514). Idempotent no-op if it was never added.
        ALTER TABLE delivery_tiers DROP CONSTRAINT IF EXISTS ck_delivery_tiers_commission_rate_flat;

        -- Forward-converge already-deployed rows to the flat 10% commission.
        -- The 0011 seed re-inserts flash/express 0.1500 + standard 0.1200 +
        -- on_the_way/eco 0.1000 every deploy via ON CONFLICT (code) DO NOTHING
        -- (existing rows keep their value); this UPDATE re-flattens every row to
        -- 0.1000 each deploy. (NOT a no-op on a fresh DB: 0011 seeds 15/15/12/
        -- 10/10; 0035 already flattened it, this is defense-in-depth.)
        UPDATE delivery_tiers
           SET commission_rate = 0.1000
         WHERE commission_rate <> 0.1000;

        -- NOTE: hard CHECK ck_delivery_tiers_commission_rate_flat
        -- (commission_rate = 0.1000) was intentionally REMOVED — same reason as
        -- the tiers CHECKs above. The 0011 seed re-inserts flash/express 0.1500
        -- + standard 0.1200 every deploy; a CHECK would be evaluated on those
        -- proposed rows BEFORE ON CONFLICT (code) DO NOTHING skips them and
        -- brick apply.sh on the 2nd deploy (23514). The flat-10% invariant is
        -- enforced by app commission policy + this per-deploy UPDATE.
    END IF;
END$$;

INSERT INTO schema_migrations (version)
VALUES ('0036_tier_table_three_tiers_ttl')
ON CONFLICT (version) DO NOTHING;

COMMIT;
