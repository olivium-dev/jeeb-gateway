using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace JeebGateway.Security;

/// <summary>
/// Authenticates internal service-to-service requests via a shared API key
/// header (T-backend-032). When enabled, requests to paths under
/// <c>/internal/</c> must carry a valid X-Api-Key header matching one of the
/// configured service keys. Public-facing routes are unaffected.
///
/// Uses fixed-time comparison to prevent timing side-channel attacks.
/// </summary>
public class ApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<SecurityOptions> _options;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;

    public ApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        IOptionsMonitor<SecurityOptions> options,
        ILogger<ApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var opts = _options.CurrentValue.ApiKey;
        if (!opts.Enabled)
        {
            await _next(context);
            return;
        }

        if (!IsInternalRoute(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(opts.HeaderName, out var providedKey)
            || string.IsNullOrWhiteSpace(providedKey))
        {
            _logger.LogWarning("Internal route {Path} called without API key", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(
                """{"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,"detail":"A valid API key is required for internal service routes."}""");
            return;
        }

        if (!ValidateKey(providedKey!, opts.ServiceKeys))
        {
            _logger.LogWarning("Internal route {Path} called with invalid API key", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(
                """{"type":"https://httpstatuses.com/403","title":"Forbidden","status":403,"detail":"The provided API key is not valid."}""");
            return;
        }

        await _next(context);
    }

    private static bool IsInternalRoute(PathString path)
    {
        return path.StartsWithSegments("/internal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateKey(string provided, Dictionary<string, string> serviceKeys)
    {
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided);

        foreach (var kvp in serviceKeys)
        {
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(kvp.Value);
            if (CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
                return true;
        }

        return false;
    }
}
