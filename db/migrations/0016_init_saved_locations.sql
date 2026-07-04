-- =====================================================================
-- Migration: 0016_init_saved_locations
-- Ticket:    ACCT-04 / REQ-02 durability follow-up
-- Purpose:   Durable backing store for per-user saved locations ("Home",
--            "Work", pinned drop-off points shown in the client's saved
--            places picker). Replaces InMemorySavedLocationStore
--            (src/JeebGateway/Users/SavedLocations/), whose rows evaporated
--            on every gateway restart / replica move (ADR-0001: the
--            gateway is stateless — it must hold no per-user row in
--            process memory).
-- Notes:     Idempotent — CREATE TABLE/INDEX IF NOT EXISTS, safe to re-run.
--
--            NOT a reuse of saved_addresses (0006), and NOT a reuse of
--            that table's future consumer either. saved_addresses backs a
--            *different* feature — IUsersStore.ListAddressesAsync /
--            SavedAddress (src/JeebGateway/Users/UserProfile.cs,
--            IUsersStore.cs, still on InMemoryUsersStore) — a separate
--            durability target with its own model. Even setting that
--            ownership concern aside, saved_addresses' shape does not fit
--            ISavedLocationStore: its user_id is UUID NOT NULL REFERENCES
--            users(id), but SavedLocationsController.TryGetUserId accepts
--            any bearer-claim/X-User-Id string, never validated as a
--            users.id UUID; and its line1 (address) column is NOT NULL
--            with a non-blank CHECK, but CreateSavedLocationRequest.Address
--            carries no [Required] — SavedLocationsEndpointTests.
--            Create_Persists_And_Is_Listable already creates a location
--            with no address at all, which a NOT NULL line1 would reject.
--            saved_locations instead mirrors the sibling durability
--            migration device_tokens (0017): user_id TEXT, no FK, no
--            assumption about upstream identity format; label/address stay
--            plain columns with no uniqueness constraint (the in-memory
--            store never enforced unique labels per user, so adding one
--            here would be a behaviour regression, not a strict superset).
--
--            latitude/longitude are DOUBLE PRECISION (not NUMERIC, unlike
--            saved_addresses) to match SavedLocation.Latitude/Longitude
--            (C# double) with no decimal<->double conversion at the store
--            boundary.
--
--            is_default is enforced at most-one-per-user via a partial
--            unique index, exactly like saved_addresses. The gateway store
--            (PostgresSavedLocationStore) runs a clear-then-set UPDATE
--            inside the same transaction as the write that flips it, per
--            that table's own documented UPDATE-then-INSERT pattern, so
--            the invariant is never violated mid-request.
-- Refs:      ACCT-04, REQ-02.
-- =====================================================================

BEGIN;

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

CREATE TABLE IF NOT EXISTS saved_locations (
    id          UUID             PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     TEXT             NOT NULL,
    label       VARCHAR(80)      NOT NULL,
    address     VARCHAR(256)     NULL,
    latitude    DOUBLE PRECISION NOT NULL,
    longitude   DOUBLE PRECISION NOT NULL,
    is_default  BOOLEAN          NOT NULL DEFAULT FALSE,
    created_at  TIMESTAMPTZ      NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ      NOT NULL DEFAULT now(),

    CONSTRAINT saved_locations_label_nonblank
        CHECK (char_length(btrim(label)) > 0),
    CONSTRAINT saved_locations_lat_range
        CHECK (latitude  >= -90  AND latitude  <= 90),
    CONSTRAINT saved_locations_lng_range
        CHECK (longitude >= -180 AND longitude <= 180)
);

-- Primary read path: ListAsync/GetAsync are always scoped to one user;
-- created_at backs the "oldest remaining" delete-promotion query too.
CREATE INDEX IF NOT EXISTS idx_saved_locations_user
    ON saved_locations (user_id, created_at);

-- REQ-02: exactly one default ("my location") per user.
CREATE UNIQUE INDEX IF NOT EXISTS uq_saved_locations_one_default_per_user
    ON saved_locations (user_id)
    WHERE is_default = TRUE;

INSERT INTO schema_migrations (version)
VALUES ('0016_init_saved_locations')
ON CONFLICT (version) DO NOTHING;

COMMIT;
