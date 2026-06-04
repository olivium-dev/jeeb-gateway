using System.Text;

namespace JeebGateway.StateService.Idempotency;

/// <summary>
/// Gateway-wide <c>Idempotency-Key</c> handler (R1 / JEB-1493). For any
/// mutating request (POST/PUT/PATCH/DELETE) that carries an
/// <c>Idempotency-Key</c> header, the first execution's response is captured
/// and persisted to jeeb-state-service; any replay with the same key returns
/// the ORIGINAL response verbatim without re-invoking the endpoint. This makes
/// a double-tapped <c>POST /requests</c> create exactly one order.
///
/// Durability is owned by jeeb-state-service (<c>ON CONFLICT (key)</c>), so the
/// guarantee survives a stop-first gateway bounce. The gateway holds no state.
/// </summary>
public sealed class IdempotencyMiddleware
{
    private const string HeaderName = "Idempotency-Key";
    private const int DefaultTtlSeconds = 24 * 60 * 60; // 24h dedup window
    private const int MaxKeyLength = 200;

    private static readonly HashSet<string> MutatingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store)
    {
        if (!ShouldApply(context, out var key))
        {
            await _next(context);
            return;
        }

        // 1. Fast replay path: if we've already stored a response, return it.
        IdempotencyOutcome? existing;
        try
        {
            existing = await store.GetAsync(key!, context.RequestAborted);
        }
        catch (Exception ex)
        {
            // State-service unreachable / breaker open → fail open (do not
            // block the request). Degraded, not down. (ADR-001-rev2.)
            _logger.LogWarning(ex, "Idempotency lookup failed for key {Key}; proceeding without dedup", key);
            await _next(context);
            return;
        }

        if (existing is not null)
        {
            await ReplayAsync(context, existing);
            return;
        }

        // 2. First execution: buffer the response so we can persist it.
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var bodyBytes = buffer.ToArray();

        // Only persist successful, deterministic responses (2xx). Errors are
        // not deduped — the caller should be able to retry a failed attempt.
        if (context.Response.StatusCode is >= 200 and < 300)
        {
            var bodyJson = Encoding.UTF8.GetString(bodyBytes);
            try
            {
                var outcome = await store.PutOrGetAsync(
                    key!, context.Response.StatusCode, bodyJson, DefaultTtlSeconds, context.RequestAborted);

                // Lost the race: another concurrent caller stored first. Replay
                // THAT original so both callers observe the same single effect.
                if (!outcome.Inserted)
                {
                    _logger.LogInformation("Idempotency race on key {Key}; replaying stored original", key);
                    await OverwriteWithStoredAsync(context, originalBody, outcome);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Idempotency persist failed for key {Key}; returning live response", key);
            }
        }

        // Flush the (newly produced) response body to the real stream.
        await originalBody.WriteAsync(bodyBytes, context.RequestAborted);
    }

    private static bool ShouldApply(HttpContext context, out string? key)
    {
        key = null;
        if (!MutatingMethods.Contains(context.Request.Method)) return false;
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values)) return false;

        var candidate = values.ToString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxKeyLength) return false;

        // Scope the key by method+path so the same client key on two different
        // endpoints cannot collide — but the persisted key must be SLASH-FREE
        // because the state-service exposes GET /idempotency/{key} and a raw
        // request path ("/prohibited-items/scan") would break path routing.
        // We therefore hash the {method}:{path} scope into a compact, URL-safe
        // prefix and append the client-supplied key.
        var scope = ScopeHash($"{context.Request.Method}:{context.Request.Path}");
        key = $"{scope}.{candidate}";
        return true;
    }

    private static string ScopeHash(string scope)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(scope);
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(bytes, hash);
        // base64url, no padding/slashes — first 16 chars are plenty to scope.
        return Convert.ToBase64String(hash)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')[..16];
    }

    private static async Task ReplayAsync(HttpContext context, IdempotencyOutcome outcome)
    {
        context.Response.StatusCode = outcome.StatusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers["Idempotency-Replayed"] = "true";
        await context.Response.WriteAsync(outcome.ResponseBodyJson, context.RequestAborted);
    }

    private static async Task OverwriteWithStoredAsync(
        HttpContext context, Stream realBody, IdempotencyOutcome outcome)
    {
        context.Response.Body = realBody;
        context.Response.StatusCode = outcome.StatusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers["Idempotency-Replayed"] = "true";
        await context.Response.WriteAsync(outcome.ResponseBodyJson, context.RequestAborted);
    }
}
