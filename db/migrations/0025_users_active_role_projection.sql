-- =====================================================================
-- Migration: 0025_users_active_role_projection
-- Ticket:    JEB users-durable
-- Purpose:   Durable gateway identity PROJECTION. The jeeb-gateway now
--            persists its user-management-resolved identity projection into
--            the EXISTING users table (0001 + 0006 + 0012) so admin
--            user-search (GET /admin/users/search) and the token-mint
--            active_role read survive a gateway bounce instead of living
--            only in process memory.
--
--            users already carries id / phone / email / name / avatar_url /
--            roles / language / rating / suspension. This migration ADDS the
--            only two projected columns it still lacks:
--              * active_role      — the dual-role active role the gateway-
--                                   minted session JWT embeds (TokenService
--                                   reads it back from the store).
--              * role_switched_at — when the active role last changed.
--
--            Identity remains user-management's source of truth; this is a
--            read-model projection for durable search + token-mint reads.
-- Notes:     Idempotent — ADD COLUMN IF NOT EXISTS only. REUSES the users
--            table (0001); it is NEVER re-created here. Safe to re-run.
--            Admin search filters name / phone / email via ILIKE, backed by
--            the trigram GIN indexes already built in 0006 — no new index.
-- Refs:      FR-admin-user-search, ADR-0001 (stateless gateway).
-- =====================================================================

BEGIN;

ALTER TABLE users
    ADD COLUMN IF NOT EXISTS active_role      VARCHAR(32) NOT NULL DEFAULT 'customer',
    ADD COLUMN IF NOT EXISTS role_switched_at TIMESTAMPTZ NULL;

INSERT INTO schema_migrations (version)
VALUES ('0025_users_active_role_projection')
ON CONFLICT (version) DO NOTHING;

COMMIT;
