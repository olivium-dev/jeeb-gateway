-- =====================================================================
-- Migration: 0043_delivery_requests_gw_expired_at
-- Purpose:   Gateway-local audit column recording WHEN a request was
--            observed expired.
-- Notes:     Idempotent, additive, no constraint touched, no backfill.
-- =====================================================================

BEGIN;

ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS gw_expired_at TIMESTAMPTZ NULL;

INSERT INTO schema_migrations (version)
VALUES ('0043_delivery_requests_gw_expired_at')
ON CONFLICT (version) DO NOTHING;

COMMIT;
