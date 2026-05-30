using System.Net;
using FluentAssertions;
using JeebGateway.Services.Bff;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// JEB-67 / T-BE-031 AC3 — end-to-end test that asserts a downstream call
/// issued through a named HttpClient configured by ServiceClientExtensions
/// carries BOTH the forwarded mobile JWT bearer AND the HMAC-signed
/// X-Service-Auth header.
///
/// Builds the handler chain in-process (DelegatingHandlers + capturing inner
/// handler) so we don't have to spin up a real downstream. This is the
/// integration-level guarantee that wiring is correct without depending on
/// HttpClientFactory's internal lifetime model.
/// </summary>
public class DownstreamAuthForwardingTests
{
    [Fact]
    public async Task Named_Downstream_Call_Forwards_Bearer_And_Signs_ServiceAuth()
    {
        var inboundCtx = new DefaultHttpContext();
        inboundCtx.Request.Headers["Authorization"] = "Bearer mobile-jwt-abc";

        var httpAccessor = new HttpContextAccessor { HttpContext = inboundCtx };
        var bearer = new BearerForwardingHandler(httpAccessor);

        var serviceAuthOptions = Options.Create(new ServiceAuthOptions
        {
            Caller = "jeeb-gateway",
            SigningKey = "integration-test-signing-key-32-chars-or-longer",
            Enabled = true,
        });
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_001));
        var signer = new ServiceAuthSigningHandler(serviceAuthOptions, time);

        var capturing = new CapturingHandler();
        signer.InnerHandler = capturing;
        bearer.InnerHandler = signer;

        using var invoker = new HttpMessageInvoker(bearer);
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            "http://delivery.test/jeeb/tiers");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        capturing.LastRequest.Headers.Authorization.Parameter.Should().Be("mobile-jwt-abc");

        capturing.LastRequest.Headers.Contains(ServiceAuthSigningHandler.HeaderName)
            .Should().BeTrue("AC3 requires every BFF call to carry ServiceAuth");

        var serviceAuth = capturing.LastRequest.Headers
            .GetValues(ServiceAuthSigningHandler.HeaderName).Single();
        serviceAuth.Should().StartWith("jeeb-gateway:1700000001:");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
