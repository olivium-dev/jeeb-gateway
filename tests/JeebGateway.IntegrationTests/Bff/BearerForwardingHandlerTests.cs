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

    // -----------------------------------------------------------------
    // GW12-OBS-3 — X-Correlation-Id downstream propagation.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Forwards_CorrelationId_From_Items_To_Outbound_Request()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        // CorrelationIdMiddleware stashes the (possibly minted) id in Items.
        accessor.HttpContext!.Items["CorrelationId"] = "corr-abc-123";

        var handler = new BearerForwardingHandler(accessor) { InnerHandler = new CapturingHandler() };
        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/ping");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.TryGetValues("X-Correlation-Id", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be("corr-abc-123");
    }

    [Fact]
    public async Task Forwards_CorrelationId_From_Inbound_Header_When_Items_Absent()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext!.Request.Headers["X-Correlation-Id"] = "corr-from-header";

        var handler = new BearerForwardingHandler(accessor) { InnerHandler = new CapturingHandler() };
        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/ping");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.TryGetValues("X-Correlation-Id", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be("corr-from-header");
    }

    [Fact]
    public async Task Does_Not_Overwrite_Outbound_CorrelationId_Already_Set()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext!.Items["CorrelationId"] = "inbound-corr";

        var handler = new BearerForwardingHandler(accessor) { InnerHandler = new CapturingHandler() };
        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/ping");
        req.Headers.TryAddWithoutValidation("X-Correlation-Id", "explicit-outbound-corr");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.GetValues("X-Correlation-Id")
            .Should().ContainSingle().Which.Should().Be("explicit-outbound-corr");
    }

    [Fact]
    public async Task Does_Not_Forward_CorrelationId_When_None_Present()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        var handler = new BearerForwardingHandler(accessor) { InnerHandler = new CapturingHandler() };
        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/ping");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.Contains("X-Correlation-Id").Should().BeFalse();
    }

    [Fact]
    public async Task NoOp_CorrelationId_When_No_HttpContext_Available()
    {
        var accessor = new HttpContextAccessor(); // background work, no context

        var handler = new BearerForwardingHandler(accessor) { InnerHandler = new CapturingHandler() };
        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/ping");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.Contains("X-Correlation-Id").Should().BeFalse();
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
