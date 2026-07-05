-- =====================================================================
-- Migration: 0030_init_push_reliability
-- Ticket:    JEBV4-144 / 137 / 136 (AUDIT-A IN-MEM-LIVE) durability follow-up
-- Purpose:   Durable backing store for the gateway's push-notification
--            reliability trio, all of which used to live ONLY in gateway
--            process memory and so LOST every pending/retrying push and
--            every delivery-tracking record on each restart / replica move
--            (silently dropped notifications):
--
--            1. notification_dispatch_outbox  (INotificationDispatchOutbox,
--               InMemoryNotificationDispatchOutbox → PostgresNotificationDispatchOutbox)
--               The render→dispatch outbox: idempotency de-dup, retry
--               scheduling and DLQ for JeebNotificationDispatcher (JEB-1494).
--
--            2. push_retry_queue              (IPushRetryQueue,
--               InMemoryPushRetryQueue → PostgresPushRetryQueue)
--               The single 30-second retry path (T-backend-022 AC): entries
--               that failed their first send wait here for exactly one retry,
--               drained-and-removed by PushRetryQueueProcessor (retried once,
--               never re-enqueued).
--
--            3. push_delivery_tracker         (IPushDeliveryTracker,
--               InMemoryPushDeliveryTracker → PostgresPushDeliveryTracker)
--               Append-only log of every push delivery outcome so the ops
--               dashboard / SLO analytics can answer "did this user receive
--               the push?" and "what's the retry-path save rate?".
--
--            These are gateway-OWNED reliability plumbing (the gateway is the
--            notification composer per the org no-coupling law — there is no
--            upstream push-transport service that owns them yet), so their
--            durable home is gateway Postgres, alongside the other AUDIT-A
--            durability tables (settlements 0015, tiers 0029, …).
--
-- Notes:     Idempotent — CREATE TABLE/INDEX IF NOT EXISTS only, no seed data
--            (these are runtime queues/logs, not reference data), so re-running
--            is a no-op and never disturbs in-flight rows.
--
--            Concurrency-safety: the outbox GetDue claim and the retry-queue
--            drain both use FOR UPDATE SKIP LOCKED / atomic DELETE … RETURNING
--            so multiple gateway replicas never double-process the same row.
--            Column shapes mirror the in-memory records byte-for-byte:
--              * parameters / request are JSONB (Dictionary<string,string> and
--                the full PushNotificationRequest serialized with System.Text.Json).
--              * status / outcome / trigger are TEXT storing the enum member
--                name (Pending/Delivered/DLQ, Delivered/QueuedForRetry/…,
--                NewOffer/StatusChange/…) exactly as the enum ToString() emits,
--                so a round-trip is lossless without an ordinal-drift risk.
-- Refs:      JEBV4-144 (NotificationDispatchOutbox), JEBV4-137 (PushRetryQueue),
--            JEBV4-136 (PushDeliveryTracker), T-backend-022 (push retry),
--            JEB-1494 (notification dispatch primitive).
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- 1. notification_dispatch_outbox — JEBV4-144
--    Mirrors NotificationDispatchEntry: idempotency de-dup, retry
--    scheduling (next_attempt_at) and DLQ (status='DLQ' after max attempts).
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS notification_dispatch_outbox (
    id                UUID          PRIMARY KEY,
    template_key      TEXT          NOT NULL,
    locale            TEXT          NOT NULL,
    parameters        JSONB         NOT NULL DEFAULT '{}'::jsonb,
    recipient_user_id UUID          NOT NULL,
    idempotency_key   TEXT,
    status            TEXT          NOT NULL DEFAULT 'Pending',
    attempt_count     INT           NOT NULL DEFAULT 0,
    created_at        TIMESTAMPTZ   NOT NULL DEFAULT now(),
    next_attempt_at   TIMESTAMPTZ,
    last_error        TEXT
);

-- Idempotency de-dup (ExistsAsync). Partial unique so many rows may have a
-- NULL key but a supplied key can appear at most once — the durable form of
-- the in-memory Ordinal key match, plus a race backstop on concurrent inserts.
CREATE UNIQUE INDEX IF NOT EXISTS uq_notif_outbox_idempotency_key
    ON notification_dispatch_outbox (idempotency_key)
    WHERE idempotency_key IS NOT NULL;

-- GetDue claim path: Pending rows whose next_attempt_at is null/past, oldest
-- first (FIFO). Partial index keeps it tight as Delivered/DLQ rows accumulate.
CREATE INDEX IF NOT EXISTS idx_notif_outbox_due
    ON notification_dispatch_outbox (next_attempt_at, created_at)
    WHERE status = 'Pending';

-- DLQ observability read (GetDlqAsync).
CREATE INDEX IF NOT EXISTS idx_notif_outbox_dlq
    ON notification_dispatch_outbox (created_at)
    WHERE status = 'DLQ';

-- ---------------------------------------------------------------------
-- 2. push_retry_queue — JEBV4-137
--    Single 30s retry hold. Drained-and-removed atomically; never re-enqueued.
--    Ordering is by id (insertion order) as a stable tiebreak within a drain.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS push_retry_queue (
    id             BIGINT        GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    request        JSONB         NOT NULL,
    due_at         TIMESTAMPTZ   NOT NULL,
    failure_reason TEXT          NOT NULL DEFAULT '',
    created_at     TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- Drain path: every entry whose due_at is in the past.
CREATE INDEX IF NOT EXISTS idx_push_retry_due
    ON push_retry_queue (due_at);

-- ---------------------------------------------------------------------
-- 3. push_delivery_tracker — JEBV4-136
--    Append-only delivery-outcome log. Never updated after insert.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS push_delivery_tracker (
    id            BIGINT        GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    user_id       TEXT          NOT NULL,
    trigger       TEXT          NOT NULL,
    outcome       TEXT          NOT NULL,
    attempts_made INT           NOT NULL DEFAULT 0,
    reason        TEXT,
    created_at    TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- "did THIS user receive the push?" (GetForUserAsync).
CREATE INDEX IF NOT EXISTS idx_push_delivery_user
    ON push_delivery_tracker (user_id, id DESC);

-- "most recent N outcomes" for the ops dashboard (GetRecentAsync).
CREATE INDEX IF NOT EXISTS idx_push_delivery_recent
    ON push_delivery_tracker (id DESC);

INSERT INTO schema_migrations (version)
VALUES ('0030_init_push_reliability')
ON CONFLICT (version) DO NOTHING;

COMMIT;
