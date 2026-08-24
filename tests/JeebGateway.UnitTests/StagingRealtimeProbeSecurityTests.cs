using FluentAssertions;
using JeebGateway.Operations.RealtimeProbe;
using JeebGateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class StagingRealtimeProbeSecurityTests
{
    [Fact]
    public async Task DedicatedProbeRoute_BypassesLegacyBroadInternalApiKeyGate()
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var options = Substitute.For<IOptionsMonitor<SecurityOptions>>();
        options.CurrentValue.Returns(new SecurityOptions
        {
            ApiKey = new SecurityOptions.ApiKeyConfig
            {
                Enabled = true,
                ServiceKeys = new Dictionary<string, string>
                {
                    ["legacy-service"] = "test-only-broad-key",
                },
            },
        });
        var middleware = new ApiKeyAuthenticationMiddleware(
            next,
            options,
            NullLogger<ApiKeyAuthenticationMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = StagingRealtimeProbeEndpoint.Route;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(
            "the route is authenticated only by its dedicated timestamped HMAC handler");
    }

    [Fact]
    public async Task OrdinaryInternalRoute_RemainsProtectedByLegacyApiKeyGate()
    {
        var nextCalled = false;
        var options = Substitute.For<IOptionsMonitor<SecurityOptions>>();
        options.CurrentValue.Returns(new SecurityOptions
        {
            ApiKey = new SecurityOptions.ApiKeyConfig
            {
                Enabled = true,
                ServiceKeys = new Dictionary<string, string>
                {
                    ["legacy-service"] = "test-only-broad-key",
                },
            },
        });
        var middleware = new ApiKeyAuthenticationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            options,
            NullLogger<ApiKeyAuthenticationMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/internal/other";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }
}
