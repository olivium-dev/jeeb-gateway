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

        await alarm.StartAsync(CancellationToken.None);

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
    public async Task Enabled_InProdLikeEnvironment_DoesNotLogAlarm()
    {
        var logger = new RecordingLogger<NotificationDurableWriteStartupAlarm>();
        var alarm = NewAlarm(enabled: true, Environments.Production, logger);

        await alarm.StartAsync(CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }

    private static NotificationDurableWriteStartupAlarm NewAlarm(
        bool enabled,
        string environmentName,
        RecordingLogger<NotificationDurableWriteStartupAlarm> logger)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NotificationRecordWriter.EnabledConfigurationKey] = enabled.ToString(),
            })
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
