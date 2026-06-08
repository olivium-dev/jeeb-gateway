using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Extensions;
using JeebGateway.Services.Bff;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// S06 / ADR-HB-001 AUTH CONTRACT regression guard.
///
/// <para>
/// heart-beat (Go) authenticates EITHER a static <c>X-Service-Auth-Key</c>
/// (constant-time compared against its <c>HEARTBEAT_SERVICE_AUTH_KEY</c>) OR a
/// JWKS-validated end-user JWT. It does NOT verify the gateway's HMAC
/// <c>X-Service-Auth</c> scheme, and no fleet JWKS exists to validate the
/// forwarded HS256 mobile bearer. So a flag-ON gateway → heart-beat call only
/// authenticates if the gateway presents the static
/// <c>X-Service-Auth-Key</c> header that heart-beat already accepts.
/// </para>
///
/// <para>
/// These tests resolve the REAL heart-beat typed client through the REAL
/// gateway DI handler chain (the same chain <see cref="IHttpMessageHandlerFactory"/>
/// builds in production) and assert that:
/// </para>
/// <list type="number">
///   <item>The chain contains <see cref="HeartBeatServiceAuthKeyHandler"/>.</item>
///   <item>On the gateway's exact outbound calls
///     (<c>PATCH /v1/presence</c> and <c>GET /v1/presence/{userId}</c>), the
///     handler attaches <c>X-Service-Auth-Key</c> carrying the configured key —
///     the precise header value heart-beat's auth middleware constant-time
///     compares to grant the service-auth principal (200, not 401).</item>
///   <item>When the key is unconfigured (the flag-off committed default), the
///     header is NOT attached — proving the change is inert until the secret is
///     injected.</item>
/// </list>
///
/// The matching heart-beat-side proof (this exact header value authenticates
/// against the real Go <c>authMiddleware</c> → 200) lives in
/// <c>heart-beat/internal/api/auth_gateway_handshake_test.go</c>.
/// </summary>
public class HeartBeatServiceAuthHandshakeTests
{
    private const string ConfiguredKey = "hb-service-auth-key-integration-test-32-chars+";

    [Fact]
    public void HeartBeat_Client_Chain_Contains_ServiceAuthKey_Handler()
    {
        using var provider = BuildGatewayServiceProvider(ConfiguredKey);
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var chain = WalkHandlerChain(factory.CreateHandler("IHeartBeatServiceClient"));

        chain.Should().Contain(h => h is HeartBeatServiceAuthKeyHandler,
            "the heart-beat client must present the static X-Service-Auth-Key heart-beat accepts");
        // It still carries the standard pipeline (bearer + HMAC ride along, ignored
        // by heart-beat's static-key path; harmless defence-in-depth).
        chain.Should().Contain(h => h is BearerForwardingHandler);
        chain.Should().Contain(h => h is ServiceAuthSigningHandler);
    }

    [Theory]
    [InlineData("PATCH", "v1/presence", "/v1/presence")]
    [InlineData("GET", "v1/presence/jeeber-1", "/v1/presence/jeeber-1")]
    public async Task Gateway_Outbound_Call_Carries_ServiceAuthKey_Header_With_Configured_Value(
        string method, string relativePath, string expectedAbsolutePath)
    {
        var (client, recorder) = BuildHeartBeatHttpClient(ConfiguredKey);

        using var request = new HttpRequestMessage(new HttpMethod(method), relativePath);
        using var _ = await client.SendAsync(request);

        recorder.Last.Should().NotBeNull();
        recorder.Last!.RequestUri!.AbsolutePath.Should().Be(expectedAbsolutePath);

        recorder.Last.Headers.TryGetValues(
            HeartBeatServiceAuthKeyHandler.HeaderName, out var values).Should().BeTrue(
            "heart-beat authenticates the gateway via the static X-Service-Auth-Key header");
        values!.Single().Should().Be(ConfiguredKey,
            "the exact configured key is what heart-beat constant-time compares to authenticate (200, not 401)");
    }

