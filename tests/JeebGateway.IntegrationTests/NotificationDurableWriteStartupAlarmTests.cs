using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class NotificationDurableWriteStartupAlarmTests
{
    [Fact]
    public async Task Disabled_InProdLikeEnvironment_LogsCriticalAlarm()
    {
        var logger = new RecordingLogger<NotificationDurableWriteStartupAlarm>();
        var alarm = NewAlarm(enabled: false, Environments.Production, logger);

        var action = () => alarm.StartAsync(CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Durable notification writes are required*");

        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Critical
            && Equals(
                entry.Properties["event"],
                NotificationDurableWriteStartupAlarm.AlarmEvent)
            && Equals(entry.Properties["enabled"], false)
            && Equals(entry.Properties["environment"], Environments.Production));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Disabled_InExemptEnvironment_DoesNotLogAlarm(
        string environmentName)
    {
        var logger = new RecordingLogger<NotificationDurableWriteStartupAlarm>();
        var alarm = NewAlarm(enabled: false, environmentName, logger);

        await alarm.StartAsync(CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Enabled_WithResolvableCredential_DoesNotLogAlarm()
    {
        var logger = new RecordingLogger<NotificationDurableWriteStartupAlarm>();
        var alarm = NewAlarm(enabled: true, Environments.Production, logger,
            ("ServiceNotificationClient:ServiceToken", "startup-alarm-resolvable-token"),
            ("PushNotificationServiceApi:GatewayApiKey", "push-relay-resolvable-token"));

        await alarm.StartAsync(CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Enabled_WithUnresolvableCredential_LogsCriticalCredentialAlarm()
    {
        var logger = new RecordingLogger<NotificationDurableWriteStartupAlarm>();
        var alarm = NewAlarm(enabled: true, Environments.Production, logger);

        var action = () => alarm.StartAsync(CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*notification owner credential*");

        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Critical
            && Equals(
                entry.Properties["event"],
                NotificationDurableWriteStartupAlarm.CredentialAlarmEvent)
            && Equals(entry.Properties["environment"], Environments.Production));
    }

    [Fact]
    public async Task Enabled_WithMissingTokenFileButEnvFallback_DoesNotLogCredentialAlarm()
    {
        var logger = new RecordingLogger<NotificationDurableWriteStartupAlarm>();
        var alarm = NewAlarm(enabled: true, Environments.Production, logger,
            ("ServiceNotificationClient:ServiceTokenFile", "/run/secrets/notification_service_token"),
            ("ServiceNotificationClient:ApiToken", "native-msi-token"),
            ("PushNotificationServiceApi:GatewayApiKey", "push-relay-resolvable-token"));

        await alarm.StartAsync(CancellationToken.None);

        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task Enabled_WithUnresolvablePushCredential_FailsStartup()
    {
        var logger = new RecordingLogger<NotificationDurableWriteStartupAlarm>();
        var alarm = NewAlarm(enabled: true, Environments.Production, logger,
            ("ServiceNotificationClient:ServiceToken", "startup-alarm-resolvable-token"));

        var action = () => alarm.StartAsync(CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*push relay credential*");
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Critical
            && Equals(
                entry.Properties["event"],
                NotificationDurableWriteStartupAlarm.PushCredentialAlarmEvent));
    }

    [Fact]
    public async Task Enabled_InExemptEnvironment_DoesNotResolveCredential()
    {
        var logger = new RecordingLogger<NotificationDurableWriteStartupAlarm>();
        var alarm = NewAlarm(enabled: true, "Testing", logger);

        await alarm.StartAsync(CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }

    private static NotificationDurableWriteStartupAlarm NewAlarm(
        bool enabled,
        string environmentName,
        RecordingLogger<NotificationDurableWriteStartupAlarm> logger,
        params (string Key, string Value)[] extraConfig)
    {
        var values = new Dictionary<string, string?>
        {
            [NotificationRecordWriter.EnabledConfigurationKey] = enabled.ToString(),
        };
        foreach (var (key, value) in extraConfig) values[key] = value;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new NotificationDurableWriteStartupAlarm(
            configuration,
            new TestHostEnvironment { EnvironmentName = environmentName },
            logger);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "JeebGateway.IntegrationTests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>>
                ?? [];
            Entries.Add(new LogEntry(
                logLevel,
                properties.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        IReadOnlyDictionary<string, object?> Properties);
}
