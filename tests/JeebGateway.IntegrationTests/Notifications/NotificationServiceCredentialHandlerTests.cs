using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests.Notifications;

public sealed class NotificationServiceCredentialHandlerTests
{
    [Fact]
    public async Task Mounted_secret_sets_exact_owner_header()
    {
        var path = Path.GetTempFileName();
        try
        {
            const string token = "notification-owner-secret";
            await File.WriteAllTextAsync(path, token + "\n");
            var terminal = new CaptureHandler();
            using var client = new HttpClient(new NotificationServiceCredentialHandler(
                Configuration(("ServiceNotificationClient:ServiceTokenFile", path)))
            {
                InnerHandler = terminal,
            });

            await client.PostAsync(
                "http://notification.test/notifications/events",
                new StringContent("{}"));

            terminal.HeaderName.Should().Be("X-Notification-Service-Token");
            terminal.HeaderValue.Should().Be(token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Environment_backed_configuration_is_supported_without_logging_token()
    {
        const string token = "notification-environment-secret";
        var terminal = new CaptureHandler();
        using var client = new HttpClient(new NotificationServiceCredentialHandler(
            Configuration(("NOTIFICATION_SERVICE_TOKEN", token)))
        {
            InnerHandler = terminal,
        });

        await client.GetAsync("http://notification.test/dlq");

        terminal.HeaderValue.Should().Be(token);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)).Build();

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? HeaderName { get; private set; }
        public string? HeaderValue { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var header = request.Headers.Single(pair =>
                string.Equals(pair.Key, "X-Notification-Service-Token", StringComparison.Ordinal));
            HeaderName = header.Key;
            HeaderValue = header.Value.Single();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