    [Fact]
    public async Task ServiceAuthKey_Handler_Strips_Inherited_Hmac_So_Static_Path_Is_Reached()
    {
        // Footgun guard: even with the fleet-wide ServiceAuth:Enabled=true (so the
        // ServiceAuthSigningHandler emits an HMAC signed with the FLEET key, which
        // is NOT heart-beat's fresh key), the heart-beat client must STRIP that
        // HMAC and present only the static X-Service-Auth-Key. Otherwise
        // heart-beat (HMAC-checked-first) would 401 on the non-shared signature.
        var (client, recorder) = BuildHeartBeatHttpClient(ConfiguredKey);

        using var request = new HttpRequestMessage(HttpMethod.Patch, "v1/presence");
        using var _ = await client.SendAsync(request);

        recorder.Last!.Headers.Contains("X-Service-Auth").Should().BeFalse(
            "the non-shared fleet HMAC must be stripped so heart-beat reaches the static-key path");
        recorder.Last.Headers.GetValues(HeartBeatServiceAuthKeyHandler.HeaderName).Single()
            .Should().Be(ConfiguredKey);
    }

    [Fact]
    public async Task No_ServiceAuthKey_Header_When_Key_Is_Unconfigured()
    {
        // Flag-off committed default: ServiceAuthKey blank => no header attached,
        // so the change is fully inert until the secret is injected.
        var (client, recorder) = BuildHeartBeatHttpClient(serviceAuthKey: "");

        using var request = new HttpRequestMessage(HttpMethod.Patch, "v1/presence");
        using var _ = await client.SendAsync(request);

        recorder.Last!.Headers.Contains(HeartBeatServiceAuthKeyHandler.HeaderName)
            .Should().BeFalse();
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds an <see cref="HttpClient"/> whose handler chain is the heart-beat
    /// client's REAL DI-registered chain, terminated by a recording handler that
    /// captures the final outbound request (so the assertions run against exactly
    /// what would hit heart-beat on the wire).
    /// </summary>
    private static (HttpClient client, RecordingHandler recorder) BuildHeartBeatHttpClient(string serviceAuthKey)
    {
        var provider = BuildGatewayServiceProvider(serviceAuthKey);
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        // Walk to the innermost DelegatingHandler and graft a recording primary
        // handler onto it so we observe the fully-decorated outbound request.
        var root = factory.CreateHandler("IHeartBeatServiceClient");
        var recorder = new RecordingHandler();
        var innermost = root;
        while (innermost is DelegatingHandler { InnerHandler: DelegatingHandler next })
        {
            innermost = next;
        }
        ((DelegatingHandler)innermost).InnerHandler = recorder;

        var client = new HttpClient(root) { BaseAddress = new Uri("http://heart-beat.test/") };
        return (client, recorder);
    }

    private static ServiceProvider BuildGatewayServiceProvider(string serviceAuthKey)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:HeartBeat:BaseUrl"] = "http://heart-beat.test/",
                ["FeatureFlags:Heartbeat:Enabled"] = "true",
                ["FeatureFlags:Heartbeat:ServiceAuthKey"] = serviceAuthKey,
                ["ServiceAuth:Caller"] = "jeeb-gateway",
                ["ServiceAuth:SigningKey"] = "integration-test-signing-key-32-chars-or-longer",
                ["ServiceAuth:Enabled"] = "true",
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);

        // Mirror Program.cs options binding: the handlers resolve these via
        // IOptions / IOptionsMonitor. AddDownstreamClients does NOT bind them.
        services.Configure<ServiceAuthOptions>(config.GetSection(ServiceAuthOptions.SectionName));
        services.Configure<HeartbeatFeatureOptions>(
            config.GetSection(HeartbeatFeatureOptions.SectionName));

        services.AddDownstreamClients(config);

        return services.BuildServiceProvider();
    }

    private static IReadOnlyList<HttpMessageHandler> WalkHandlerChain(HttpMessageHandler root)
    {
        var chain = new List<HttpMessageHandler>();
        var current = root;
        while (current is not null)
        {
            chain.Add(current);
            current = current is DelegatingHandler d ? d.InnerHandler : null;
        }
        return chain;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"userId":"jeeber-1","online":true}"""),
            });
        }
    }
}
