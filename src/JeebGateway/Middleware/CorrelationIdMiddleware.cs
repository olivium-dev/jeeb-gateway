namespace JeebGateway.Middleware;

/// <summary>
/// Ensures every request carries a correlation / trace ID.
/// If the caller supplies X-Correlation-Id, it is forwarded; otherwise a new one is minted.
/// The ID is also written to the response headers for downstream tracing.
///
/// <para>GW12-OBS-1 (Leg-12): in addition to echoing the id on the wire, the id is now
/// pushed into an <see cref="ILogger.BeginScope"/> scope that wraps the rest of the
/// pipeline, so EVERY log line emitted while handling the request carries a
/// <c>CorrelationId</c> property. Combined with the OTel log exporter
/// (<c>IncludeScopes = true</c>) this makes the client-quoted X-Correlation-Id grep-able
/// in the log backend — previously it was only ever set on the response header and never
/// written to any log.</para>
/// </summary>
public class CorrelationIdMiddleware
{
    private const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var correlationId)
            || string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("D");
        }

        var id = correlationId.ToString();
        context.Items[ItemKey] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        // Push the correlation id into a log scope so it rides every log line emitted
        // downstream (captured by the OTel log exporter's IncludeScopes). The scope is
        // disposed when the request completes.
        using (_logger.BeginScope(new Dictionary<string, object> { [ItemKey] = id }))
        {
            await _next(context);
        }
    }
}
