# Chat Retention Policy

Ticket: **T-database-004**
Scope:  `chat_messages` table (introduced in `migrations/0002_init_chat_messages.sql`)

## Policy

Chat messages are retained for **90 days** from `created_at`. After that, rows
are **hard-deleted** — Jeeb's product policy is to honour the retention window
strictly for privacy/compliance reasons, so there is no soft-delete column and
no archival tier.

## Ownership and cadence

| Field      | Value                                                       |
|------------|-------------------------------------------------------------|
| Owner      | Jeeb platform team (gateway DB)                             |
| Cadence    | Daily at **03:00 UTC** (low-traffic window)                 |
| Mechanism  | External scheduled job (k8s CronJob / Oban / pg_cron)       |
| Re-runs    | Idempotent — running multiple times in one day is harmless  |

## SQL

The reaper job executes:

```sql
DELETE FROM chat_messages
WHERE created_at < NOW() - INTERVAL '90 days';
```

The `chat_messages_created_at_idx` btree index keeps this scan cheap.

## Media tombstoning

`chat_messages.media_url` only stores a **URL into object storage** (S3 / R2 /
MinIO); the DB does not own the binary content. When the reaper deletes rows,
it must also emit tombstones into the object-storage lifecycle pipeline so the
underlying media blobs are deleted in lock-step. Otherwise the DB and the
bucket drift apart and stale media survives past the retention window.

Implementation outline (executed inside the same reaper job):

1. `SELECT id, media_url FROM chat_messages WHERE created_at < NOW() - INTERVAL '90 days' AND media_url IS NOT NULL;`
2. For each `media_url`, emit a delete-marker to the object-storage tombstone
   topic (or call the bucket's delete API directly if synchronous).
3. Then run the `DELETE` above to remove the rows.

Step 1+2 before step 3 ensures we never lose the URL→blob pointer before the
blob is queued for deletion.

## Why no built-in scheduler

`pg_cron` is not enabled on the gateway DB by default. The retention job is
intentionally **external** so that:

- Retention is observable via the same job-runner dashboards as other reapers.
- A failed run pages the platform on-call, not silently disappears inside the DB.
- The media-tombstone step (which calls out to object storage) is naturally
  expressed in application code, not in SQL.
