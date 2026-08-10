using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Infrastructure;

/// <summary>
/// Production composition guard for the stateless gateway boundary. Owner
/// adapters and explicit capability-unavailable adapters are allowed; local
/// databases, volatile owner stores, and gateway-owned background delivery
/// rails are not.
/// </summary>
internal static class StoreDurabilityGuard
{
    internal static readonly (Type Iface, Type[] DurableImpls)[] Critical =
    {
        (typeof(JeebGateway.Financials.ISettlementStore), new[] { typeof(JeebGateway.Financials.UnavailableSettlementStore) }),
        (typeof(JeebGateway.Financials.ISettlementBatchStore), new[] { typeof(JeebGateway.Financials.UnavailableSettlementBatchStore) }),
        (typeof(JeebGateway.Financials.ISettlementEnqueueStore), new[] { typeof(JeebGateway.Financials.StateServiceSettlementEnqueueStore) }),
        (typeof(JeebGateway.Financials.ISettlementLedgerClient), new[] { typeof(JeebGateway.Financials.WalletSettlementLedgerClient) }),
        (typeof(JeebGateway.Financials.Cod.ICodSettlementLedger), new[] { typeof(JeebGateway.Financials.Cod.WalletCodSettlementLedger) }),
        (typeof(JeebGateway.Users.IUsersStore), new[] { typeof(JeebGateway.Users.OwnerBackedUsersStore) }),
        (typeof(JeebGateway.Tokens.IRefreshTokenStore), new[] { typeof(JeebGateway.Tokens.StateServiceRefreshTokenStore) }),
        (typeof(JeebGateway.StateService.Idempotency.IIdempotencyStore), new[] { typeof(JeebGateway.StateService.Idempotency.StateServiceIdempotencyStore) }),
        (typeof(JeebGateway.StateService.Work.IStateWorkItemClient), new[] { typeof(JeebGateway.StateService.Work.StateWorkItemClient) }),
        (typeof(JeebGateway.StateService.Audit.IStateAuditClient), new[] { typeof(JeebGateway.StateService.Audit.StateAuditClient) }),
        (typeof(JeebGateway.Services.Clients.IGenericCaseStateClient), new[] { typeof(JeebGateway.Services.Clients.JeebStateServiceClient) }),
        (typeof(JeebGateway.Availability.IOfferRequestIndex), new[] { typeof(JeebGateway.StateService.Durable.StateServiceOfferRequestIndex) }),
        (typeof(JeebGateway.Requests.IRequestsStore), new[] { typeof(JeebGateway.Requests.DeliveryOwnerRequestsStore) }),
        (typeof(JeebGateway.Admin.IAdminAuditLog), new[] { typeof(JeebGateway.Admin.StateServiceAdminAuditLog) }),
        (typeof(JeebGateway.Users.IAccountDeletionStore), new[] { typeof(JeebGateway.Users.StateServiceAccountDeletionStore) }),
        (typeof(JeebGateway.Requests.OtpHandover.IAdminEscalationStore), new[] { typeof(JeebGateway.Requests.OtpHandover.StateServiceAdminEscalationStore) }),
        (typeof(JeebGateway.ProhibitedItems.FlaggedRequests.IFlaggedRequestStore), new[] { typeof(JeebGateway.ProhibitedItems.FlaggedRequests.StateServiceFlaggedRequestStore) }),
        (typeof(JeebGateway.ProhibitedItems.IProhibitedItemsStore), new[] { typeof(JeebGateway.ProhibitedItems.BanServiceProhibitedItemsStore) }),
        (typeof(JeebGateway.Availability.IAvailabilityStore), new[] { typeof(JeebGateway.Availability.DeliveryServiceAvailabilityStore) }),
        (typeof(JeebGateway.Users.SavedLocations.ISavedLocationStore), new[] { typeof(JeebGateway.Users.SavedLocations.RemoteUserPreferencesSavedLocationStore) }),
        (typeof(JeebGateway.Ratings.IRatingStore), new[] { typeof(JeebGateway.Ratings.FeedbackServiceRatingStore) }),
        (typeof(JeebGateway.NotificationPreferences.INotificationPreferencesStore), new[] { typeof(JeebGateway.NotificationPreferences.RemoteUserPreferencesNotificationPreferencesStore) }),
        (typeof(JeebGateway.Requests.Cancellation.IJeeberRestrictionStore), new[] { typeof(JeebGateway.Requests.Cancellation.BanServiceJeeberRestrictionStore) }),
        (typeof(JeebGateway.Tiers.ITiersStore), new[] { typeof(JeebGateway.Tiers.DeliveryServiceTiersStore) }),
        (typeof(JeebGateway.Users.IFinancialLedgerAnonymizer), new[] { typeof(JeebGateway.Users.WalletServiceFinancialLedgerAnonymizer) }),
        (typeof(JeebGateway.Partner.IPartnerWalletOperationStore), new[] { typeof(JeebGateway.Partner.StateServicePartnerWalletOperationStore) }),
        (typeof(JeebGateway.Partner.IPartnerOtpChallengeStore), new[] { typeof(JeebGateway.Partner.StateServicePartnerOtpChallengeStore) }),
        (typeof(JeebGateway.Tracking.ILocationStore), new[] { typeof(JeebGateway.Tracking.GeoServiceLocationStore) }),
        (typeof(JeebGateway.Availability.IPendingOffersStore), new[] { typeof(JeebGateway.Availability.UpstreamPendingOffersStore) }),
        (typeof(JeebGateway.Notifications.INotificationOwnerClient), new[] { typeof(JeebGateway.Notifications.NotificationOwnerClient) }),
        (typeof(JeebGateway.Push.IPushNotificationService), new[] { typeof(JeebGateway.Push.NotificationOwnerPushService) }),
    };

