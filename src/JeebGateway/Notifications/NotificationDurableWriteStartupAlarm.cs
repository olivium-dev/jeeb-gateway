using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

/// <summary>
/// Emits the operator-visible authority alarm when durable notification writes
/// remain disabled in a prod-like environment, and proves at boot that the
/// notification owner credential actually resolves when they are enabled.
/// </summary>
internal sealed class NotificationDurableWriteStartupAlarm : IHostedService
{
    internal const string AlarmEvent = "notif.durable_write.disabled_prod_like";
    internal const string CredentialAlarmEvent = "notif.durable_write.credential_unresolvable";
    internal const string PushCredentialAlarmEvent = "push.relay.credential_unresolvable";

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
        if (isExemptEnvironment)
        {
            return Task.CompletedTask;
        }

        if (!enabled)
        {
            _logger.LogCritical(
                "event={event} enabled={enabled} environment={environment} " +
                "DURABLE NOTIFICATION WRITES ARE DISABLED IN A PROD-LIKE ENVIRONMENT; " +
                "JEBV4-333 missed-push durability is unavailable; fix the owner path forward.",
                AlarmEvent,
                false,
                _environment.EnvironmentName);
            throw new InvalidOperationException(
                "Durable notification writes are required in production-like environments.");
        }

        return VerifyCredentialsResolveAsync(cancellationToken);
    }

    // 608debf outage: the flag said "enabled" while the credential chain failed closed
    // on every send. Resolving it here makes that state loud at the moment of boot.
    private async Task VerifyCredentialsResolveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await NotificationServiceCredentialHandler.ReadTokenAsync(
                _configuration, _logger, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "event={event} environment={environment} " +
                "DURABLE NOTIFICATION WRITES ARE ENABLED BUT THE NOTIFICATION SERVICE " +
                "CREDENTIAL DOES NOT RESOLVE; EVERY PUSH HANDOVER WILL FAIL CLOSED.",
                CredentialAlarmEvent,
                _environment.EnvironmentName);
            throw new InvalidOperationException(
                "The notification owner credential does not resolve.", ex);
        }

        try
        {
            await PushRelayCredentialHandler.ReadTokenAsync(
                _configuration, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "event={event} environment={environment} " +
                "THE PUSH RELAY CREDENTIAL DOES NOT RESOLVE; REGISTRATION AND RECOVERY " +
                "CALLS WILL FAIL CLOSED.",
                PushCredentialAlarmEvent,
                _environment.EnvironmentName);
            throw new InvalidOperationException(
                "The push relay credential does not resolve.", ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
