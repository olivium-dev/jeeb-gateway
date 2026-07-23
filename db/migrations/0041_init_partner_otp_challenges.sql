-- =====================================================================
-- Migration: 0041_init_partner_otp_challenges
-- Ticket:    partner-wallet-bff PP-7 — OTP step-up on high-value partner
--            top-ups (money-adjacent authorization control).
-- Purpose:   Durable store for the Jeeb Partner Portal's PP-7 one-time step-up
--            code. A partner→jeeber top-up whose gross amount is ABOVE
--            PartnerWallet__OtpStepUpThreshold must first mint a challenge
--            (POST v1/partner/wallet/transfers/otp/challenge) and then present the
--            6-digit code back on the confirm (POST v1/partner/wallet/transfers)
--            before the wallet-service saga is ever invoked. Backs
--            Partner/IPartnerOtpChallengeStore.
--
--            The code is minted cryptographically-random and is stored HASHED
--            (SHA-256 hex in code_hash) — the raw code is NEVER persisted and
--            NEVER logged (it is surfaced once in-app as devCode only when
--            Features__DevEndpoints__Enabled=true; production returns null and SMS
--            delivery stays a documented TODO — no Twilio in this cut). A challenge
--            is SINGLE-USE: consumed_at is stamped atomically at confirm time via a
--            conditional UPDATE (... WHERE consumed_at IS NULL RETURNING id), so a
--            concurrent double-submit or a later replay of the same challenge can
--            authorize AT MOST ONE money move (maps to 403 otp-consumed). attempts
--            caps wrong-code guesses (403 otp-invalid) and hard-expires the
--            challenge after the ceiling (403 otp-exhausted); expires_at bounds the
--            5-minute validity window.
--
--            Because it is a money-authorization control, it is guarded fail-closed
--            in prod-like environments (StoreDurabilityGuard.Critical): a gateway
--            with GatewayPostgres unset refuses to boot rather than silently serve
--            the in-memory fallback and let a spent/expired code re-authorize a
--            high-value move across a restart.
--
-- Notes:     Idempotent — CREATE TABLE IF NOT EXISTS, safe to re-run. No seed rows:
--            the store starts empty exactly like the in-memory ConcurrentDictionary.
--            id / partner_id / jeeber_id are UUID; amount is NUMERIC(18,4) to match
--            the money precision used across the gateway financial tables. Same
--            style as migration 0040 (partner_wallet_operations).
-- Refs:      Partner/IPartnerOtpChallengeStore.cs, Partner/PostgresPartnerOtpChallengeStore.cs,
--            migration 0040 (partner_wallet_operations — sibling partner-wallet store).
--
-- ROLLBACK (additive, isolated new table — no other object depends on it, so dropping
--           it is safe and loses only pending step-up challenges; after rollback the
--           code path falls back to the in-memory store which the durability guard
--           then refuses in prod-like envs — intended):
--   BEGIN;
--     DROP TABLE IF EXISTS partner_otp_challenges;
--     DELETE FROM schema_migrations WHERE version = '0041_init_partner_otp_challenges';
--   COMMIT;
-- =====================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS partner_otp_challenges (
    id           UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    partner_id   UUID          NOT NULL,            -- caller partner the challenge is bound to
    jeeber_id    UUID          NOT NULL,            -- top-up destination the challenge authorizes
    amount       NUMERIC(18,4) NOT NULL,            -- gross top-up amount the challenge authorizes
    code_hash    TEXT          NOT NULL,            -- SHA-256 hex of the 6-digit code (raw code NEVER stored)
    attempts     INT           NOT NULL DEFAULT 0,  -- wrong-code guesses; hard-expires at the policy ceiling
    expires_at   TIMESTAMPTZ   NOT NULL,            -- validity window end (created_at + 5 min)
    consumed_at  TIMESTAMPTZ   NULL,                -- single-use stamp; set atomically on a successful confirm
    created_at   TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- Sweep/read path (expire-old maintenance + a partner's recent challenges), newest first.
CREATE INDEX IF NOT EXISTS ix_partner_otp_challenges_partner
    ON partner_otp_challenges (partner_id, created_at DESC);

INSERT INTO schema_migrations (version)
VALUES ('0041_init_partner_otp_challenges')
ON CONFLICT (version) DO NOTHING;

COMMIT;
