using System.Net.Http;
using FluentAssertions;
using JeebGateway.Extensions;
using JeebGateway.Services.Cdn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Cdn;

/// <summary>
/// JEBV4-259 SECURITY (CWE-918) — the dedicated <c>cdn-proxy</c> HttpClient must NOT
/// follow redirects. Without a pinned primary handler, <c>AllowAutoRedirect</c> defaults
/// to <c>true</c>, so a cdn <c>3xx</c> Location would be chased by the gateway to an
/// arbitrary host. This asserts the fix at the source: the client's configured primary
/// handler is a <see cref="SocketsHttpHandler"/> with <c>AllowAutoRedirect == false</c>.
///
/// <para>Behavioural redirect testing is not faithful here: the streaming-proxy tests
/// SWAP the primary handler for a capturing stub, which would mask the real handler's
/// setting. So we assert the registered configuration directly.</para>
/// </summary>
public sealed class CdnUploadProxyHttpClientTests
{
    [Fact]
    public void CdnProxy_Client_Does_Not_Follow_Redirects()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:Cdn:BaseUrl"] = "http://cdn-test.internal:10072/",
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);

        services.AddDownstreamClients(config);

        using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(CdnUploadUrlResolver.ProxyHttpClientName);

        // Materialise the handler chain exactly as HttpClientFactory would, then inspect
        // the primary handler the registration pinned.
        var builder = new ProbeHandlerBuilder { Name = CdnUploadUrlResolver.ProxyHttpClientName };
        foreach (var configure in options.HttpMessageHandlerBuilderActions)
        {
            configure(builder);
        }

        builder.PrimaryHandler.Should().BeOfType<SocketsHttpHandler>(
            "the cdn-proxy client pins its own primary handler (Fix 3)")
            .Which.AllowAutoRedirect.Should().BeFalse(
                "the gateway must relay a cdn 3xx to the client, never chase the Location to an arbitrary host (CWE-918)");
    }

    /// <summary>Minimal <see cref="HttpMessageHandlerBuilder"/> to run the registered handler actions against.</summary>
    private sealed class ProbeHandlerBuilder : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();
        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
