namespace JeebGateway.Middleware;

/// <summary>
/// Ensures every request carries a correlation / trace ID.
/// If the caller supplies X-Correlation-Id, it is forwarded; otherwise a new one is minted.
/// The ID is also written to the response headers for downstream tracing.
/// </summary>
public class CorrelationIdMiddleware
{
    private const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var correlationId)
            || string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("D");
        }

        context.Items["CorrelationId"] = correlationId.ToString();
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId.ToString();
            return Task.CompletedTask;
        });

        await _next(context);
    }
}
