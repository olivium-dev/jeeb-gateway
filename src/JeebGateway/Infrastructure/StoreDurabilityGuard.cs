using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Infrastructure;

/// <summary>
/// AUDIT-A (durability program, FIX-1) — fail-closed boot guard on the gateway's
/// critical stores of record. Mirrors <see cref="JeebGateway.Tokens.JwtSigningKeyGuard"/>.
///
/// <para>Store selection is spread across ~15 <c>if/else</c> blocks in Program.cs (Postgres
/// connection string set? state-service wired? per-feature UseUpstream flag?). Every one is a
/// <b>silent</b> <c>if(selectorSet){durable}else{inMemory}</c>: drop or typo a single env var on a
/// deploy and a Production gateway boots serving money / identity / audit / legal / security state
/// from process memory — corruptly, with a 200 and a green <c>/health/ready</c>. There is no
/// exception, no log, nothing to page on.</para>
///
/// <para>This guard closes that class in one place. After the DI container is built it inspects the
/// <b>resolved concrete type</b> of each critical interface. In a prod-like environment
/// (anything that is not Development/Testing) it refuses to start — throwing an
/// <see cref="InvalidOperationException"/> that names every offending store — if any critical
/// interface did not resolve to one of its approved durable implementations. Development and Testing
/// are a no-op, so local runs and the integration-test harness keep their in-memory stores and are
/// completely unaffected. The guard changes NO registration; it only asserts what DI produced.</para>
///
/// <para>Matching is by concrete <see cref="Type"/>, not by type-name string: a <c>null</c>
/// resolution (interface unregistered) and an in-memory fallback are both violations, and only a
/// vetted durable implementation passes.</para>
/// </summary>
internal static class StoreDurabilityGuard
{
    /// <summary>
    /// The critical stores of record and, for each, the ONLY concrete implementation(s) that count
    /// as durable in a prod-like environment. A resolved type outside this set (including
    /// <c>null</c> / unregistered, or a silent <c>InMemory*</c> fallback) is a fail-closed violation.
    /// Only stores that already HAVE a durable target are listed; stores with no durable target yet
    /// live on <see cref="KnownInMemoryBacklog"/> (logged loudly, not blocking) until migrated.
    /// </summary>
    internal static readonly (Type Iface, Type[] DurableImpls)[] Critical =
    {
        (typeof(JeebGateway.Financials.ISettlementStore),                   new[] { typeof(JeebGateway.Financials.PostgresSettlementStore) }),
        (typeof(JeebGateway.Financials.ISettlementBatchStore),              new[] { typeof(JeebGateway.Financials.PostgresSettlementBatchStore) }),
        (typeof(JeebGateway.Users.IUsersStore),                             new[] { typeof(JeebGateway.Users.UpstreamBackedUsersStore) }),
        (typeof(JeebGateway.Tokens.IRefreshTokenStore),                     new[] { typeof(JeebGateway.Tokens.StateServiceRefreshTokenStore) }),
        (typeof(JeebGateway.StateService.Idempotency.IIdempotencyStore),    new[] { typeof(JeebGateway.StateService.Idempotency.StateServiceIdempotencyStore) }),
        (typeof(JeebGateway.Disputes.IDisputeStore),                        new[] { typeof(JeebGateway.Disputes.StateServiceDisputeStore) }),
        (typeof(JeebGateway.Disputes.V2.IDisputeCaseStore),                 new[] { typeof(JeebGateway.Disputes.V2.StateServiceDisputeCaseStore) }),
        (typeof(JeebGateway.Availability.IOfferRequestIndex),               new[] { typeof(JeebGateway.StateService.Durable.StateServiceOfferRequestIndex) }),
        (typeof(JeebGateway.Requests.IRequestsStore),                       new[] { typeof(JeebGateway.Requests.DurableRequestsStore) }),
        (typeof(JeebGateway.Admin.IAdminAuditLog),                          new[] { typeof(JeebGateway.Admin.PostgresAdminAuditLog) }),
        (typeof(JeebGateway.Users.IAccountDeletionStore),                   new[] { typeof(JeebGateway.Users.PostgresAccountDeletionStore) }),
        (typeof(JeebGateway.Users.DataExport.IDataExportStore),             new[] { typeof(JeebGateway.Users.DataExport.PostgresDataExportStore) }),
        (typeof(JeebGateway.Requests.OtpHandover.IAdminEscalationStore),    new[] { typeof(JeebGateway.Requests.OtpHandover.PostgresAdminEscalationStore) }),
        (typeof(JeebGateway.ProhibitedItems.FlaggedRequests.IFlaggedRequestStore), new[] { typeof(JeebGateway.ProhibitedItems.FlaggedRequests.PostgresFlaggedRequestStore) }),
        (typeof(JeebGateway.ProhibitedItems.IProhibitedItemsStore),         new[] { typeof(JeebGateway.ProhibitedItems.PostgresProhibitedItemsStore) }),
        (typeof(JeebGateway.Availability.IAvailabilityStore),               new[] { typeof(JeebGateway.Availability.PostgresAvailabilityStore) }),
        (typeof(JeebGateway.Push.IDeviceTokenStore),                        new[] { typeof(JeebGateway.Push.PostgresDeviceTokenStore) }),
        // JEBV4-165 / JEBV4-194 D5 (D1 matrix row 5): saved locations migrated off the gateway's
        // own Postgres (PostgresSavedLocationStore / saved_locations table DELETED) to its owning
        // service, remote-user-preferences (blob key "jeeb.saved_locations"). In a prod-like env it
        // MUST resolve to the remote-backed store, never the InMemory fallback — same treatment as
        // INotificationPreferencesStore -> RemoteUserPreferencesNotificationPreferencesStore.
        (typeof(JeebGateway.Users.SavedLocations.ISavedLocationStore),      new[] { typeof(JeebGateway.Users.SavedLocations.RemoteUserPreferencesSavedLocationStore) }),
        (typeof(JeebGateway.Ratings.IRatingStore),                          new[] { typeof(JeebGateway.Ratings.FeedbackServiceRatingStore) }),
        (typeof(JeebGateway.NotificationPreferences.INotificationPreferencesStore), new[] { typeof(JeebGateway.NotificationPreferences.RemoteUserPreferencesNotificationPreferencesStore) }),
        (typeof(JeebGateway.Requests.Cancellation.IJeeberRestrictionStore), new[] { typeof(JeebGateway.Requests.Cancellation.BanServiceJeeberRestrictionStore) }),
        (typeof(JeebGateway.Auth.OtpSignIn.IOtpRequestRateLimiter),         new[] { typeof(JeebGateway.Auth.OtpSignIn.RedisOtpRequestRateLimiter) }),
        // JEBV4-125 (IN-MEM-LIVE): promoted from the in-memory backlog once the durable target
        // landed. The admin delivery-tier catalog is now Postgres-backed (tiers table, migration
        // 0029); in a prod-like env it MUST resolve to PostgresTiersStore, never InMemoryTiersStore.
        (typeof(JeebGateway.Tiers.ITiersStore),                             new[] { typeof(JeebGateway.Tiers.PostgresTiersStore) }),
        // JEBV4-154 (IN-MEM-LIVE): promoted from the in-memory backlog once the durable target
        // landed. The gateway's financial-ledger anonymization bookkeeping (GDPR account-deletion
        // seam — money + GDPR, the highest-risk remaining in-memory store) is now Postgres-backed
        // (financial_ledger_anonymization table, migration 0030); in a prod-like env it MUST
        // resolve to PostgresFinancialLedger, never InMemoryFinancialLedger.
        (typeof(JeebGateway.Users.IFinancialLedgerAnonymizer),             new[] { typeof(JeebGateway.Users.PostgresFinancialLedger) }),
        // JEBV4-144 / 137 / 136 (IN-MEM-LIVE): the push-reliability trio — dispatch
        // outbox, retry queue, delivery tracker — promoted from the in-memory backlog
        // now that their durable targets landed (push_reliability tables, migration
        // 0031). In a prod-like env each MUST resolve to its Postgres impl, never the
        // in-memory store: a fallback means pending/retrying pushes and delivery-log
        // records evaporate on restart (silently dropped notifications).
        (typeof(JeebGateway.Services.Dispatch.INotificationDispatchOutbox), new[] { typeof(JeebGateway.Services.Dispatch.PostgresNotificationDispatchOutbox) }),
        (typeof(JeebGateway.Push.IPushRetryQueue),                          new[] { typeof(JeebGateway.Push.PostgresPushRetryQueue) }),
        (typeof(JeebGateway.Push.IPushDeliveryTracker),                     new[] { typeof(JeebGateway.Push.PostgresPushDeliveryTracker) }),
        // JEBV4-126 (IN-MEM-LIVE): the voice-note transcription FALLBACK queue —
        // small metadata rows (audio_id, reason, queued_at) for jobs awaiting a
        // re-drive once Whisper recovers — promoted from the in-memory backlog now
        // that its durable target landed (transcription_fallback_queue, migration
        // 0033). In a prod-like env it MUST resolve to PostgresTranscriptionFallbackQueue,
        // never InMemoryTranscriptionFallbackQueue: a fallback means the pending-retry
        // backlog and the health-check/status PendingQueueDepth silently evaporate on
        // restart. (Only the job metadata is durable here — the raw audio bytes are the
        // intentional-transient IAudioStore buffer on the backlog below, NOT this queue.)
        (typeof(JeebGateway.Whisper.ITranscriptionFallbackQueue),           new[] { typeof(JeebGateway.Whisper.PostgresTranscriptionFallbackQueue) }),
        // JEBV4-132 (IN-MEM-LIVE): promoted from the in-memory backlog once the durable target
        // landed. The gateway-owned CMS authoring plane (WS-01 — surfaces, drafts, and the
        // append-only published-version history that drives every MFE config envelope) is now
        // Postgres-backed (cms_surfaces + cms_surface_versions tables, migration 0032); in a
        // prod-like env it MUST resolve to PostgresCmsSurfaceStore, never InMemoryCmsSurfaceStore —
        // a fallback means every admin draft edit and published config version evaporates on
        // restart, flapping the MFEs back to the seeded v1 defaults.
        (typeof(JeebGateway.Cms.ICmsSurfaceStore),                          new[] { typeof(JeebGateway.Cms.PostgresCmsSurfaceStore) }),
        // JEBV4-124 (AUDIT-A guard-gap): the pending-COD-settlement ENQUEUE intent
        // (Financials/ISettlementEnqueueStore) — MONEY-ADJACENT. Its whole contract is
        // idempotency ("no double-enqueue"), yet InMemorySettlementEnqueueStore held that
        // intent in a process ConcurrentDictionary that evaporates on restart, so a bounce
        // could drop the "already enqueued" record and let a delivery be enqueued for
        // settlement twice. Now Postgres-backed (settlement_enqueue table, migration 0034,
        // delivery_id PK + INSERT ON CONFLICT DO NOTHING); in a prod-like env it MUST resolve
        // to PostgresSettlementEnqueueStore, never InMemorySettlementEnqueueStore.
        (typeof(JeebGateway.Financials.ISettlementEnqueueStore),            new[] { typeof(JeebGateway.Financials.PostgresSettlementEnqueueStore) }),
    };

