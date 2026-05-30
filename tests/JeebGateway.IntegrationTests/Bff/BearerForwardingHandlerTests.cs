using System.Net;
using FluentAssertions;
using JeebGateway.Services.Bff;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// JEB-67 / T-BE-031 AC3 — BearerForwardingHandler unit tests.
///
/// Asserts:
///   * inbound Authorization: Bearer xxx is forwarded to the outbound request
///   * absent inbound bearer leaves the outbound request without one
///   * non-bearer schemes are ignored (we only forward Bearer)
///   * outbound Authorization already set is not overwritten
///   * background work without HttpContext is a no-op
/// </summary>
public class BearerForwardingHandlerTests
{
    [Fact]
    public async Task Forwards_Inbound_Bearer_To_Outbound_Request()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        accessor.HttpContext!.Request.Headers["Authorization"] = "Bearer eyJ-mobile-jwt";

        var handler = new BearerForwardingHandler(accessor)
        {
            InnerHandler = new CapturingHandler(),
        };

        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/ping");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.Authorization.Should().NotBeNull();
        capturing.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        capturing.LastRequest.Headers.Authorization.Parameter.Should().Be("eyJ-mobile-jwt");
    }

    [Fact]
    public async Task Does_Not_Forward_When_Inbound_Has_No_Authorization()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };

        var handler = new BearerForwardingHandler(accessor)
        {
            InnerHandler = new CapturingHandler(),
        };

        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/ping");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task Ignores_Non_Bearer_Schemes()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        accessor.HttpContext!.Request.Headers["Authorization"] = "Basic dXNlcjpwYXNz";

        var handler = new BearerForwardingHandler(accessor)
        {
            InnerHandler = new CapturingHandler(),
        };

        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/ping");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task Does_Not_Overwrite_Outbound_Authorization_Already_Set()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        accessor.HttpContext!.Request.Headers["Authorization"] = "Bearer inbound-token";

        var handler = new BearerForwardingHandler(accessor)
        {
            InnerHandler = new CapturingHandler(),
        };

        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/ping");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "explicit-outbound-token");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.Authorization!.Parameter.Should().Be("explicit-outbound-token");
    }

    [Fact]
    public async Task NoOp_When_No_HttpContext_Available()
    {
        var accessor = new HttpContextAccessor(); // HttpContext = null (background work)

        var handler = new BearerForwardingHandler(accessor)
        {
            InnerHandler = new CapturingHandler(),
        };

        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/ping");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    /// <summary>
    /// Captures the outbound HttpRequestMessage so the test can assert on
    /// the headers the handler attached. Returns a canned 200 so the
    /// invoker pipeline completes.
    /// </summary>
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
