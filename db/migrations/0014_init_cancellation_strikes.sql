-- JEB-1507: gateway-local cancellation strike ledger
--
-- Persists the Jeeber progressive-ban counter so the rolling 7-day
-- cancellation count survives pod restarts. The in-memory
-- InMemoryJeeberRestrictionStore is kept as the development/test fallback;
-- this table is the durable backing store for production.
--
-- Index: user_id + week_number covers the "count strikes in this ISO week"
-- query pattern used by the cancellation policy evaluation.

CREATE TABLE IF NOT EXISTS jeeb_cancellation_strikes (
    id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id       UUID        NOT NULL,
    delivery_id   UUID        NOT NULL,
    struck_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    week_number   INT         NOT NULL,

    CONSTRAINT uq_cancellation_strike_delivery
        UNIQUE (delivery_id)
);

CREATE INDEX IF NOT EXISTS idx_strikes_user_week
    ON jeeb_cancellation_strikes (user_id, week_number);

COMMENT ON TABLE jeeb_cancellation_strikes IS
    'Gateway-local Jeeber cancellation strike ledger (JEB-1507). '
    'One row per delivery where the Jeeber cancelled; week_number is '
    'the ISO-8601 week number used for the rolling 7-day threshold.';
