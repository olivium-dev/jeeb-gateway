using System.Collections.Concurrent;
using JeebGateway.Admin;
using JeebGateway.Availability;
using JeebGateway.Financials;
using JeebGateway.Disputes;
using JeebGateway.Disputes.V2;
using JeebGateway.NotificationPreferences;
using JeebGateway.Notifications;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.Push;
using JeebGateway.Ratings;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.StateService.Idempotency;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using JeebGateway.Tokens;
using JeebGateway.Tracking;
using JeebGateway.Users;
using JeebGateway.Users.SavedLocations;
using JeebGateway.IntegrationTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Default integration host with explicit test-owned state. This shadows the
/// framework factory inside the test namespace so a bare factory can exercise
/// gateway behavior without production secretly selecting local stores.
/// Individual tests can still replace any registration afterward through
/// WithWebHostBuilder/ConfigureTestServices.
/// </summary>
public class WebApplicationFactory<TEntryPoint>
    : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    private readonly List<Action<IWebHostBuilder>> _additionalConfiguration = new();

    // The framework method returns the framework base type. Preserve this
    // test-owned factory (and its explicit owners) while retaining the familiar
    // fluent API used throughout the suite.
    public new WebApplicationFactory<TEntryPoint> WithWebHostBuilder(
        Action<IWebHostBuilder> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var clone = new WebApplicationFactory<TEntryPoint>();
        clone._additionalConfiguration.AddRange(_additionalConfiguration);
        clone._additionalConfiguration.Add(configuration);
        return clone;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DELIVERY_SERVICE_TOKEN", new string('t', 48));
        builder.ConfigureServices((context, services) =>
        {
            if (context.HostingEnvironment.IsEnvironment("Testing")
                || context.HostingEnvironment.IsDevelopment())
            {
                UseExplicitTestOwners(services);
            }
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        foreach (var configure in _additionalConfiguration)
            builder.ConfigureWebHost(configure);
        return base.CreateHost(builder);
    }

    internal static void UseExplicitTestOwners(IServiceCollection services)
    {
        OwnerServiceFakes.UseInMemoryUsers(services);
        OwnerServiceFakes.AllowAllAccounts(services);
        OwnerServiceFakes.UseSeededModerationCatalog(services);
        services.RemoveAll<IUserManagementDualRoleClient>();
        services.RemoveAll<TestUserManagementDualRoleClient>();
        services.AddSingleton<TestUserManagementDualRoleClient>();
        services.AddSingleton<IUserManagementDualRoleClient>(sp =>
            sp.GetRequiredService<TestUserManagementDualRoleClient>());
        services.Configure<UpstreamFeatureFlags>(flags =>
            flags.UserManagement = true);

        ReplaceSingleton<IRequestsStore, InMemoryRequestsStore>(services);
        services.RemoveAll<InMemoryRequestsStore>();
        services.AddSingleton<InMemoryRequestsStore>();
        services.RemoveAll<IRequestsStore>();
        services.AddSingleton<IRequestsStore>(sp => sp.GetRequiredService<InMemoryRequestsStore>());

        ReplaceSingleton<IOfferRequestIndex, InMemoryOfferRequestIndex>(services);
        services.RemoveAll<IPendingOffersStore>();
        services.RemoveAll<FakePendingOffersStore>();
        services.AddSingleton<FakePendingOffersStore>();
        services.AddSingleton<IPendingOffersStore>(sp =>
            sp.GetRequiredService<FakePendingOffersStore>());

        ReplaceSingleton<IAvailabilityStore, InMemoryAvailabilityStore>(services);
        ReplaceSingleton<JeebGateway.Tiers.ITiersStore, InMemoryTiersStore>(services);
        ReplaceSingleton<ILocationStore, InMemoryLocationStore>(services);
        ReplaceSingleton<INotificationPreferencesStore,
            InMemoryNotificationPreferencesStore>(services);
        ReplaceSingleton<ISavedLocationStore, InMemorySavedLocationStore>(services);
        ReplaceSingleton<IRatingStore, InMemoryRatingStore>(services);
        services.RemoveAll<IRatingStoreExtended>();
        services.AddSingleton<IRatingStoreExtended>(sp =>
            (IRatingStoreExtended)sp.GetRequiredService<IRatingStore>());
        ReplaceSingleton<IJeeberRestrictionStore,
            InMemoryJeeberRestrictionStore>(services);
        ReplaceSingleton<IAdminEscalationStore,
            InMemoryAdminEscalationStore>(services);
        ReplaceSingleton<IAdminAuditLog, InMemoryAdminAuditLog>(services);
        ReplaceSingleton<IFlaggedRequestStore, InMemoryFlaggedRequestStore>(services);

        ReplaceSingleton<ISettlementStore, InMemorySettlementStore>(services);
        ReplaceSingleton<ISettlementLedgerClient,
            InMemorySettlementLedgerClient>(services);
        ReplaceSingleton<ISettlementEnqueueStore,
            TestSettlementEnqueueStore>(services);
        ReplaceSingleton<ISettlementBatchStore,
            TestSettlementBatchStore>(services);
        ReplaceSingleton<JeebGateway.Financials.Cod.ICodSettlementLedger,
            TestCodSettlementLedger>(services);
        ReplaceSingleton<IRefreshTokenStore, InMemoryRefreshTokenStore>(services);
        ReplaceSingleton<IFinancialLedgerAnonymizer,
            InMemoryFinancialLedger>(services);
        ReplaceScoped<IAccountDeletionStore,
            InMemoryAccountDeletionStore>(services);

        ReplaceSingleton<IDisputeStore, InMemoryDisputeStore>(services);
        ReplaceSingleton<IDisputeService, DisputeService>(services);
        ReplaceSingleton<IDisputeCaseStore, InMemoryDisputeCaseStore>(services);
        services.RemoveAll<IDisputeEvidenceOrchestrator>();
        services.AddScoped<IDisputeEvidenceOrchestrator, DisputeEvidenceOrchestrator>();
        ReplaceSingleton<IPaymentRefundClient,
            CashOnDeliveryNoRefundClient>(services);
        services.RemoveAll<IDisputeCaseService>();
        services.AddScoped<IDisputeCaseService, DisputeCaseService>();

        services.RemoveAll<TestExternalIdempotencyStore>();
        services.AddSingleton<TestExternalIdempotencyStore>();
        services.RemoveAll<IExternalIdempotencyStore>();
        services.AddSingleton<IExternalIdempotencyStore>(sp =>
            sp.GetRequiredService<TestExternalIdempotencyStore>());
        services.RemoveAll<IIdempotencyStore>();
        services.AddSingleton<IIdempotencyStore>(sp =>
            sp.GetRequiredService<TestExternalIdempotencyStore>());

        services.RemoveAll<INotificationOwnerClient>();
        services.AddSingleton<INotificationOwnerClient, TestNotificationOwnerClient>();
    }

    private static void ReplaceSingleton<TContract, TImplementation>(IServiceCollection services)
        where TContract : class
        where TImplementation : class, TContract
    {
        services.RemoveAll<TContract>();
        services.AddSingleton<TContract, TImplementation>();
    }

    private static void ReplaceScoped<TContract, TImplementation>(IServiceCollection services)
        where TContract : class
        where TImplementation : class, TContract
    {
        services.RemoveAll<TContract>();
        services.AddScoped<TContract, TImplementation>();
    }
}

internal sealed class TestExternalIdempotencyStore : IExternalIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyOutcome> _rows =
        new(StringComparer.Ordinal);

    public Task<IdempotencyOutcome> PutOrGetAsync(
        string key,
        int statusCode,
        string responseBodyJson,
        int ttlSeconds,
        CancellationToken ct)
    {
        var candidate = new IdempotencyOutcome
        {
            Inserted = true,
            StatusCode = statusCode,
            ResponseBodyJson = responseBodyJson,
        };
        var row = _rows.GetOrAdd(key, candidate);
        if (!ReferenceEquals(row, candidate))
        {
            row = new IdempotencyOutcome
            {
                Inserted = false,
                StatusCode = row.StatusCode,
                ResponseBodyJson = row.ResponseBodyJson,
            };
        }
        return Task.FromResult(row);
    }

    public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct) =>
        Task.FromResult(_rows.TryGetValue(key, out var row) ? row : null);

    public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
        string prefix,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IdempotencyOutcome>>(_rows
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .ToArray());
}

