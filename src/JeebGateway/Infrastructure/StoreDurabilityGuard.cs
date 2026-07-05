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
        (typeof(JeebGateway.Users.SavedLocations.ISavedLocationStore),      new[] { typeof(JeebGateway.Users.SavedLocations.PostgresSavedLocationStore) }),
        (typeof(JeebGateway.Ratings.IRatingStore),                          new[] { typeof(JeebGateway.Ratings.FeedbackServiceRatingStore) }),
        (typeof(JeebGateway.NotificationPreferences.INotificationPreferencesStore), new[] { typeof(JeebGateway.NotificationPreferences.RemoteUserPreferencesNotificationPreferencesStore) }),
        (typeof(JeebGateway.Requests.Cancellation.IJeeberRestrictionStore), new[] { typeof(JeebGateway.Requests.Cancellation.BanServiceJeeberRestrictionStore) }),
        (typeof(JeebGateway.Auth.OtpSignIn.IOtpRequestRateLimiter),         new[] { typeof(JeebGateway.Auth.OtpSignIn.RedisOtpRequestRateLimiter) }),
        // JEBV4-125 (IN-MEM-LIVE): promoted from the in-memory backlog once the durable target
        // landed. The admin delivery-tier catalog is now Postgres-backed (tiers table, migration
        // 0029); in a prod-like env it MUST resolve to PostgresTiersStore, never InMemoryTiersStore.
        (typeof(JeebGateway.Tiers.ITiersStore),                             new[] { typeof(JeebGateway.Tiers.PostgresTiersStore) }),
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
        typeof(JeebGateway.Cms.ICmsSurfaceStore),
        typeof(JeebGateway.Services.Dispatch.INotificationDispatchOutbox),
        typeof(JeebGateway.Push.IPushRetryQueue),
        typeof(JeebGateway.Push.IPushDeliveryTracker),
        typeof(JeebGateway.Users.IFinancialLedgerAnonymizer),
        typeof(JeebGateway.Availability.IGeoIndex),
        typeof(JeebGateway.Whisper.IAudioStore),
        typeof(JeebGateway.Whisper.ITranscriptionFallbackQueue),
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

        foreach (var iface in KnownInMemoryBacklog)
        {
            logger?.LogWarning(
                "StoreDurability: {Interface} is still in-memory (no durable target yet) — its data is lost on restart. Tracked on the AUDIT-A Tier-1 backlog.",
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
