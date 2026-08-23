using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
            using var client = Client(terminal,
                ("ServiceNotificationClient:ServiceTokenFile", path));

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
    public async Task Mounted_secret_wins_over_every_value_backed_key()
    {
        var path = Path.GetTempFileName();
        try
        {
            const string token = "notification-file-token";
            await File.WriteAllTextAsync(path, token);
            var terminal = new CaptureHandler();
            using var client = Client(terminal,
                ("ServiceNotificationClient:ServiceTokenFile", path),
                ("ServiceNotificationClient:ServiceToken", "value-token"),
                ("ServiceNotificationClient:ApiToken", "api-token"));

            await client.GetAsync("http://notification.test/dlq");

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
        using var client = Client(terminal, ("NOTIFICATION_SERVICE_TOKEN", token));

        await client.GetAsync("http://notification.test/dlq");

        terminal.HeaderValue.Should().Be(token);
    }

    [Fact]
    public async Task Service_token_value_is_used_when_no_file_is_configured()
    {
        const string token = "notification-service-token-value";
        var terminal = new CaptureHandler();
        using var client = Client(terminal,
            ("ServiceNotificationClient:ServiceToken", token));

        await client.GetAsync("http://notification.test/dlq");

        terminal.HeaderValue.Should().Be(token);
    }

    [Fact]
    public async Task Api_token_fallback_supports_native_deployments()
    {
        const string token = "notification-api-token-native";
        var terminal = new CaptureHandler();
        using var client = Client(terminal,
            ("ServiceNotificationClient:ApiToken", token));

        await client.GetAsync("http://notification.test/dlq");

        terminal.HeaderValue.Should().Be(token);
    }

    [Fact]
    public async Task Configured_but_missing_file_falls_through_to_api_token()
    {
        const string token = "notification-api-token-after-missing-file";
        var terminal = new CaptureHandler();
        using var client = Client(terminal,
            ("ServiceNotificationClient:ServiceTokenFile", "/run/secrets/notification_service_token"),
            ("ServiceNotificationClient:ApiToken", token));

        await client.GetAsync("http://notification.test/dlq");

        terminal.HeaderValue.Should().Be(token);
    }

    [Fact]
    public async Task Nothing_configured_throws_naming_every_key()
    {
        var terminal = new CaptureHandler();
        using var client = Client(terminal);

        var act = () => client.GetAsync("http://notification.test/dlq");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("ServiceNotificationClient:ServiceTokenFile")
            .And.Contain("ServiceNotificationClient:ServiceToken")
            .And.Contain("NOTIFICATION_SERVICE_TOKEN")
            .And.Contain("ServiceNotificationClient:ApiToken");
    }

    [Fact]
    public async Task Missing_file_with_no_value_fallback_names_the_missing_path()
    {
        var terminal = new CaptureHandler();
        using var client = Client(terminal,
            ("ServiceNotificationClient:ServiceTokenFile", "/run/secrets/notification_service_token"));

        var act = () => client.GetAsync("http://notification.test/dlq");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("/run/secrets/notification_service_token");
    }

    private static HttpClient Client(
        CaptureHandler terminal,
        params (string Key, string Value)[] values) =>
        new(new NotificationServiceCredentialHandler(
            Configuration(values),
            NullLogger<NotificationServiceCredentialHandler>.Instance)
        {
            InnerHandler = terminal,
        });

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