internal sealed class TestSettlementEnqueueStore : ISettlementEnqueueStore
{
    private readonly ConcurrentDictionary<string, byte> _deliveryIds =
        new(StringComparer.Ordinal);

    public Task<bool> TryEnqueueAsync(
        string deliveryId, DateTimeOffset at, CancellationToken ct) =>
        Task.FromResult(_deliveryIds.TryAdd(deliveryId, 0));

    public Task<bool> IsEnqueuedAsync(string deliveryId, CancellationToken ct) =>
        Task.FromResult(_deliveryIds.ContainsKey(deliveryId));
}

internal sealed class TestSettlementBatchStore : ISettlementBatchStore
{
    public Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Settlement>>(Array.Empty<Settlement>());

    public Task MarkBatchProcessedAsync(
        IReadOnlyList<string> settlementIds, DateTimeOffset at, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<SettlementBatch> CreateOrGetBatchAsync(
        string jeeberId, DateOnly periodStart, DateOnly periodEnd,
        IReadOnlyList<Settlement> settlements, CancellationToken ct) =>
        Task.FromResult(new SettlementBatch
        {
            Id = Guid.NewGuid(),
            JeeberId = jeeberId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    public Task<SettlementBatch?> GetByIdAsync(Guid batchId, CancellationToken ct) =>
        Task.FromResult<SettlementBatch?>(null);

    public Task<IReadOnlyList<SettlementBatch>> ListByStatusAsync(
        string status, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SettlementBatch>>(Array.Empty<SettlementBatch>());

    public Task<SettlementBatch> MarkPaidAsync(
        Guid batchId, string adminUserId, DateTimeOffset paidAt, CancellationToken ct) =>
        Task.FromException<SettlementBatch>(new InvalidOperationException("batch not found"));
}

internal sealed class TestNotificationOwnerClient : INotificationOwnerClient
{
    public ConcurrentQueue<NotificationOwnerEvent> Events { get; } = new();

    public Task<NotificationOwnerAcceptance> PublishAsync(
        NotificationOwnerEvent notification,
        CancellationToken cancellationToken)
    {
        Events.Enqueue(notification);
        return Task.FromResult(new NotificationOwnerAcceptance(notification.NotificationId));
    }

    public Task<System.Text.Json.JsonElement> GetDeadLettersAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(System.Text.Json.JsonDocument.Parse("[]").RootElement.Clone());
}
