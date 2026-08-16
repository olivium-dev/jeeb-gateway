using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Cms;
using JeebGateway.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Cms;

/// <summary>
/// R2-2 regression cover for the probe that replaced the status-code-only URL group.
///
/// <para>The live defect, measured on MSI 2026-08-16: a Host-matching reverse proxy answered
/// <c>200</c> with <c>Content-Length: 0</c> on EVERY path because no bundler site block matched the
/// host, so <c>/health/aggregate</c> reported bundler-service <b>Healthy in 1.44 ms</b> while the
/// service itself answered <c>503</c>. A probe that only reads the status line cannot see that, so
/// these cases pin the two claims that make the probe capable of failing: it asserts on the BODY,
/// and it dials the readiness route rather than the liveness-only one.</para>
/// </summary>
public sealed class BundlerServiceHealthCheckTests
{
    [Fact]
    public async Task An_Empty_200_Is_Not_Healthy()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, string.Empty));

        var result = await CheckAsync(handler);

        // The negative control: the status line alone says "success", which is exactly why the old
        // URL-group probe read Healthy against a proxy default.
        handler.LastStatusWasSuccess.Should().BeTrue();
        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("EMPTY body",
            "the operator has to be told this is a proxy default, not an answer from bundler");
    }

    [Fact]
    public async Task A_503_Is_Not_Healthy()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.ServiceUnavailable, "not ready"));

        var result = await CheckAsync(handler);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("503");
    }

    [Fact]
    public async Task A_Real_Non_Empty_200_Is_Healthy()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, """{"status":"Healthy"}"""));

        var result = await CheckAsync(handler);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task An_Unreachable_Bundler_Is_Not_Healthy()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var result = await CheckAsync(handler);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("unreachable");
    }

    [Fact]
    public async Task The_Probe_Dials_The_Readiness_Route_Not_The_Liveness_One()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "ok"));

        await CheckAsync(handler);

        handler.Paths.Should().Equal("/health/ready",
            "health/live is liveness-only; readiness is what reflects bundler's own dependencies");
    }

    [Fact]
    public void The_Registration_Is_The_Body_Asserting_Check_And_Only_Degrades_The_Gateway()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddDownstreamHealthChecks(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BundlerCmsSurfaceStore.BaseUrlConfigurationKey] = "http://bundler.test/",
            }).Build(),
            new StubEnvironment("Production"));

        using var provider = services.BuildServiceProvider();
        var registration = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Single(r => r.Name == BundlerServiceHealthCheck.Name);

        registration.Factory(provider).Should().BeOfType<BundlerServiceHealthCheck>(
            "a URL-group probe cannot fail against a proxy that returns an empty 200");
        registration.FailureStatus.Should().Be(HealthStatus.Degraded,
            "bundler backs the ADMIN authoring plane only, so its outage belongs in failing[] "
            + "rather than 503-ing /health/ready for the whole gateway");
        registration.Tags.Should().Contain("ready").And.Contain("downstream");
        GatewayHealthRoster.DownstreamProbes.Select(p => p.Name)
            .Should().Contain(BundlerServiceHealthCheck.Name,
                "the roster declares it, so name and registration cannot drift");
    }

    private static async Task<HealthCheckResult> CheckAsync(StubHandler handler)
    {
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://bundler.test/") };
        var check = new BundlerServiceHealthCheck(new FixedHttpClientFactory(client));
        var registration = new HealthCheckRegistration(
            BundlerServiceHealthCheck.Name, check, HealthStatus.Degraded, new[] { "ready" });

        return await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration }, CancellationToken.None);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        public bool LastStatusWasSuccess { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            var response = respond(request);
            LastStatusWasSuccess = response.IsSuccessStatusCode;
            return Task.FromResult(response);
        }
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public StubEnvironment(string name) => EnvironmentName = name;

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "JeebGateway";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
