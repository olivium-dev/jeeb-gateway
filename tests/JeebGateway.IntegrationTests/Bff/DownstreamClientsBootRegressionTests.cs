using FluentAssertions;
using JeebGateway.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// GAP-1 regression guard (boot crash, exit 139).
///
/// Before the fix, <see cref="ServiceClientExtensions.AddDownstreamClients"/>
/// registered TWO typed clients both short-named <c>IGeolocationServiceClient</c>
/// — the legacy hand-coded <c>Services.Clients.IGeolocationServiceClient</c>
/// (dead code, never injected) and the live NSwag-generated
/// <c>Services.Generated.GeolocationService.IGeolocationServiceClient</c>.
/// <see cref="System.Net.Http.IHttpClientFactory"/> keys typed clients by the
/// SHORT type name (namespace-insensitive), so the second
/// <c>AddHttpClient&lt;T,Impl&gt;</c> collided with the first and threw an
/// <see cref="System.InvalidOperationException"/> UNCONDITIONALLY at startup —
/// the gateway could not boot on any environment (the host process exited 139).
///
/// This test exercises the exact crashing path: it runs the real DI
/// registration and creates the geolocation typed client's handler chain. It
/// would FAIL (throw) against the pre-fix duplicate registration and PASSES now
/// that the dead legacy registration/interface/impl are removed.
/// </summary>
public sealed class DownstreamClientsBootRegressionTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Only the values needed for the registrations to materialise;
                // we never dial out, we only build the container + handler chain.
                ["Services:Auth"] = "http://auth.test",
                ["Services:Delivery"] = "http://delivery.test",
                ["Services:Matching"] = "http://matching.test",
                ["Services:Geolocation"] = "http://geo.test",
                ["Services:Cdn:BaseUrl"] = "http://cdn.test",
                ["Services:ServiceOTP:BaseUrl"] = "http://otp.test",
                ["Services:FormBuilder:BaseUrl"] = "http://form-builder.test",
                ["ServiceAuth:Caller"] = "jeeb-gateway",
                ["ServiceAuth:SigningKey"] = "integration-test-signing-key-32-chars-or-longer",
                ["ServiceAuth:Enabled"] = "true",
            })
            .Build();

    [Fact]
    public void AddDownstreamClients_BuildsContainer_WithoutDuplicateClientName_Crash()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        var config = BuildConfig();
        services.AddSingleton(config);

        // The two acts that crashed on the pre-fix branch: registering every
        // downstream client (duplicate short-named typed client), then building
        // the provider and materialising the geolocation handler chain.
        Action act = () =>
        {
            services.AddDownstreamClients(config);
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
            // Forces IHttpClientFactory to resolve the client keyed by the short
            // type name "IGeolocationServiceClient" — the exact collision point.
            using var handler = factory.CreateHandler("IGeolocationServiceClient");
            handler.Should().NotBeNull();
        };

        act.Should().NotThrow(
            "the gateway must boot deterministically — there must be exactly one " +
            "typed client short-named 'IGeolocationServiceClient' (GAP-1 / exit 139)");
    }
}
