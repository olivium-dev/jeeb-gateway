-- =====================================================================
-- Seed: test_accounts
-- Ticket: T-database-009
-- Purpose: Five persona accounts (P1-P5) used by local dev, CI integration
--          tests, the Maestro mobile e2e suite, and the jeeb-admin smoke
--          tests. Phone numbers use the +961 (Lebanon) 70 mobile prefix
--          followed by 0000XXX so they cannot collide with real subscribers
--          (no Lebanese carrier issues the 70 0000 range).
--
-- Idempotency: every row uses ON CONFLICT against the natural unique key
-- (users.phone), so applying this script repeatedly is safe.
--
-- IMPORTANT: do NOT apply this in production. The Jeeb production database
-- is bootstrapped via db/migrations/*.sql only; this directory exists for
-- dev/CI/test environments. db/seed.sh refuses to run when DATABASE_URL
-- points at a production host.
--
-- Deterministic UUIDs: the persona ids are pinned constants so test
-- fixtures, Postman collections, and the Maestro suite can reference them
-- by literal value without an extra round-trip to fetch them by phone.
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- P1 — Layla, Beirut working mom. Primary Client persona (FR-1.* coverage).
-- ---------------------------------------------------------------------
INSERT INTO users (id, phone, email, name, roles, language)
VALUES (
    '11111111-1111-4111-8111-111111111111',
    '+96170000001',
    'layla.test@jeeb.local',
    'Layla (P1 Client)',
    '["customer"]'::jsonb,
    'en'
)
ON CONFLICT (phone) DO NOTHING;

-- ---------------------------------------------------------------------
-- P2 — Hajj Antoine, retired Achrafieh resident. Accessibility-critical
-- Client persona. Arabic-first; covers FR-16 RTL flows.
-- ---------------------------------------------------------------------
INSERT INTO users (id, phone, email, name, roles, language)
VALUES (
    '22222222-2222-4222-8222-222222222222',
    '+96170000002',
    'antoine.test@jeeb.local',
    'Hajj Antoine (P2 Client, Arabic)',
    '["customer"]'::jsonb,
    'ar'
)
ON CONFLICT (phone) DO NOTHING;

-- ---------------------------------------------------------------------
-- P3 — Rami, university student with a scooter. Primary Jeeber persona.
-- Holds both roles to cover dual-mode tests (FR-2 role switching).
-- ---------------------------------------------------------------------
INSERT INTO users (id, phone, email, name, roles, language, rating, rating_count)
VALUES (
    '33333333-3333-4333-8333-333333333333',
    '+96170000003',
    'rami.test@jeeb.local',
    'Rami (P3 Jeeber, dual-role)',
    '["customer","driver"]'::jsonb,
    'en',
    4.62, 18
)
ON CONFLICT (phone) DO NOTHING;

-- ---------------------------------------------------------------------
-- P4 — Khaled, full-time courier. Power-user Jeeber persona. Drives the
-- earnings-dashboard tests (FR-11) and weekly settlement (FR-10.3).
-- ---------------------------------------------------------------------
INSERT INTO users (id, phone, email, name, roles, language, rating, rating_count)
VALUES (
    '44444444-4444-4444-8444-444444444444',
    '+96170000004',
    'khaled.test@jeeb.local',
    'Khaled (P4 Jeeber, power user)',
    '["driver"]'::jsonb,
    'ar',
    4.81, 412
)
ON CONFLICT (phone) DO NOTHING;

-- ---------------------------------------------------------------------
-- P5 — Operations Admin (internal). Drives FR-14.* admin flows: KYC
-- review, dispute resolution, prohibited-items moderation.
-- ---------------------------------------------------------------------
INSERT INTO users (id, phone, email, name, roles, language)
VALUES (
    '55555555-5555-4555-8555-555555555555',
    '+96170000005',
    'admin.test@jeeb.local',
    'Ops Admin (P5)',
    '["admin"]'::jsonb,
    'en'
)
ON CONFLICT (phone) DO NOTHING;

-- KYC seeding removed with the kyc_submissions DROP (gwdbx W0-08, migration 0045).
-- Verification authority is user-management; nothing in the gateway reads it.

-- Saved-address seeding removed with the saved_addresses DROP (gwdbx W0-06,
-- migration 0049). Address authority is user-management; nothing in the gateway
-- reads this table.

COMMIT;
