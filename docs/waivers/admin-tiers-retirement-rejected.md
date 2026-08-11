# Audit — proposal to retire `/admin/tiers` writes: REJECTED

**Status:** rejected, no code change
**Recorded:** 2026-08-11
**Proposal:** disable `POST`/`PUT`/`DELETE /admin/tiers` with `410 Gone`, on the
premise that the CRUD "writes a catalog nothing reads since #375".

## Verdict

**The premise is false, and inverted.** The tier catalog is read on the live hot
path, and PR #375 made it *more* load-bearing, not less. Returning `410` from the
write endpoints would freeze the catalog permanently with no administrative path
to change it, and would regress the D2 push fan-out that #375 had just fixed.

No code was changed. This note records the audit so the proposal is not retried.

## What actually reads the catalog

`AdminTiersController` writes `JeebGateway.Tiers.ITiersStore`
(`InMemoryTiersStore`, registered in `Program.cs`). Non-test readers of that
store on `main` @ `fb52a2d`:

| Reader | What it uses the catalog for |
| --- | --- |
| `Tiers/TierCatalogResolver.cs` | `gateway-local` taxonomy — the fallback arm of the D2 tier resolution added by #375 |
| `Requests/TierExpiryWindowResolver.cs` | reads `RequestTtlSeconds` per tier to build the TTL map |
| `Requests/RequestExpirySweeper.cs` | resolves the TTL map, derives the scan cutoff, expires pending requests |
| `Requests/RequestNudgeSweeper.cs` | same catalog, no-offer nudge windows |
| `Requests/OfferDeadlineProjector.cs` | projects offer deadlines |
| `Matching/DeliveryRowMirror.cs` | mirrors tier onto delivery rows |
| `Controllers/TiersController.cs` | public `GET /tiers` read surface |
| `Controllers/RequestVoiceController.cs` | tier validation on the voice request path |

`TierExpiryWindowResolver` is explicit:

```csharp
var localCatalog = await tiers.ListAsync(ct);
foreach (var tier in localCatalog.Where(t => t.RequestTtlSeconds > 0))
    merged[tier.Id] = TimeSpan.FromSeconds(tier.RequestTtlSeconds);
```

The sweeper then derives its scan window from that map. A frozen catalog means a
frozen request-expiry policy.

## What #375 actually did

PR #375 — *"resolve the tier radius against the catalog the tier-picker rendered
from (GUID tier ids)"* — introduced `ITierCatalogResolver`, consumed by
`NewRequestPushNotifier`, `JeebFeedController` and `RequestOffersController`.

`TierCatalogResolver.SnapshotAsync` resolves upstream first and **falls back to
the local catalog**, tagging the snapshot `"gateway-local"`:

```csharp
return new TierCatalogSnapshot(await _catalog.ListAsync(ct), "gateway-local");
```

Every D2 branch fails **closed** on an unresolved tier — no fan-out push, empty
feed, `409` on the offer route. So the local catalog is the safety net for the
exact bug #375 fixed. Retiring its only write path removes the ability to repair
that net.

## The CMS question

The brief's conditional was "check whether jeeb-cms calls those endpoints".
It does not — `grep` for `/admin/tiers` across `jeeb-cms` (all sources,
`node_modules` excluded) returns **no call sites**. The only tier references are
a `tier_id` field on delivery DTOs, an `OrderAdminListItem.tier` filter on the
admin-orders list, and OMDS `StatusChip` tier styling.

But "the CMS does not call it" is not "nothing reads the catalog". The CMS is
merely not the *writer*; the gateway itself is the *reader*. The conditional's
two branches — disable the endpoints, or hide a dead CMS surface — both rest on
the false premise, and neither applies:

- Disabling the writes would break gateway-internal policy reads.
- There is no dead CMS surface to hide; the CMS never exposed tier CRUD.

## If retirement is still wanted

The catalog needs an owner before its write path can be removed. The
architecture ownership review already assigns this: point 15 `delivery-tiers`,
category Core, status **approved** — tiers belong to delivery-service, which
already serves the UUID taxonomy behind `FeatureFlags:UseUpstream:Delivery`.

The correct sequence is migrate-then-retire:

1. Make delivery-service the sole tier authority, including `RequestTtlSeconds`.
2. Cut the gateway's local readers over to the resolver's upstream arm only.
3. Delete `InMemoryTiersStore`, `AdminTiersController` and the local fallback
   together, in one PR.

A `410` on the writes while the readers still point at the local store would be
step 3 without steps 1 and 2.

## Related

`Tiers.AdminTierTtlGuardTests.T3_2_Acknowledged_Ttl_Change_Applies_And_Shortens_The_Live_Deadline`
is one of the 12 tests failing on `main` — see
[`pr-373-merged-on-red.md`](./pr-373-merged-on-red.md). It is pre-existing and
unrelated to this audit, but it is a live TTL-path test and is worth fixing
before anyone touches tier TTL behaviour.
