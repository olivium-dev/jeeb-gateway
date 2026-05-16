using System.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace JeebGateway.Middleware;

/// <summary>
/// T-backend-050 — records each request's wall-clock duration into the
/// per-endpoint histogram on <see cref="RequestLatencyMetrics"/>.
///
/// Runs as late as possible in the pipeline so it sees the matched route
/// template (populated by <c>UseRouting</c>) and the final response status.
/// The <c>route</c> tag falls back to <c>"unmatched"</c> rather than the raw
/// path so unauthenticated probes cannot inflate metric cardinality.
/// </summary>
public sealed class RequestLatencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestLatencyMetrics _metrics;

    public RequestLatencyMiddleware(RequestDelegate next, RequestLatencyMetrics metrics)
    {
        _next = next;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await _next(context);
        }
        finally
        {
            var elapsedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            var route = ResolveRoute(context);
            var statusCode = context.Response.StatusCode;
            var statusClass = $"{statusCode / 100}xx";

            _metrics.RequestDuration.Record(
                elapsedSeconds,
                new KeyValuePair<string, object?>("method", context.Request.Method),
                new KeyValuePair<string, object?>("route", route),
                new KeyValuePair<string, object?>("status_class", statusClass),
                new KeyValuePair<string, object?>("status_code", statusCode));
        }
    }

    private static string ResolveRoute(HttpContext context)
    {
        var endpoint = context.GetEndpoint() as RouteEndpoint;
        var template = endpoint?.RoutePattern.RawText;
        if (!string.IsNullOrWhiteSpace(template))
        {
            return template!.StartsWith('/') ? template : "/" + template;
        }

        return "unmatched";
    }
}