    /// <summary>
    /// Stores that are still in-memory by design because no durable target exists YET (Tier-1
    /// backlog in AUDIT-A). Logged LOUDLY at boot so we never silently forget them, but they do NOT
    /// block startup. As each migration lands, move its interface up to <see cref="Critical"/>.
    /// </summary>
    internal static readonly Type[] KnownInMemoryBacklog =
    {
        // JeebGateway.Tiers.ITiersStore promoted to Critical (JEBV4-125) — durable target
        // PostgresTiersStore + migration 0029 now exist.
        // The push-reliability trio (INotificationDispatchOutbox / IPushRetryQueue /
        // IPushDeliveryTracker) promoted to Critical (JEBV4-144/137/136) — durable
        // targets Postgres* impls + migration 0030 now exist.
        // JeebGateway.Users.IFinancialLedgerAnonymizer promoted to Critical (JEBV4-154) — durable
        // target PostgresFinancialLedger + migration 0030 now exist.
        // JeebGateway.Whisper.ITranscriptionFallbackQueue promoted to Critical (JEBV4-126) —
        // durable target PostgresTranscriptionFallbackQueue + migration 0033 now exist.
        // JeebGateway.Cms.ICmsSurfaceStore promoted to Critical (JEBV4-132) — durable target
        // PostgresCmsSurfaceStore + migration 0032 now exist.
        // JeebGateway.Availability.IGeoIndex moved to IntentionalInMemory (JEBV4-156) — it is a
        // DERIVED, rebuildable hot-path cache (a Redis GEO index), NOT a store of record; see below.
        // JEBV4-133 (INTENTIONAL — transient audio buffer, NOT a store of record):
        // IAudioStore holds the raw voice-note BYTES (WhisperAudio.Content). Large audio
        // blobs deliberately do NOT belong in the gateway Postgres DB — their durable home
        // is the voice-transcription-service's S3-compatible object storage (per IAudioStore's
        // own doc-comment), a reusable microservice the gateway must not reach into or own
        // (org no-coupling law → ESCALATE for a true durable audio home, out of gateway
        // scope). In the gateway it is only a transient in-process buffer of the bytes already
        // in-hand at the moment of fallback (SaveAsync is the ONLY method ever called — there
        // is no GetAsync / drain-back path in the gateway), so it is left in-memory ON PURPOSE.
        // It stays on this backlog (logged loudly) but is NOT pending a gateway-Postgres
        // migration — do not promote it to Critical.
        typeof(JeebGateway.Whisper.IAudioStore),

        // JEBV4-148 (AUDIT-A guard-gap — DURABLE-TARGET-EXISTS-BUT-PROMOTION-OWNER-GATED):
        // the pending-offers ledger (Availability/IPendingOffersStore). When
        // FeatureFlags:UseUpstream:Offer is OFF (today's default) the AUTHORITATIVE store is
        // InMemoryPendingOffersStore — it holds live auction state (pending/accepted/
        // superseded, edit counts, the 20-offer cap, one-live-offer-per-jeeber) in process
        // memory, so a restart drops in-flight bids. That is a real gap, hence it is listed
        // here and logged loudly at boot rather than left silent.
        //
        // It is deliberately NOT promoted to Critical, and NOT given a gateway-Postgres table,
        // because the offer ledger's system of record is the offer-service (Elixir/Phoenix,
        // its OWN Postgres) — the gateway must not own an offers table (org no-coupling law;
        // same reason IUsersStore→UpstreamBackedUsersStore and IOfferRequestIndex→
        // StateServiceOfferRequestIndex are BFF/state-service backed, not gateway-Postgres).
        // The durable path already exists as the thin-BFF UpstreamPendingOffersStore behind
        // FeatureFlags:UseUpstream:Offer. Promoting this interface to Critical(
        // UpstreamPendingOffersStore) would force UseUpstream:Offer=true in prod, but that
        // BFF still throws NotSupportedException for GetAsync / AcceptAsync /
        // AcceptWithSupersedeAsync / TryEditAsync / WithdrawForJeeberAsync (offer-service has
        // no get-by-id or bulk-withdraw-for-jeeber route yet, and accept is driven by
        // OffersController's own auction-close orchestration, not this seam) — so forcing the
        // flag on today would 500 the auto-offline sweeper and the offer-accept lookup path.
        // Promotion is therefore an OWNER decision (confirm prod runs UseUpstream:Offer=true
        // AND offer-service grows the missing read/withdraw routes so the BFF is complete),
        // tracked on JEBV4-148 — not a change this PR can safely make. Until then it stays a
        // loudly-logged known-in-memory gap.
        typeof(JeebGateway.Availability.IPendingOffersStore),
    };

