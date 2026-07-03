-- =====================================================================
-- Migration: 0024_delivery_requests_gateway_mirror
-- Ticket:    requests-durable (DurableRequestsStore owner-list durability)
-- Purpose:   Make the client-scoped request list (GET /requests →
--            IRequestsStore.ListForClientAsync) survive a gateway bounce.
--            delivery-service exposes NO client-scoped list endpoint, so the
--            gateway BFF mirrors every created request into its OWN Postgres
--            delivery_requests table and serves the owner-list from there.
--            This migration ADDs the gateway-mirror columns that table lacks.
-- Notes:     Idempotent — ALTER TABLE ... ADD COLUMN IF NOT EXISTS /
--            CREATE INDEX IF NOT EXISTS, wrapped in BEGIN/COMMIT. The
--            delivery_requests TABLE already exists (0004) — this migration
--            ONLY ALTERs it; it never re-CREATEs it.
--
--            WHY a parallel gw_* column set instead of reusing the native
--            columns for the gateway status/tier/jeeber:
--              * native `status` is the delivery_request_status ENUM, which
--                does NOT carry the gateway lifecycle tokens the BFF holds
--                (at_door / cancellation_requested), and it is coupled to the
--                status↔timestamp CHECK constraints
--                (cancelled_consistency / delivered_consistency /
--                 tier_required_after_pending / scheduled_consistency). The
--                mirror therefore writes the SAFE constant 'pending' into the
--                native `status` (which satisfies every one of those CHECKs
--                unconditionally) and keeps the REAL gateway status in
--                `gw_status`, free of the enum + CHECK coupling. That makes the
--                mirror upsert robust for EVERY create status without ever
--                fighting a constraint.
--              * native `tier_id` is a UUID FK into delivery_tiers; the gateway
--                row carries the tier CODE (flash/express/…), so it is stored
--                losslessly in `gw_tier_code` (no code→UUID resolution, no FK).
--              * the native table has NO jeeber_id / conversation_id /
--                accepted_fee / recipient_phone columns the owner-list needs.
--            `gw_mirror` marks rows the gateway BFF mirror wrote so the
--            owner-list read is scoped to them and never collides with any
--            future canonical writer of this table.
-- Refs:      migration 0004 (delivery_requests), 0013 (scheduled_at).
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Gateway-mirror columns. All NULLABLE (or DEFAULTed) and UNCONSTRAINED so
-- the mirror upsert never trips a CHECK. Additive only — the native columns,
-- their CHECKs, and any existing rows are untouched.
-- ---------------------------------------------------------------------
ALTER TABLE delivery_requests
    -- Provenance: TRUE on rows written by the gateway BFF owner-list mirror.
    ADD COLUMN IF NOT EXISTS gw_mirror              BOOLEAN       NOT NULL DEFAULT FALSE,
    -- The REAL gateway lifecycle status (pending/scheduled/accepted/at_door/
    -- cancellation_requested/cancelled/…), free of the native status ENUM +
    -- its status↔timestamp CHECK coupling. The owner-list reads this.
    ADD COLUMN IF NOT EXISTS gw_status             TEXT           NULL,
    -- Assigned jeeber (native delivery_requests has no jeeber_id column).
    ADD COLUMN IF NOT EXISTS gw_jeeber_id          TEXT           NULL,
    -- Tier CODE (native tier_id is a delivery_tiers UUID FK; store the code).
    ADD COLUMN IF NOT EXISTS gw_tier_code          TEXT           NULL,
    -- Broadcasting conversation id auto-created at request time.
    ADD COLUMN IF NOT EXISTS gw_conversation_id    TEXT           NULL,
    -- Accepted-offer fee snapshot (NUMERIC — no float money at any layer).
    ADD COLUMN IF NOT EXISTS gw_accepted_fee       NUMERIC(20,4)  NULL,
    -- E.164 handover-OTP recipient phone.
    ADD COLUMN IF NOT EXISTS gw_recipient_phone    TEXT           NULL,
    -- Cancel audit (mirrored on a committed cancel so a post-bounce owner-list
    -- reflects it). gw_cancelled_by is the role literal client/jeeber.
    ADD COLUMN IF NOT EXISTS gw_cancelled_by       TEXT           NULL,
    ADD COLUMN IF NOT EXISTS gw_cancellation_reason TEXT          NULL,
    ADD COLUMN IF NOT EXISTS gw_cancelled_at       TIMESTAMPTZ    NULL,
    -- Mirror freshness stamp (app-maintained; independent of the native
    -- updated_at trigger).
    ADD COLUMN IF NOT EXISTS gw_updated_at         TIMESTAMPTZ    NOT NULL DEFAULT NOW();

-- ---------------------------------------------------------------------
-- Index: the owner-list read is
--   SELECT ... WHERE client_id = ? AND gw_mirror = TRUE ORDER BY created_at.
-- Partial on gw_mirror keeps it tiny (only BFF-mirrored rows) and disjoint
-- from the native delivery_requests_client_created index.
-- ---------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS delivery_requests_gw_mirror_client_idx
    ON delivery_requests (client_id, created_at)
    WHERE gw_mirror = TRUE;

INSERT INTO schema_migrations (version)
VALUES ('0024_delivery_requests_gateway_mirror')
ON CONFLICT (version) DO NOTHING;

COMMIT;
