using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://push.invalid/api/v1/register");
            request.Headers.TryAddWithoutValidation(PushRelayCredentialHandler.HeaderName, "forged-key");
            request.Headers.TryAddWithoutValidation(PushRelayCredentialHandler.CallerHeaderName, "forged-caller");
            await client.SendAsync(request);

            terminal.ApiKey.Should().Be("relay-key-with-newline");
            terminal.CallerId.Should().Be("jeeb-gateway");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SharedContract_GrantsGatewayOnlyRegistrationAndRecovery()
    {
        var contractPath = FindContract();
        var bytes = File.ReadAllBytes(contractPath);
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            .Should().Be("4d023153823a3007928f9798095d62d57694a6d70c7041b2fe4fae5d694a4ce2");
        using var document = JsonDocument.Parse(bytes);
        var gateway = document.RootElement
            .GetProperty("consumers")
            .GetProperty("jeeb-gateway");

        gateway.GetProperty("caller_id").GetString().Should().Be(
            PushRelayCredentialHandler.CallerId);
        gateway.GetProperty("scopes").EnumerateArray()
            .Select(value => value.GetString())
            .Should().BeEquivalentTo("gateway.registration", "gateway.recovery");
        gateway.GetProperty("scopes").EnumerateArray()
            .Select(value => value.GetString())
            .Should().NotContain("notification.user-delivery");
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
    public async Task ScopedReadiness_UsesTheMountedGatewayKeyAndExpectedProviderResponse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jeeb-ready-key-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "gateway-readiness-key\n");
        try
        {
            var terminal = new ReadinessHandler(HttpStatusCode.OK,
                """{"status":"ready","scope":"gateway.registration"}""");
            var check = Check(Configuration(
                (PushRelayCredentialHandler.TokenFileKey, path)), terminal);

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            result.Status.Should().Be(HealthStatus.Healthy);
            terminal.Path.Should().Be(PushRelayCredentialHealthCheck.ReadinessPath);
            terminal.ApiKey.Should().Be("gateway-readiness-key");
            terminal.CallerId.Should().Be(PushRelayCredentialHandler.CallerId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ScopedReadiness_RejectsBadOrCrossScopeCredentials(HttpStatusCode status)
    {
        var check = Check(Configuration(
            (PushRelayCredentialHandler.TokenKey, "gateway-readiness-key")),
            new ReadinessHandler(status, "{}"));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"status\":\"ready\",\"scope\":\"notification.user-delivery\"}")]
    public async Task ScopedReadiness_RejectsMalformedOrWrongScopeBodies(string body)
    {
        var check = Check(Configuration(
            (PushRelayCredentialHandler.TokenKey, "gateway-readiness-key")),
            new ReadinessHandler(HttpStatusCode.OK, body));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task ScopedReadiness_RejectsUnreachableProvider()
    {
        var check = Check(Configuration(
            (PushRelayCredentialHandler.TokenKey, "gateway-readiness-key")),
            new ThrowingHandler());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task ScopedReadiness_RejectsMissingCredential()
    {
        var check = Check(Configuration(), new ReadinessHandler(
            HttpStatusCode.OK, """{"status":"ready","scope":"gateway.registration"}"""));

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

    private static PushRelayCredentialHealthCheck Check(
        IConfiguration configuration,
        HttpMessageHandler terminal)
    {
        var credentialHandler = new PushRelayCredentialHandler(configuration)
        {
            InnerHandler = terminal,
        };
        var client = new HttpClient(credentialHandler)
        {
            BaseAddress = new Uri("https://push.invalid"),
        };
        return new PushRelayCredentialHealthCheck(new SingleClientFactory(client));
    }

    private static string FindContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "contracts",
                "notification-push-relay-v1.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Scoped push relay contract was not found.");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? ApiKey { get; private set; }
        public string? CallerId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ApiKey = request.Headers.GetValues(
                PushRelayCredentialHandler.HeaderName).Single();
            CallerId = request.Headers.GetValues(
                PushRelayCredentialHandler.CallerHeaderName).Single();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class ReadinessHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? ApiKey { get; private set; }
        public string? CallerId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            ApiKey = request.Headers.GetValues(PushRelayCredentialHandler.HeaderName).Single();
            CallerId = request.Headers.GetValues(PushRelayCredentialHandler.CallerHeaderName).Single();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("provider unavailable");
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