    /// <summary>
    /// Stores that are in-memory <b>by design</b> and are NOT a durability gap: derived,
    /// rebuildable caches / hot-path indexes whose authoritative data lives in a durable store of
    /// record elsewhere. Unlike <see cref="KnownInMemoryBacklog"/> (stores of record still awaiting a
    /// Postgres target), these have NO pending migration — migrating them to Postgres would be wrong.
    /// They are logged at boot as informational (rebuildable, not lost-forever) and never block startup.
    ///
    /// <para><b>JEBV4-156 — IGeoIndex is intentional in-memory (derived/rebuildable cache, not a
    /// store of record).</b> Per db/JEEBER_LOCATION_DESIGN.md, the Jeeber online-presence system of
    /// record is the durable Postgres <c>jeeber_availability</c> table (is_online, vehicle_type,
    /// <c>last_location GEOGRAPHY(Point,4326)</c>, last_seen_at) — owned by
    /// <see cref="JeebGateway.Availability.IAvailabilityStore"/>, which is ALREADY a
    /// <see cref="Critical"/> Postgres-backed store (<see cref="JeebGateway.Availability.PostgresAvailabilityStore"/>).
    /// <see cref="JeebGateway.Availability.IGeoIndex"/> is only the spatial ACCELERATION index over
    /// that truth — its production target is a <b>Redis</b> GEO sorted set
    /// (<c>jeeber:online:geo</c> + per-vehicle sets, GEOADD/GEOSEARCH), explicitly a hot-path cache,
    /// not Postgres. PostgresAvailabilityStore.AddAsync writes the durable row and then updates the
    /// derived geo index, so the index is fully rebuildable from Postgres and its loss on restart
    /// costs only a warm-up, never authoritative data. Migrating it to a durable Postgres store would
    /// duplicate <c>jeeber_availability</c> and defeat the whole point of the hot path. It therefore
    /// stays in-memory (Redis in prod) by design and is deliberately NOT on the migration backlog.</para>
    ///
    /// <para><b>JEBV4-143 — ILocationStore is intentional in-memory (derived/rebuildable
    /// hot-path cache, not a store of record).</b> <see cref="JeebGateway.Tracking.ILocationStore"/>
    /// is the per-Jeeber "latest non-expired GPS fix, with a TTL" — a synchronous, lock-free hot
    /// path sized for the 50k-updates/min location firehose (per db/JEEBER_LOCATION_DESIGN.md).
    /// Its authoritative last-known location is the DURABLE Postgres <c>jeeber_availability</c>
    /// table (<c>last_location GEOGRAPHY(Point,4326)</c>, <c>last_seen_at</c>), owned by
    /// <see cref="JeebGateway.Availability.IAvailabilityStore"/> — ALREADY a <see cref="Critical"/>
    /// Postgres-backed store (<see cref="JeebGateway.Availability.PostgresAvailabilityStore"/>) —
    /// promoted from Redis by the debounced flusher. The design doc is explicit that per-second
    /// location updates deliberately do NOT pay Postgres durability at that frequency ("Hot path
    /// lives in Redis"); the production target of this store is Redis (<c>SET jeeber:{id}:position
    /// … EX 300</c>), and a durable upstream ALSO exists behind a flag
    /// (<see cref="JeebGateway.Tracking.GeoServiceLocationStore"/> → the shared geolocation-service,
    /// <c>FeatureFlags:UseUpstream:Geolocation</c>). Losing the in-memory latest fix on restart
    /// costs only a warm-up until the next heartbeat re-populates it — never authoritative data —
    /// so this is NOT a durability gap. Migrating the per-ping firehose to a synchronous
    /// gateway-Postgres write would be the write-amplification anti-pattern the design doc rejects.
    /// It therefore stays in-memory (Redis in prod) by design and is deliberately NOT on the
    /// migration backlog — the exact IGeoIndex (JEBV4-156) treatment.</para>
    /// </summary>
    internal static readonly Type[] IntentionalInMemory =
    {
        typeof(JeebGateway.Availability.IGeoIndex),
        typeof(JeebGateway.Tracking.ILocationStore),
    };

