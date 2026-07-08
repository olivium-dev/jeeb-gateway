-- =====================================================================
-- Migration: 0011_init_seed_reference_data
-- Ticket:    T-database-009
-- Purpose:   Reference data the application requires on day one:
--              * 5 delivery tiers (FR-4.1) — flash/express/standard/
--                on_the_way/eco with the canonical SLA + radius matrix
--              * Initial prohibited-items catalog (FR-17.1) — weapons,
--                drugs, alcohol, prescription medication, hazardous
--                materials and a few illustrative sub-items so the
--                Client app has something to render on first launch
-- Notes:     Idempotent. Every row uses ON CONFLICT DO NOTHING against
--            the table's natural unique key, so re-running this migration
--            is a no-op and never overwrites an admin's hand-edited row.
--
--            Commission is a flat 10% across all tiers.
--
--            Test accounts for the P1-P5 personas live in
--            db/seeds/test_accounts.sql; they are NOT in this migration
--            because production must never auto-create them.
-- Refs:      FR-4.1 (tier matrix), FR-17.1 (prohibited items list),
--            BR-2   (flat commission policy).
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- 1. delivery_tiers — five-tier catalog (FR-4.1, BR-2)
--
-- `code` is the stable identifier referenced by application code,
-- API contracts, and the mobile client. `sla_minutes` is NULL for
-- on_the_way (no SLA by design — the Jeeber is already heading that
-- direction). `commission_rate` is stored as a fraction (0.1000 = 10%).
-- `sort_order` controls the display order of the tier-picker UI
-- (FR-4.2): fastest/most-expensive first.
--
-- ON CONFLICT (code) keeps this insert idempotent — re-running the
-- migration will not stomp admin edits to name/sla/radius (those flow
-- through the admin moderation path, not this seed).
-- ---------------------------------------------------------------------
INSERT INTO delivery_tiers (code, name, sla_minutes, radius_metres, commission_rate, sort_order, is_active)
VALUES
    ('flash',      'Flash',       30,    3000, 0.1000, 10, TRUE),
    ('express',    'Express',     60,    7000, 0.1000, 20, TRUE),
    ('standard',   'Standard',   180,   15000, 0.1000, 30, TRUE),
    ('on_the_way', 'On-the-way', NULL,  25000, 0.1000, 40, TRUE),
    ('eco',        'Eco',       1440,   25000, 0.1000, 50, TRUE)
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------
-- 2. prohibited_items — initial moderated catalog (FR-17.1)
--
-- The MVP ships with a representative starter list covering the five
-- categories named explicitly in FR-17.1. The admin UI (FR-prohibited-
-- items) can add/disable rows at runtime — this seed only guarantees
-- the client has *something* to render on first launch so users see
-- the policy before they file a request (FR-17.2 terms prompt).
--
-- Unique index is on LOWER(name); the ON CONFLICT target uses the same
-- expression so re-running the migration is a no-op.
-- ---------------------------------------------------------------------
INSERT INTO prohibited_items (name, category, description, active)
VALUES
    -- weapons
    ('Firearms',                'weapons',                'Pistols, rifles, shotguns, and ammunition of any kind.',                                       TRUE),
    ('Knives and bladed weapons', 'weapons',              'Combat knives, swords, machetes. Standard kitchen knives in sealed retail packaging are permitted.', TRUE),
    ('Explosives and fireworks', 'weapons',               'Including consumer fireworks, flares, and any pyrotechnic devices.',                            TRUE),
    -- drugs
    ('Illegal narcotics',       'drugs',                  'Any controlled substance prohibited under Lebanese law.',                                       TRUE),
    ('Cannabis and derivatives','drugs',                  'Marijuana, hashish, CBD and THC products in any form.',                                         TRUE),
    -- alcohol  (configurable per FR-17.1 — disabled by default; admin enables per market)
    ('Alcoholic beverages',     'alcohol',                'Beer, wine, spirits. Disabled by default; admin may enable in markets where legal.',            FALSE),
    -- prescription medication
    ('Prescription medication', 'prescription_medication','Any medicine requiring a doctor''s prescription. Over-the-counter products are permitted.',     TRUE),
    ('Controlled substances',   'prescription_medication','Schedule II-V drugs, opioids, benzodiazepines.',                                                TRUE),
    -- hazardous materials
    ('Flammable liquids',       'hazardous_materials',    'Gasoline, kerosene, lighter fluid, paint thinners.',                                            TRUE),
    ('Compressed gas cylinders','hazardous_materials',    'Propane tanks, oxygen cylinders, butane canisters.',                                            TRUE),
    ('Corrosive chemicals',     'hazardous_materials',    'Industrial acids, bases, and other corrosive substances.',                                      TRUE),
    ('Radioactive materials',   'hazardous_materials',    'Any item emitting ionising radiation.',                                                         TRUE),
    -- live animals (commonly-requested category that doesn't fit elsewhere)
    ('Live animals',            'other',                  'Live animals of any species; Jeeb does not insure animal transport.',                           TRUE),
    -- cash + financial instruments
    ('Cash and securities',     'other',                  'Bulk cash, bearer bonds, lottery tickets, or other negotiable instruments above $200.',         TRUE),
    -- human remains
    ('Human remains',           'other',                  'Including ashes; transport requires regulated funeral services.',                               TRUE)
ON CONFLICT ((LOWER(name))) DO NOTHING;

INSERT INTO schema_migrations (version)
VALUES ('0011_init_seed_reference_data')
ON CONFLICT (version) DO NOTHING;

COMMIT;
