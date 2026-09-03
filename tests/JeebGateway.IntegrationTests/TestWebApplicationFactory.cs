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
using Microsoft.Extensions.Logging;

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
        builder.UseSetting(
            "ServiceNotificationClient:ServiceToken",
            "integration-test-notification-owner-token");
        builder.UseSetting(
            "PushNotificationServiceApi:GatewayApiKey",
            "integration-test-push-relay-key");
        // Some security tests deliberately boot the complete gateway under the
        // Production environment. Production now correctly requires a usable
        // Firebase signer at startup, so give every test host an ephemeral key
        // generated outside the repository. Individual Firebase negative tests
        // explicitly override this path with their invalid case.
        builder.UseSetting(
            "Firebase:Chat:ServiceAccountKeyPath",
            TestFirebaseCredential.CredentialPath);
        // A full gateway host is created by thousands of integration cases. Its
        // production console providers otherwise stream every background-worker
        // retry into VSTest, overwhelming the CI runner before the suite finishes.
        // Tests that assert logs add their own capturing provider afterward.
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices((context, services) =>
        {
            // The generated test signer is harness plumbing, not application
            // behavior. Keep its startup status out of tests that intentionally
            // assert no token-related security event was logged.
            services.AddSingleton<ILogger<JeebGateway.Chat.Firebase.FirebaseCustomTokenMinter>>(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    JeebGateway.Chat.Firebase.FirebaseCustomTokenMinter>.Instance);

            if (context.HostingEnvironment.IsEnvironment("Testing")
                || context.HostingEnvironment.IsDevelopment())
            {
                UseExplicitTestOwners(services);
            }

            // The real readiness check performs an authenticated provider call.
            // Keep unrelated gateway integration tests local while exercising the
            // production handler and fixed ready/scope response shape.
            services.AddHttpClient("ServicePushNotificationClient")
                .ConfigurePrimaryHttpMessageHandler(
                    static () => new TestPushRelayReadinessHandler());
        });
    }

    private static class TestFirebaseCredential
    {
        private static readonly Lazy<string> Credential = new(CreateCredential);

        internal static string CredentialPath => Credential.Value;

        private static string CreateCredential()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), $"jeeb-gateway-firebase-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "service-account.json");
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var document = new
            {
                type = "service_account",
                project_id = "jeeb-5a293",
                private_key_id = "integration-test-key-id",
                private_key = rsa.ExportPkcs8PrivateKeyPem(),
                client_email = "firebase-test@jeeb-5a293.iam.gserviceaccount.com",
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(document));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    File.Delete(path);
                    Directory.Delete(directory);
                }
                catch (IOException)
                {
                    // Best effort only; the OS temp directory remains the security boundary.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best effort only; never weaken the runtime validator for cleanup.
                }
            };
            return path;
        }
    }

    private sealed class TestPushRelayReadinessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"ready","scope":"gateway.registration"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
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

        // gwdbx W2-R11: the local settlement store, ledger client, enqueue store, batch store
        // and COD ledger are all deleted; settlement-service owns them behind ISettlementServiceClient.
        ReplaceSingleton<IRefreshTokenStore, InMemoryRefreshTokenStore>(services);
        ReplaceSingleton<IFinancialLedgerAnonymizer,
            InMemoryFinancialLedger>(services);
        ReplaceScoped<IAccountDeletionStore,
            InMemoryAccountDeletionStore>(services);

        ReplaceSingleton<IDisputeStore, InMemoryDisputeStore>(services);
        // SCOPED, matching Program.cs: DisputeService consumes the scoped
        // IGenericEventDispatcher, so a singleton here is a captive dependency.
        ReplaceScoped<IDisputeService, DisputeService>(services);
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
