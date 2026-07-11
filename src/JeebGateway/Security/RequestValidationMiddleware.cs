using Microsoft.Extensions.Options;

namespace JeebGateway.Security;

/// <summary>
/// Validates inbound requests against configurable size and content-type
/// constraints before they reach controllers (T-backend-032). Rejects with
/// 413 (body too large), 414 (URI too long), or 415 (unsupported media type)
/// as RFC 7231 Problem+JSON responses.
/// </summary>
public class RequestValidationMiddleware
{
    /// <summary>
    /// JEBV4-259 — the KYC-photo streaming upload proxy
    /// (<see cref="JeebGateway.Controllers.CdnUploadProxyController"/>, route
    /// <c>PUT /api/cdn/put-signed/{**objectPath}</c>) legitimately receives raw
    /// image/pdf bytes that are (a) not in this middleware's JSON-oriented
    /// content-type allowlist and (b) larger than the 1 MB body cap tuned for the
    /// JSON API surface. That endpoint enforces its OWN 15 MB
    /// <c>[RequestSizeLimit]</c> and streams straight to cdn-service (which
    /// validates the signed-URL HMAC), so it is exempt from the content-type +
    /// body-size checks here. URL-length and header-size checks still apply.
    /// Before this exemption the KYC PUT was rejected with the exact 415
    /// "request content type is not supported" wall the JEBV4-259 RCA describes.
    /// </summary>
    private static readonly PathString BinaryUploadProxyPathPrefix = new("/api/cdn/put-signed");

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<SecurityOptions> _options;

    public RequestValidationMiddleware(RequestDelegate next, IOptionsMonitor<SecurityOptions> options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var opts = _options.CurrentValue.RequestValidation;
        if (!opts.Enabled)
        {
            await _next(context);
            return;
        }

        var request = context.Request;

        var fullUrlLength = (request.Path.Value?.Length ?? 0) + (request.QueryString.Value?.Length ?? 0);
        if (fullUrlLength > opts.MaxUrlLength)
        {
            context.Response.StatusCode = StatusCodes.Status414UriTooLong;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(
                """{"type":"https://httpstatuses.com/414","title":"URI Too Long","status":414,"detail":"The request URI exceeds the maximum allowed length."}""");
            return;
        }

        foreach (var header in request.Headers)
        {
            if (header.Value.Any(v => v != null && v.Length > opts.MaxHeaderValueLength))
            {
                context.Response.StatusCode = StatusCodes.Status431RequestHeaderFieldsTooLarge;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(
                    """{"type":"https://httpstatuses.com/431","title":"Request Header Fields Too Large","status":431,"detail":"A request header value exceeds the maximum allowed length."}""");
                return;
            }
        }

        // JEBV4-259: the dedicated binary upload proxy self-limits (15 MB) and
        // streams image/pdf bytes to cdn-service — exempt it from the JSON-oriented
        // content-type allowlist + 1 MB body cap below (see field doc).
        var isBinaryUploadProxy = request.Path.StartsWithSegments(
            BinaryUploadProxyPathPrefix, StringComparison.OrdinalIgnoreCase);

        if (HasBody(request) && !isBinaryUploadProxy)
        {
            if (request.ContentLength > opts.MaxBodySizeBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(
                    """{"type":"https://httpstatuses.com/413","title":"Payload Too Large","status":413,"detail":"The request body exceeds the maximum allowed size."}""");
                return;
            }

            if (request.ContentType is not null && !IsAllowedContentType(request.ContentType, opts.AllowedContentTypes))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(
                    """{"type":"https://httpstatuses.com/415","title":"Unsupported Media Type","status":415,"detail":"The request content type is not supported."}""");
                return;
            }
        }

        await _next(context);
    }

    private static bool HasBody(HttpRequest request)
    {
        return request.ContentLength > 0
            || request.ContentType is not null
            || string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Method, "PUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Method, "PATCH", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedContentType(string contentType, string[] allowed)
    {
        foreach (var a in allowed)
        {
            if (contentType.StartsWith(a, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
