using System.Net;
using System.Net.Http;
using System.Text;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.UnitTests;

// D1 (MSI 2026-08-15/16): role-service pointed at a decommissioned DB host, answered
// /health/ready 503, and every GET /v1/users/me 500ed — while the gateway aggregate
// stayed green because role-service had no probe at all.
public class RoleServiceHealthCheckFailClosedTests
{
    // The structural trap the task calls out: a bare listener answers 200 with
    // Content-Length: 0, so a status-only probe can never fail.
    [Fact]
    public async Task A_200_with_an_empty_body_is_unhealthy_on_the_live_path()
    {
        var result = await CheckAsync(
            _ => Respond(HttpStatusCode.OK, string.Empty), livePath: true);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("EMPTY body", result.Description ?? string.Empty);
        Assert.Equal(0, Convert.ToInt32(result.Data["bodyBytes"]));
    }

    // The exact live failure: role-service cannot open its Postgres, so it 503s.
    // Body is non-empty so the empty-body branch is not what makes this fail.
    [Fact]
    public async Task A_503_is_unhealthy_on_the_live_path()
    {
        var result = await CheckAsync(
            _ => Respond(HttpStatusCode.ServiceUnavailable, "Unhealthy"), livePath: true);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("503", result.Description ?? string.Empty);
    }

    [Fact]
    public async Task A_transport_failure_is_unhealthy_on_the_live_path()
    {
        var result = await CheckAsync(
            _ => throw new HttpRequestException("connection refused"), livePath: true);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("unreachable", result.Description ?? string.Empty);
    }

    // Off the live path user-management answers roles, so the same hard failure
    // must stay visible without 503-ing the whole gateway.
    [Fact]
    public async Task The_same_failure_is_only_degraded_when_the_kill_switch_is_off()
    {
        var result = await CheckAsync(
            _ => Respond(HttpStatusCode.ServiceUnavailable, "Unhealthy"), livePath: false);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    // The anti-construction control: proves the probe can still pass, so the
    // failures above are not an always-fails probe.
    [Fact]
    public async Task A_200_with_a_real_body_is_healthy()
    {
        var result = await CheckAsync(_ => Respond(HttpStatusCode.OK, "Healthy"), livePath: true);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True(Convert.ToInt32(result.Data["bodyBytes"]) > 0);
    }

    [Fact]
    public async Task An_unset_base_url_is_unhealthy_rather_than_a_silent_pass()
    {
        var result = await CheckAsync(
            _ => Respond(HttpStatusCode.OK, "Healthy"), livePath: true, baseUrl: null);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("BaseUrl", result.Description ?? string.Empty);
    }

    [Fact]
    public async Task The_probe_dials_the_readiness_route()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, "Healthy"));
        await RunAsync(handler, livePath: true, baseUrl: "http://role.test");

        Assert.Equal("/health/ready", Assert.Single(handler.Paths));
    }

    private static Task<HealthCheckResult> CheckAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        bool livePath,
        string? baseUrl = "http://role.test") =>
        RunAsync(new StubHandler(respond), livePath, baseUrl);

    private static async Task<HealthCheckResult> RunAsync(
        StubHandler handler, bool livePath, string? baseUrl)
    {
        using var client = new HttpClient(handler);
        var check = new RoleServiceHealthCheck(
            new StubHttpClientFactory(client),
            new StubMonitor<RoleServiceOptions>(new RoleServiceOptions { BaseUrl = baseUrl }),
            new StubMonitor<UpstreamFeatureFlags>(new UpstreamFeatureFlags { RoleService = livePath }));

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                RoleServiceHealthCheck.Name, check, HealthStatus.Unhealthy, new[] { "ready" }),
        };

        return await check.CheckHealthAsync(context, CancellationToken.None);
    }

    // ByteArrayContent, not StringContent: the empty case must be exactly 0 bytes
    // on the wire, with no encoding preamble deciding the outcome.
    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubMonitor<T> : IOptionsMonitor<T>
    {
        public StubMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
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
