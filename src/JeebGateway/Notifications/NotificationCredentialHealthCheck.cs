using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Notifications;

/// <summary>
/// In-process readiness gate proving the notification owner credential resolves. The
/// 608debf outage stayed green because no health surface exercised the fail-closed
/// credential chain; this check turns that state visibly unhealthy on /health/ready.
/// </summary>
public sealed class NotificationCredentialHealthCheck(
    IConfiguration configuration,
    ILogger<NotificationCredentialHealthCheck> logger) : IHealthCheck
{
    internal const string Name = "notification-credential";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Same gate as the writer: with durable writes off, nothing dials the centre.
        if (!configuration.GetValue<bool>(NotificationRecordWriter.EnabledConfigurationKey))
        {
            return HealthCheckResult.Healthy("durable notification writes are not armed");
        }

        try
        {
            await NotificationServiceCredentialHandler.ReadTokenAsync(
                configuration, logger, cancellationToken);
            return HealthCheckResult.Healthy("notification service credential resolves");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // ex.Message names config keys and paths only, never token material.
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
