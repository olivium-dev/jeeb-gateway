# ADR 0014 — Wallet independence and non-breaking evolution

Status: owner-ruled principle; offline G0 draft from cached 6679f6e. No rollout authorized.

Wallet-service owns its data and HTTP API. Gateway access is through the pinned contract and generated client, never a wallet database connection. Shared wallet-service source remains product-agnostic and additive; no sibling deployment follows from a gateway change.

Consumer routes, fields, statuses, problem types and push payloads must remain backward compatible. Added fields are optional; old mobile builds retain safe behavior. Path/method retention checks are necessary but do not prove field or semantic compatibility: endpoint and old-build tests remain required.

Behavior changes ship with current defaults: legacy mode, holds off, unlimited cap, current currency behavior and commission collection dormant. Environment activation, money migration, data backfill and deployments require separate owner gates. New health invariants degrade before a later, separately approved throw/default flip.

Migrations are HTTP-only, dry-run by default, idempotent and reversible by compensating transaction. Retain active source wallets during the reversible phase; an inactive source cannot be recreated through holder/ensure. Staging must explicitly pin Partner currency and both Commission currency settings after its own verified migration.

Preparation follows G0 → G1 → G2 → G3 → G4 → G5 → G6. Reconcile P01 first when integrating shared gateway files. G6 is not prepared as an active default flip without both environments' required migration and health evidence. Wallet authentication remains a separately deferred rollout.

G0 adds a nonempty wallet-slice guard, required operation and fee-default checks, reviewed-base path/method retention and pinned-generator freshness CI. The 28-path source slice is retained byte-for-byte; the epic's empty placeholder is rejected. The cached client's regeneration already produces no diff, so no hand-edited generated code is introduced.

This draft does not establish current published-swagger compatibility, full SDK-pinned CI, all eight program gates, old-device behavior or runtime rollout acceptance. Those remain explicit integration requirements, not inferred from source-only checks.
