using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class PushRelayCredentialHandlerTests
{
    [Fact]
    public async Task SendAsync_AddsFileBackedApiKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jeeb-push-key-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "relay-key-with-newline\n");
        try
        {
            var terminal = new RecordingHandler();
            var handler = new PushRelayCredentialHandler(Configuration(
                (PushRelayCredentialHandler.TokenFileKey, path)))
            {
                InnerHandler = terminal,
            };
            using var client = new HttpClient(handler);

            await client.GetAsync("https://push.invalid/api/v1/register");

            terminal.ApiKey.Should().Be("relay-key-with-newline");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadTokenAsync_RejectsAmbiguousSources()
    {
        var action = () => PushRelayCredentialHandler.ReadTokenAsync(
            Configuration(
                (PushRelayCredentialHandler.TokenFileKey, "/run/secrets/push-key"),
                (PushRelayCredentialHandler.TokenKey, "direct-key")),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*only one*");
    }

    [Fact]
    public async Task HealthCheck_IsUnhealthyWhenCredentialIsMissing()
    {
        var check = new PushRelayCredentialHealthCheck(Configuration());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    private static IConfiguration Configuration(
        params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                pair => pair.Key,
                pair => (string?)pair.Value))
            .Build();

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ApiKey = request.Headers.GetValues(
                PushRelayCredentialHandler.HeaderName).Single();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
