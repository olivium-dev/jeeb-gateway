-- =====================================================================
-- Migration: 0028_account_deletions_side_effects_marker
-- Ticket:    gateway-durability (F5 — crash-safe deletion side effects)
-- Purpose:   Make the account-deletion REQUEST side effects (refresh-token
--            revocation + order/ledger anonymization) crash-safe.
--
--            Before: RequestAsync committed the INSERT (ON CONFLICT (user_id)
--            DO NOTHING) as its own command and THEN ran the side effects. A
--            crash after the commit but before the side effects finished left
--            a row that, on retry, hit ON CONFLICT DO NOTHING and returned the
--            existing row while SKIPPING every side effect — so refresh tokens
--            were never revoked (security) and orders/ledger were never
--            anonymized (GDPR).
--
--            After: a nullable completion marker drives the side effects. A
--            retry re-runs the (idempotent) side effects until the marker is
--            set, rather than treating insert-success as the one-shot guard.
--
-- Notes:     Idempotent — ADD COLUMN IF NOT EXISTS. Nullable with no default,
--            so existing rows read as "side effects not yet marked complete";
--            that is safe because token-revoke and hash-anonymize are both
--            idempotent (re-running them is a harmless no-op).
-- =====================================================================

BEGIN;

ALTER TABLE account_deletions
    ADD COLUMN IF NOT EXISTS side_effects_completed_at TIMESTAMPTZ NULL;

INSERT INTO schema_migrations (version)
VALUES ('0028_account_deletions_side_effects_marker')
ON CONFLICT (version) DO NOTHING;

COMMIT;