    // Kept as empty compatibility surfaces for older test code. A production
    // gateway has no accepted in-process owner backlog.
    internal static readonly Type[] KnownInMemoryBacklog = Array.Empty<Type>();
    internal static readonly Type[] UpstreamContractIncomplete = Array.Empty<Type>();
    internal static readonly Type[] IntentionalInMemory =
    {
        typeof(JeebGateway.Availability.IGeoIndex),
    };

    private static readonly string[] ForbiddenConfigurationKeys =
    {
        "GatewayPostgres:ConnectionString",
        "WalletPostgres:ConnectionString",
        "ConnectionStrings:GatewayPostgres",
        "ConnectionStrings:WalletPostgres",
        "ConnectionStrings:Default",
        "DATABASE_URL",
        "JEEB_DATABASE_URL",
        "UnifiedPaymentGateway:BaseUrl",
        "UPG:BaseUrl",
        "UPG_BASE_URL",
    };

    internal static readonly Type[] AllowedHostedServices =
    {
        typeof(JeebGateway.Auth.Capabilities.CapabilityCoverageGuard),
        typeof(JeebGateway.Services.Bff.BffStartupValidator),
    };

    internal static bool IsExempt(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    internal static IReadOnlyList<string> Evaluate(Func<Type, Type?> resolve)
    {
        var violations = new List<string>();
        foreach (var (contract, allowed) in Critical)
        {
            Type? implementation;
            try
            {
                implementation = resolve(contract);
            }
            catch (Exception ex)
            {
                violations.Add($"{contract.Name} could not be resolved ({ex.GetType().Name})");
                continue;
            }

            if (implementation is null || !allowed.Contains(implementation))
            {
                violations.Add(
                    $"{contract.Name} resolved to '{implementation?.Name ?? "<unregistered>"}' " +
                    $"(allowed: {string.Join(" or ", allowed.Select(type => type.Name))})");
            }
        }
        return violations;
    }

    internal static IReadOnlyList<string> EvaluateConfiguration(IConfiguration configuration)
    {
        var violations = ForbiddenConfigurationKeys
            .Where(key => !string.IsNullOrWhiteSpace(configuration[key]))
            .Select(key => $"forbidden gateway-owned database/UPG setting '{key}' is configured")
            .ToList();

        ValidateMountedSecret(
            configuration["JeebStateService:ServiceTokenFile"],
            "JeebStateService:ServiceTokenFile",
            violations);
        var deliveryTokenFile = configuration["DELIVERY_SERVICE_TOKEN_FILE"]
                                ?? configuration["Services:Delivery:ServiceTokenFile"];
        ValidateMountedSecret(deliveryTokenFile, "DELIVERY_SERVICE_TOKEN_FILE", violations);
        if (!string.IsNullOrWhiteSpace(deliveryTokenFile)
            && Path.IsPathFullyQualified(deliveryTokenFile)
            && File.Exists(deliveryTokenFile)
            && !JeebGateway.Services.Clients.DeliveryServiceCredentialHandler
                .TryValidateMountedTokenFile(deliveryTokenFile, out var deliveryTokenError))
        {
            violations.Add($"DELIVERY_SERVICE_TOKEN_FILE {deliveryTokenError}");
        }

        var notificationFile = configuration["ServiceNotificationClient:ServiceTokenFile"];
        var notificationToken = configuration["ServiceNotificationClient:ServiceToken"]
                                ?? configuration["NOTIFICATION_SERVICE_TOKEN"];
        if (!string.IsNullOrWhiteSpace(notificationFile))
        {
            ValidateMountedSecret(notificationFile,
                "ServiceNotificationClient:ServiceTokenFile", violations);
        }
        else if (string.IsNullOrWhiteSpace(notificationToken))
        {
            violations.Add("notification owner credential is not configured");
        }

        if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                string.Equals(assembly.GetName().Name, "Npgsql", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add("Npgsql is loaded in the gateway process");
        }

        return violations;
    }

    internal static IReadOnlyList<string> EvaluateHostedServices(
        IEnumerable<IHostedService> hostedServices)
    {
        var allowed = AllowedHostedServices.ToHashSet();
        return hostedServices
            .Select(service => service.GetType())
            .Where(type => !allowed.Contains(type))
            .Select(type => $"forbidden hosted service '{type.Name}' is registered")
            .ToArray();
    }

    private static void ValidateMountedSecret(
        string? path,
        string setting,
        ICollection<string> violations)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            violations.Add($"{setting} must name an absolute mounted-secret file");
            return;
        }
        if (!File.Exists(path))
            violations.Add($"{setting} mounted-secret file does not exist");
    }

    public static void EnsureDurable(
        IServiceProvider services,
        IHostEnvironment environment,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        if (IsExempt(environment)) return;

        using var scope = services.CreateScope();
        var violations = Evaluate(type => scope.ServiceProvider.GetService(type)?.GetType()).ToList();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        violations.AddRange(EvaluateConfiguration(configuration));
        violations.AddRange(EvaluateHostedServices(
            scope.ServiceProvider.GetServices<IHostedService>()));

        if (violations.Count != 0)
        {
            throw new InvalidOperationException(
                "FAIL-CLOSED: gateway composition is not stateless:\n  - " +
                string.Join("\n  - ", violations));
        }

        logger.LogInformation(
            "Stateless gateway guard passed with {Count} owner boundaries.", Critical.Length);
    }
}

internal sealed class StoreDurabilityHealthCheck(
    IServiceProvider services,
    IHostEnvironment environment) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (StoreDurabilityGuard.IsExempt(environment))
            return Task.FromResult(HealthCheckResult.Healthy("stateless-gateway: test/development host"));

        using var scope = services.CreateScope();
        var violations = StoreDurabilityGuard
            .Evaluate(type => scope.ServiceProvider.GetService(type)?.GetType())
            .Concat(StoreDurabilityGuard.EvaluateConfiguration(
                scope.ServiceProvider.GetRequiredService<IConfiguration>()))
            .Concat(StoreDurabilityGuard.EvaluateHostedServices(
                scope.ServiceProvider.GetServices<IHostedService>()))
            .ToArray();

        return Task.FromResult(violations.Length == 0
            ? HealthCheckResult.Healthy("stateless-gateway: owner-only composition")
            : HealthCheckResult.Unhealthy("stateless-gateway: " + string.Join("; ", violations)));
    }
}
