using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

/// <summary>
/// Emits the operator-visible rollback alarm when durable notification writes
/// remain disabled in a prod-like environment.
/// </summary>
internal sealed class NotificationDurableWriteStartupAlarm : IHostedService
{
    internal const string AlarmEvent = "notif.durable_write.disabled_prod_like";

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<NotificationDurableWriteStartupAlarm> _logger;

    public NotificationDurableWriteStartupAlarm(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<NotificationDurableWriteStartupAlarm> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var enabled = _configuration.GetValue<bool>(
            NotificationRecordWriter.EnabledConfigurationKey);
        var isExemptEnvironment = _environment.IsDevelopment()
            || _environment.IsEnvironment("Testing");
        if (enabled || isExemptEnvironment)
        {
            return Task.CompletedTask;
        }

        _logger.LogCritical(
            "event={event} enabled={enabled} environment={environment} " +
            "DURABLE NOTIFICATION WRITES ARE DISABLED IN A PROD-LIKE ENVIRONMENT; " +
            "JEBV4-333 missed-push durability is rolled back.",
            AlarmEvent,
            false,
            _environment.EnvironmentName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