    /// <summary>
    /// Dev + CI/integration-tests legitimately use in-memory stores; the guard is a no-op there.
    /// Everything else (Production, Staging, …) is prod-like and armed.
    /// </summary>
    internal static bool IsExempt(IHostEnvironment environment)
        => environment is null
           || environment.IsDevelopment()
           || string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Config key for the NARROW, prod-safe test-harness escape hatch. DEFAULT FALSE = the fail-closed
    /// boot gate is ARMED. It exists ONLY so the integration-test harness can boot a
    /// <c>ASPNETCORE_ENVIRONMENT=Production</c> <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/>
    /// (to exercise prod-only controller behaviour) WITHOUT provisioning real Postgres/Redis/upstream
    /// durable stores. The real appsettings / real environment NEVER sets it, so real Production stays
    /// fail-closed. Matching by concrete resolved type is unchanged; this flag only decides whether the
    /// gate is armed at all in a prod-like env.
    /// </summary>
    internal const string FailClosedDisabledKey = "StoreDurability:FailClosedDisabled";

    /// <summary>
    /// Reads the test-harness escape hatch (<see cref="FailClosedDisabledKey"/>) off the built
    /// container's <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>. Fails SAFE: if the
    /// provider has no IConfiguration (e.g. the unit-test <c>MapServiceProvider</c>) or the flag is
    /// absent/unparseable, the gate stays ARMED (returns false). Only a literal <c>true</c> disables it.
    /// </summary>
    internal static bool IsFailClosedDisabled(IServiceProvider services)
    {
        var config = services?.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration))
            as Microsoft.Extensions.Configuration.IConfiguration;
        var raw = config?[FailClosedDisabledKey];
        return bool.TryParse(raw, out var disabled) && disabled;
    }

    /// <summary>
    /// Pure decision core (unit-testable without a real container or DB): given a resolver
    /// interface-&gt;concrete-type, returns a human-readable violation per critical store that did
    /// not resolve to an approved durable implementation. Empty list == all durable.
    /// </summary>
    internal static IReadOnlyList<string> Evaluate(Func<Type, Type?> resolve)
    {
        var violations = new List<string>();
        foreach (var (iface, durable) in Critical)
        {
            var impl = resolve(iface);
            if (impl is null || !durable.Contains(impl))
            {
                violations.Add(
                    $"{iface.Name} resolved to '{impl?.Name ?? "<null/unregistered>"}' " +
                    $"(expected durable: {string.Join(" or ", durable.Select(d => d.Name))})");
            }
        }
        return violations;
    }

    /// <summary>
    /// Fail-closed boot gate. Call once immediately after <c>builder.Build()</c>. No-op in
    /// Development/Testing. In a prod-like environment, logs the known-in-memory backlog and then
    /// throws (refusing to start) if any critical store fell back to in-memory / went unregistered.
    /// </summary>
    public static void EnsureDurable(IServiceProvider services, IHostEnvironment environment, ILogger logger)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (IsExempt(environment))
        {
            return; // dev/CI keep in-memory stores — never block local or test boot.
        }

        // NARROW test-harness escape hatch (default OFF). The gate is armed in prod-like envs UNLESS
        // StoreDurability:FailClosedDisabled=true. Only the integration-test ProdFactory sets it (via
        // UseSetting) so its Production-env WebApplicationFactory can boot without real durable stores;
        // real Production never sets it → still fail-closed. This weakens NOTHING in real prod.
        if (IsFailClosedDisabled(services))
        {
            logger?.LogWarning(
                "StoreDurability: fail-closed boot gate DISABLED in '{Env}' via {Key}=true. This is the " +
                "TEST-HARNESS-ONLY escape hatch — real Production must NEVER set it. In-memory critical " +
                "stores will NOT block boot.",
                environment.EnvironmentName, FailClosedDisabledKey);
            return;
        }

        foreach (var iface in KnownInMemoryBacklog)
        {
            logger?.LogWarning(
                "StoreDurability: {Interface} is still in-memory (no durable target yet) — its data is lost on restart. Tracked on the AUDIT-A Tier-1 backlog.",
                iface.Name);
        }

        foreach (var iface in IntentionalInMemory)
        {
            logger?.LogInformation(
                "StoreDurability: {Interface} is in-memory BY DESIGN — a derived, rebuildable hot-path cache whose authoritative data lives in a durable store of record (not a durability gap; no migration pending).",
                iface.Name);
        }

        var violations = Evaluate(iface => services.GetService(iface)?.GetType());
        if (violations.Count == 0)
        {
            logger?.LogInformation(
                "StoreDurability: all {Count} critical stores resolved to durable implementations in '{Env}'.",
                Critical.Length, environment.EnvironmentName);
            return;
        }

        throw new InvalidOperationException(
            $"FAIL-CLOSED: {violations.Count} critical store(s) resolved to in-memory in the prod-like " +
            $"environment '{environment.EnvironmentName}'. A durability selector (GatewayPostgres__ConnectionString, " +
            "JeebStateService__*, a Redis connection string, or a UseUpstream feature flag) is unset or wrong — " +
            "refusing to serve corrupt/ephemeral money/identity/audit/legal state with a green health check:\n  - " +
            string.Join("\n  - ", violations));
    }
}

/// <summary>
/// Readiness surface for the boot gate (AUDIT-A). Belt-and-suspenders on top of
/// <see cref="StoreDurabilityGuard.EnsureDurable"/>: the boot gate crashes a mis-provisioned prod
/// deploy before it serves; this "ready"-tagged check gives ops a probe target and catches a
/// <em>runtime</em> re-resolution/degradation. Reports Unhealthy (→ /health/ready 503) if any
/// critical store is in-memory in a prod-like environment. No-op-Healthy in Development/Testing.
/// </summary>
internal sealed class StoreDurabilityHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _services;
    private readonly IHostEnvironment _environment;

    public StoreDurabilityHealthCheck(IServiceProvider services, IHostEnvironment environment)
    {
        _services = services;
        _environment = environment;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (StoreDurabilityGuard.IsExempt(_environment))
        {
            return Task.FromResult(HealthCheckResult.Healthy("store-durability: exempt (Development/Testing)"));
        }

        var violations = StoreDurabilityGuard.Evaluate(iface => _services.GetService(iface)?.GetType());
        if (violations.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                $"store-durability: all {StoreDurabilityGuard.Critical.Length} critical stores durable"));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy(
            "store-durability: " + string.Join("; ", violations)));
    }
}
