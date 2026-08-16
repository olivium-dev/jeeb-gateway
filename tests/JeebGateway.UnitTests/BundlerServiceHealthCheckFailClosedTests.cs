using System.Net;
using System.Net.Http;
using System.Text;
using JeebGateway.Cms;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace JeebGateway.UnitTests;

// R3-3: fail-closed cover for the bundler probe as PURE unit tests (fake handler,
// no WebApplicationFactory) so they run in the project that compiles.
public class BundlerServiceHealthCheckFailClosedTests
{
    // The live defect (MSI 2026-08-16): a Host-matching proxy answered 200 with
    // Content-Length: 0 for an unmatched host, so a dead bundler read Healthy.
    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public async Task A_200_with_an_empty_body_reports_the_registered_failure_status(
        HealthStatus failure)
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, string.Empty));

        var result = await CheckAsync(handler, failure);

        Assert.Equal(failure, result.Status);
        Assert.Contains("EMPTY body", result.Description ?? string.Empty);
        Assert.Equal(200, Convert.ToInt32(result.Data["statusCode"]));
        Assert.Equal(0, Convert.ToInt32(result.Data["bodyBytes"]));
    }

    // Body is deliberately NON-empty: the empty-body branch must not be what
    // makes this pass, or the status-code branch would be untested.
    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public async Task A_non_2xx_reports_the_registered_failure_status(HealthStatus failure)
    {
        var handler = new StubHandler(
            _ => Respond(HttpStatusCode.ServiceUnavailable, "not ready"));

        var result = await CheckAsync(handler, failure);

        Assert.Equal(failure, result.Status);
        Assert.Contains("503", result.Description ?? string.Empty);
        Assert.Equal(503, Convert.ToInt32(result.Data["statusCode"]));
    }

    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public async Task A_transport_failure_reports_the_registered_failure_status(
        HealthStatus failure)
    {
        var handler = new StubHandler(
            _ => throw new HttpRequestException("connection refused"));

        var result = await CheckAsync(handler, failure);

        Assert.Equal(failure, result.Status);
        Assert.Contains("unreachable", result.Description ?? string.Empty);
    }

    // The anti-construction control: proves the probe can still say Healthy, so the
    // three failure cases above are not passing because it always fails.
    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public async Task A_200_with_a_real_body_is_healthy_whatever_the_registration_says(
        HealthStatus failure)
    {
        var handler = new StubHandler(
            _ => Respond(HttpStatusCode.OK, "{\"status\":\"Healthy\"}"));

        var result = await CheckAsync(handler, failure);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True(Convert.ToInt32(result.Data["bodyBytes"]) > 0);
    }

    [Fact]
    public async Task The_probe_dials_the_stores_named_client_on_the_readiness_route()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, "ok"));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://bundler.test/"),
        };
        var factory = new RecordingHttpClientFactory(client);
        var check = new BundlerServiceHealthCheck(factory);

        await check.CheckHealthAsync(ContextFor(check, HealthStatus.Degraded), CancellationToken.None);

        // health/live stays 200 while bundler own dependencies are down.
        Assert.Equal("/health/ready", Assert.Single(handler.Paths));
        Assert.Equal(BundlerCmsSurfaceStore.HttpClientName, factory.RequestedName);
    }

    private static async Task<HealthCheckResult> CheckAsync(
        StubHandler handler, HealthStatus failure)
    {
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://bundler.test/"),
        };
        var check = new BundlerServiceHealthCheck(new RecordingHttpClientFactory(client));

        return await check.CheckHealthAsync(ContextFor(check, failure), CancellationToken.None);
    }

    private static HealthCheckContext ContextFor(IHealthCheck check, HealthStatus failure) =>
        new()
        {
            Registration = new HealthCheckRegistration(
                BundlerServiceHealthCheck.Name, check, failure, new[] { "ready" }),
        };

    // ByteArrayContent, not StringContent: the empty case must be exactly 0 bytes
    // on the wire, with no encoding preamble deciding the outcome.
    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public RecordingHttpClientFactory(HttpClient client) => _client = client;

        public string? RequestedName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            RequestedName = name;
            return _client;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _respond = respond;

        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return Task.FromResult(_respond(request));
        }
    }
}
