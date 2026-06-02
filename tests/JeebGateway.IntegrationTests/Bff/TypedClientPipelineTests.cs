using FluentAssertions;
using JeebGateway.Extensions;
using JeebGateway.Services.Bff;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// Batch 1 P0 regression guard. A typed <c>AddHttpClient&lt;IFoo, Foo&gt;</c>
/// registration does NOT inherit the handler chain of a separately-registered
/// NAMED client of the same logical name — <see cref="IHttpClientFactory"/>
/// keys handler chains by client name, and a typed client is keyed by its
/// interface type name. Before the Batch 1 fix the typed downstream clients
/// (Notification, Push, Wallet, Delivery, Geolocation, …) were registered
/// with a BARE handler chain, so they silently bypassed bearer forwarding,
/// X-Service-Auth signing, and the Polly resilience pipeline.
///
/// These tests resolve the REAL handler chain that
/// <see cref="IHttpMessageHandlerFactory"/> builds for each typed client and
/// assert both DelegatingHandlers are present. They would FAIL against the
/// pre-fix bare registration.
/// </summary>
public class TypedClientPipelineTests
{
    // Typed clients are keyed by the interface's type name in
    // AddHttpClient<TInterface, TImpl>(). These are the post-auth downstream
    // clients that MUST carry bearer + X-Service-Auth + resilience.
    public static IEnumerable<object[]> PostAuthTypedClientNames() => new[]
    {
        new object[] { "IAuthServiceClient" },
        new object[] { "IDeliveryServiceClient" },
        new object[] { "IMatchingServiceClient" },
        new object[] { "IGeolocationServiceClient" },
        new object[] { "INotificationServiceClient" },
        new object[] { "IScoreServiceClient" },
        new object[] { "IFeedbackServiceClient" },
        new object[] { "ICDNServiceClient" },
        new object[] { "IServiceOTPClient" },
        new object[] { "IPushNotificationClient" },
        new object[] { "IFormBuilderServiceClient" },
    };

    [Theory]
    [MemberData(nameof(PostAuthTypedClientNames))]
    public void Typed_Downstream_Client_Carries_Bearer_And_ServiceAuth_Handlers(string clientName)
    {
        using var provider = BuildGatewayServiceProvider();
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var chain = WalkHandlerChain(factory.CreateHandler(clientName));

        chain.Should().Contain(h => h is BearerForwardingHandler,
            $"typed client '{clientName}' must forward the inbound mobile JWT");
        chain.Should().Contain(h => h is ServiceAuthSigningHandler,
            $"typed client '{clientName}' must sign X-Service-Auth on every call");
    }

    [Theory]
    [MemberData(nameof(PostAuthTypedClientNames))]
    public void Typed_Downstream_Client_Has_Resilience_Handler(string clientName)
    {
        using var provider = BuildGatewayServiceProvider();
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var chain = WalkHandlerChain(factory.CreateHandler(clientName));

        // The Polly resilience handler is a DelegatingHandler from
        // Microsoft.Extensions.Http.Resilience; assert at least one handler in
        // the chain originates from that package so retries/circuit-breaker/
        // timeout are present.
        chain.Should().Contain(
            h => h.GetType().FullName!.Contains("Resilience", StringComparison.Ordinal),
            $"typed client '{clientName}' must carry the standard resilience pipeline");
    }

    private static ServiceProvider BuildGatewayServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // ServiceAuthSigningHandler depends on TimeProvider; Program.cs registers
        // it but AddDownstreamClients does not, so supply it here.
        services.AddSingleton(TimeProvider.System);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Bare/nested BaseUrls so registrations resolve; values are
                // irrelevant — we only inspect the handler chain, not call out.
                ["Services:Auth"] = "http://auth.test",
                ["Services:Delivery"] = "http://delivery.test",
                ["Services:Matching"] = "http://matching.test",
                ["Services:Geolocation"] = "http://geo.test",
                ["Services:Notification:BaseUrl"] = "http://notif.test",
                ["Services:ScoreTaking:BaseUrl"] = "http://score.test",
                ["Services:Feedback:BaseUrl"] = "http://feedback.test",
                ["Services:Cdn:BaseUrl"] = "http://cdn.test",
                ["Services:ServiceOTP:BaseUrl"] = "http://otp.test",
                ["Services:PushNotification:BaseUrl"] = "http://push.test",
                ["Services:FormBuilder:BaseUrl"] = "http://form-builder.test",
                ["ServiceAuth:Caller"] = "jeeb-gateway",
                ["ServiceAuth:SigningKey"] = "integration-test-signing-key-32-chars-or-longer",
                ["ServiceAuth:Enabled"] = "true",
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);

        // Mirrors Program.cs: AddDownstreamClients registers the transient
        // bearer/ServiceAuth handlers and every typed downstream client.
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
}
