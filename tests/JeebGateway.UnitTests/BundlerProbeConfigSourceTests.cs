using System.Net;
using System.Net.Http;
using System.Text;
using JeebGateway.Cms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace JeebGateway.UnitTests;

// OA-24. The live defect (MSI 2026-08-16) was NOT that the probe dialled the wrong
// place: it dialled exactly what BUNDLER_CMS_BASE_URL resolved to. The defect was
// that TWO layers set that key -- a systemd drop-in AND the env file sourced by
// ExecStart afterwards -- and the drop-in silently lost. From outside, a correct
// drop-in and an overriding env file are indistinguishable, so the wrong host was
// probed for days. These cases pin that the probe now names the winning provider.
public class BundlerProbeConfigSourceTests
{
    private const string Key = BundlerCmsSurfaceStore.BaseUrlConfigurationKey;

    [Fact]
    public void The_single_configuring_provider_is_named()
    {
        var configuration = Build(json: "http://127.0.0.1:10056/");

        Assert.Equal(
            "JsonStreamConfigurationProvider",
            BundlerServiceHealthCheck.DescribeSource(configuration, Key));
    }

    // The anti-construction control for the case above: SAME key, SAME first
    // provider, and the answer changes because a later layer shadows it. This is
    // the shape of the live defect.
    [Fact]
    public void A_later_provider_that_shadows_an_earlier_one_is_named_instead()
    {
        var configuration = Build(
            json: "http://127.0.0.1:10056/", shadow: "http://192.168.2.39:10056/");

        Assert.Equal(
            "MemoryConfigurationProvider",
            BundlerServiceHealthCheck.DescribeSource(configuration, Key));
        // The shadowing value is what the rest of the app sees, so the reported
        // source has to describe the winner, not the intent.
        Assert.Equal("http://192.168.2.39:10056/", configuration[Key]);
    }

    [Fact]
    public void An_unconfigured_key_reports_unset()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal("unset", BundlerServiceHealthCheck.DescribeSource(configuration, Key));
    }

    [Fact]
    public async Task The_probe_publishes_the_winning_source_in_its_data_and_description()
    {
        var configuration = Build(
            json: "http://127.0.0.1:10056/", shadow: "http://192.168.2.39:10056/");
        using var client = new HttpClient(
            new StubHandler(() => Respond(HttpStatusCode.OK, string.Empty)))
        {
            BaseAddress = new Uri("http://192.168.2.39:10056/"),
        };
        var check = new BundlerServiceHealthCheck(
            new SingleClientFactory(client), configuration);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration(
                    BundlerServiceHealthCheck.Name, check, HealthStatus.Degraded,
                    new[] { "ready" }),
            },
            CancellationToken.None);

        Assert.Equal("MemoryConfigurationProvider", result.Data["baseUrlSource"]);
        Assert.Contains("MemoryConfigurationProvider", result.Description ?? string.Empty);
        Assert.Contains(Key, result.Description ?? string.Empty);
    }

    // Guards the null path: DI always supplies IConfiguration, but the parameter is
    // optional so the older probe tests keep constructing the check with one argument.
    [Fact]
    public void A_missing_configuration_reports_unknown_rather_than_throwing()
    {
        Assert.Equal("unknown", BundlerServiceHealthCheck.DescribeSource(null, Key));
    }

    // A JSON provider first, then an optional in-memory layer on top. The two
    // provider TYPES differ, which is what makes the winner observable. Production
    // uses Json + EnvironmentVariables providers; only the type NAMES differ here.
    private static IConfigurationRoot Build(string json, string? shadow = null)
    {
        var builder = new ConfigurationBuilder().AddJsonStream(
            new MemoryStream(Encoding.UTF8.GetBytes($"{{\"{Key}\":\"{json}\"}}")));
        if (shadow is not null)
        {
            builder.AddInMemoryCollection(
                new Dictionary<string, string?> { [Key] = shadow });
        }

        return builder.Build();
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond());
    }
}
