using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using Yarp.ReverseProxy.Forwarder;

namespace JeebGateway.Realtime.Proxy;

internal static class RealtimeWebSocketProxyEndpoint
{
    public const string Route = "/socket/websocket";

    public static bool IsSensitivePath(PathString path) =>
        path.StartsWithSegments(new PathString(Route), StringComparison.OrdinalIgnoreCase);

    public static IEndpointConventionBuilder MapRealtimeWebSocketProxy(
        this IEndpointRouteBuilder endpoints,
        IHostEnvironment environment)
    {
        var options = endpoints.ServiceProvider
            .GetRequiredService<IOptions<RealtimeWebSocketProxyOptions>>().Value;

        RealtimeWebSocketProxyStartupGuard.EnsureEnvironment(options, environment);

        if (!options.Enabled)
        {
            return new DisabledEndpointConventionBuilder();
        }

        return endpoints.MapMethods(Route, new[] { HttpMethods.Get }, ForwardAsync)
            .AllowAnonymous();
    }

    private static async Task ForwardAsync(
        HttpContext context,
        IRealtimeProxyDestinationResolver destinationResolver,
        IHttpForwarder forwarder,
        RealtimeWebSocketProxyTransport transport,
        RealtimeWebSocketProxyTransformer transformer,
        RealtimeWebSocketProxyConcurrencyLimiter concurrency,
        RealtimeWebSocketProxyMetrics metrics)
    {
        // ASP.NET route matching is case-insensitive. Enforce the public wire
        // contract before any configuration lookup, limiter acquisition, or dial.
        if (!string.Equals(context.Request.Path.Value, Route, StringComparison.Ordinal))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status404NotFound,
                "https://jeeb.dev/errors/not-found",
                "Not Found");
            metrics.Record("path_rejected");
            return;
        }

        context.Response.Headers[RealtimeWebSocketProxyTransformer.MarkerHeader] =
            RealtimeWebSocketProxyTransformer.MarkerValue;

        if (!RealtimeWebSocketProxyTransformer.IsOriginAllowed(context.Request.Headers))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status403Forbidden,
                "https://jeeb.dev/errors/realtime-origin-forbidden",
                "Realtime origin is not allowed.");
            metrics.Record("origin_rejected");
            return;
        }

        if (!destinationResolver.TryResolve(out var destinationPrefix))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "https://jeeb.dev/errors/realtime-proxy-unavailable",
                "Realtime transport is unavailable.");
            metrics.Record("configuration_rejected");
            return;
        }

        using var lease = concurrency.TryAcquire(context);
        if (lease is null)
        {
            context.Response.Headers.RetryAfter = "1";
            await WriteProblemAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                "https://jeeb.dev/errors/realtime-proxy-busy",
                "Realtime transport is busy.");
            metrics.Record("concurrency_rejected");
            return;
        }

        ForwarderError error;
        using (SuppressInstrumentationScope.Begin())
        {
            error = await forwarder.SendAsync(
                context,
                destinationPrefix,
                transport.Invoker,
                transport.RequestConfig,
                transformer,
                context.RequestAborted);
        }

        if (error == ForwarderError.None)
        {
            metrics.Record(context.Response.StatusCode == StatusCodes.Status101SwitchingProtocols
                ? "upgraded"
                : "upstream_rejected");
            return;
        }

        metrics.Record("forward_error");
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        context.Response.Clear();
        context.Response.Headers[RealtimeWebSocketProxyTransformer.MarkerHeader] =
            RealtimeWebSocketProxyTransformer.MarkerValue;
        var status = error is ForwarderError.RequestTimedOut
            or ForwarderError.UpgradeActivityTimeout
            ? StatusCodes.Status504GatewayTimeout
            : StatusCodes.Status502BadGateway;
        await WriteProblemAsync(
            context,
            status,
            "https://jeeb.dev/errors/realtime-proxy-upstream-unavailable",
            "Realtime transport is unavailable.");
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int status,
        string type,
        string title)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new { type, title, status },
            cancellationToken: context.RequestAborted);
    }

    private sealed class DisabledEndpointConventionBuilder : IEndpointConventionBuilder
    {
        public void Add(Action<EndpointBuilder> convention)
        {
        }
    }
}
