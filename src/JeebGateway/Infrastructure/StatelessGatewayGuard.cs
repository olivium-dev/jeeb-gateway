using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Infrastructure;

/// <summary>
/// Production invariant for the gateway process. The gateway may orchestrate
/// authoritative owners, but it may not receive a database credential or run a
/// local authoritative store/worker. Development and Testing retain their
/// isolated fixture stores.
/// </summary>
internal static class StatelessGatewayGuard
{
    internal const string CodOwnerVerifiedKey = "Settlements:CodOwnerVerified";

    private static readonly string[] ForbiddenConfigurationKeys =
    {
        "GatewayPostgres:ConnectionString",
        "WalletPostgres:ConnectionString",
        "ConnectionStrings:Default",
        "DATABASE_URL",
        "JEEB_DATABASE_URL",
    };

    private static readonly HashSet<string> ForbiddenHostedServices = new(StringComparer.Ordinal)
    {
        "AcceptChatSettleReconciler",
        "CourierPositionPublisher",
        "NotificationDurableWriteStartupAlarm",
        "NewRequestFanoutProcessor",
        "PushRetryQueueProcessor",
        "OtpHandoverSweeper",
        "RequestExpirySweeper",
        "RequestNudgeSweeper",
        "RequestExpiryObserver",
        "ScheduledDeliveryActivator",
        "DefaultLexiconSeeder",
        "AccountDeletionPurgeWorker",
        "DataExportWorker",
        "DataExportProcessor",
        "AutoOfflineSweeper",
        "RatingRevealJob",
        "LowRatingAutoFlag",
    };

    private static readonly Type[] ProcessStateContracts =
    {
        typeof(JeebGateway.Users.IUsersStore),
        typeof(JeebGateway.Tokens.IRefreshTokenStore),
        typeof(JeebGateway.Requests.IRequestsStore),
        typeof(JeebGateway.Users.IAccountDeletionStore),
        typeof(JeebGateway.StateService.Idempotency.IIdempotencyStore),
        typeof(JeebGateway.Services.Clients.IGenericCaseStateClient),
        typeof(JeebGateway.Financials.ISettlementStore),
        typeof(JeebGateway.Financials.Cod.ICodSettlementLedger),
    };

    internal static bool IsLocalHarness(IHostEnvironment environment) =>
        environment.IsDevelopment()
        || string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> Evaluate(IServiceProvider services, IHostEnvironment environment)
    {
        if (IsLocalHarness(environment)) return Array.Empty<string>();

        var violations = new List<string>();
        var configuration = services.GetRequiredService<IConfiguration>();
        foreach (var key in ForbiddenConfigurationKeys)
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
                violations.Add($"forbidden database configuration '{key}' is present");
        }

        if (!configuration.GetValue<bool>(CodOwnerVerifiedKey))
        {
            violations.Add(
                $"'{CodOwnerVerifiedKey}=true' is required after the existing cash-on-delivery owner has been independently verified");
        }

        foreach (var hosted in services.GetServices<IHostedService>())
        {
            if (ForbiddenHostedServices.Contains(hosted.GetType().Name))
                violations.Add($"stateful hosted service '{hosted.GetType().FullName}' is registered");
        }

        foreach (var contract in ProcessStateContracts)
        {
            var implementation = services.GetService(contract)?.GetType();
            if (implementation is null) continue;
            var name = implementation.Name;
            if (name.StartsWith("InMemory", StringComparison.Ordinal)
                || name.StartsWith("Postgres", StringComparison.Ordinal)
                || name.StartsWith("InProcess", StringComparison.Ordinal)
                || name.Equals("DurableRequestsStore", StringComparison.Ordinal))
            {
                violations.Add($"{contract.Name} resolves to process state '{implementation.FullName}'");
            }
        }

        if (AppDomain.CurrentDomain.GetAssemblies().Any(static assembly =>
                string.Equals(assembly.GetName().Name, "Npgsql", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add("the Npgsql provider is loaded into the gateway process");
        }

        return violations;
    }

    public static void EnsureStateless(IServiceProvider services, IHostEnvironment environment)
    {
        var violations = Evaluate(services, environment);
        if (violations.Count == 0) return;

        throw new InvalidOperationException(
            "FAIL-CLOSED: the production gateway is not stateless:\n  - "
            + string.Join("\n  - ", violations));
    }
}

internal sealed class StatelessGatewayHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _services;
    private readonly IHostEnvironment _environment;

    public StatelessGatewayHealthCheck(IServiceProvider services, IHostEnvironment environment)
    {
        _services = services;
        _environment = environment;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var violations = StatelessGatewayGuard.Evaluate(_services, _environment);
        return Task.FromResult(violations.Count == 0
            ? HealthCheckResult.Healthy("gateway composition is stateless")
            : HealthCheckResult.Unhealthy(string.Join("; ", violations)));
    }
}
