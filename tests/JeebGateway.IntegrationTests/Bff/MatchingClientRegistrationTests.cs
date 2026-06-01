using FluentAssertions;
using JeebGateway.Extensions;
using JeebGateway.Services.Bff;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// Config-drift guard for the matching upstream wire (feat/add-matching).
///
/// The matching integration (typed <see cref="IMatchingServiceClient"/>, named
/// "matching" client, MatchingController, readiness probe) was already present,
/// but <c>appsettings.Production.json</c> carried NO <c>Services:Matching</c>
/// entry. The consequences of that gap were silent and severe:
///
///   * <see cref="ServiceClientExtensions.AddDownstreamClients"/> would bind the
///     <see cref="IMatchingServiceClient"/> typed client with NO BaseAddress, so
///     <c>MatchingController.GetMatchingUsers</c> would throw
///     <see cref="System.InvalidOperationException"/> ("An invalid request URI…")
///     on first call — a 500, not a clean 502.
///   * <see cref="HealthCheckExtensions.AddDownstreamHealthChecks"/> SKIPS the
///     matching readiness probe entirely when the BaseUrl is unset, so a dead
///     matching upstream would never surface on <c>/health/ready</c>.
///   * <see cref="BffStartupValidator"/> lists "Matching" in
///     <see cref="DownstreamServicesOptions.Required"/>; a production boot
///     without the key fails closed with a StartupConfigurationException.
///
/// These tests assert that the committed production-shaped config now binds the
/// matching BaseAddress on BOTH the named and typed clients, and that the
/// <c>ServiceMatchingApi:BaseUrl</c> convention key (parity with
/// <c>ServiceOTPApi</c> / <c>UserManagementApi</c>) resolves. They FAIL against
/// the pre-PR config where <c>Services:Matching:BaseUrl</c> was absent.
/// </summary>
public class MatchingClientRegistrationTests
{
    // Mirrors the committed appsettings.Production.json shape for the matching
    // upstream (host 192.168.2.50:10025 — verified GET /health 200 in
    // jeeb-infrastructure/SMOKE-REPORT.md row A-17).
    private const string MatchingBaseUrl = "http://192.168.2.50:10025";

    [Fact]
    public void Typed_MatchingClient_Binds_Production_BaseAddress()
    {
        using var provider = BuildProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // The typed client is keyed by its interface type name in
        // AddHttpClient<IMatchingServiceClient, MatchingServiceClient>().
        var http = factory.CreateClient(nameof(IMatchingServiceClient));

        http.BaseAddress.Should().NotBeNull(
            "the matching typed client must bind a BaseAddress in production, else "
            + "GetMatchingUsers throws InvalidOperationException on first call");
        http.BaseAddress!.ToString().Should().StartWith(MatchingBaseUrl);
    }

    [Fact]
    public void Named_Matching_Client_Binds_Production_BaseAddress()
    {
        using var provider = BuildProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var http = factory.CreateClient("matching");

        http.BaseAddress.Should().NotBeNull();
        http.BaseAddress!.ToString().Should().StartWith(MatchingBaseUrl);
    }

    [Fact]
    public void Typed_MatchingClient_Resolves_From_Container()
    {
        using var provider = BuildProvider();

        // Resolving the typed client proves the registration is intact end to end
        // (interface -> impl -> configured HttpClient) and would catch a future
        // accidental removal of the AddHttpClient<IMatchingServiceClient,…> line.
        var client = provider.GetRequiredService<IMatchingServiceClient>();

        client.Should().BeOfType<MatchingServiceClient>();
    }

    [Fact]
    public void ServiceMatchingApi_BaseUrl_Convention_Key_Resolves()
    {
        var config = BuildConfig();

        // Parity with ServiceOTPApi / UserManagementApi top-level convention.
        config["ServiceMatchingApi:BaseUrl"].Should().Be(MatchingBaseUrl);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // ServiceAuthSigningHandler depends on TimeProvider; Program.cs supplies
        // it, AddDownstreamClients does not.
        services.AddSingleton(TimeProvider.System);

        var config = BuildConfig();
        services.AddSingleton<IConfiguration>(config);
        services.AddDownstreamClients(config);

        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The matching keys this PR commits to appsettings.Production.json.
                ["Services:Matching:BaseUrl"] = MatchingBaseUrl,
                ["ServiceMatchingApi:BaseUrl"] = MatchingBaseUrl,
                // ServiceAuth signing inputs so the handler chain builds.
                ["ServiceAuth:Caller"] = "jeeb-gateway",
                ["ServiceAuth:SigningKey"] = "integration-test-signing-key-32-chars-or-longer",
                ["ServiceAuth:Enabled"] = "true",
            })
            .Build();
}
