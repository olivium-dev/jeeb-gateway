using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Yarp.ReverseProxy.Forwarder;

namespace JeebGateway.Realtime.Proxy;

/// <summary>
/// Minimal transform for the Phoenix upgrade. The query is copied byte-for-byte
/// into the fixed upstream path, while all ambient gateway credentials and
/// spoofable internal identity headers are discarded.
/// </summary>
internal sealed class RealtimeWebSocketProxyTransformer : HttpTransformer
{
    internal const string MarkerHeader = "X-Jeeb-Realtime-Proxy";
    internal const string MarkerValue = "gateway";
    internal const string FixedPath = "/socket/websocket";
    internal const string ExactBrowserOrigin = "https://app.jeeb.fds-1.com";

    private static readonly HashSet<string> SafeRequestHeaders = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Encoding",
        "Accept-Language",
        "Cache-Control",
        "Connection",
        "Origin",
        "Pragma",
        "Sec-WebSocket-Extensions",
        "Sec-WebSocket-Key",
        "Sec-WebSocket-Protocol",
        "Sec-WebSocket-Version",
        "Upgrade",
        "User-Agent",
    };

    public static bool IsOriginAllowed(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue("Origin", out var origin))
        {
            return true;
        }

        return origin.Count == 1
            && string.Equals(origin[0], ExactBrowserOrigin, StringComparison.Ordinal);
    }

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(
            httpContext,
            proxyRequest,
            destinationPrefix,
            cancellationToken);

        proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(
            destinationPrefix,
            new PathString(FixedPath),
            httpContext.Request.QueryString);

        RemoveUnsafeHeaders(proxyRequest.Headers);
        if (proxyRequest.Content is not null)
        {
            RemoveUnsafeHeaders(proxyRequest.Content.Headers);
        }

        proxyRequest.Headers.Host = null;
        proxyRequest.Headers.Remove(MarkerHeader);
        proxyRequest.Headers.TryAddWithoutValidation(MarkerHeader, MarkerValue);
    }

    public override async ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext,
        HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken)
    {
        if (proxyResponse is null)
        {
            return false;
        }

        await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
        SanitizeResponseHeaders(httpContext.Response.Headers);
        httpContext.Response.Headers[MarkerHeader] = MarkerValue;

        if (proxyResponse.StatusCode == HttpStatusCode.SwitchingProtocols)
        {
            return true;
        }

        // Never reflect an upstream error body: Phoenix/auth failures can contain
        // credential diagnostics. Keep the authoritative status (notably 401/403)
        // and replace the body with a stable RFC 7807 envelope.
        httpContext.Response.Headers.ContentLength = null;
        httpContext.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            new
            {
                type = "https://jeeb.dev/errors/realtime-connection-rejected",
                title = "Realtime connection was not accepted.",
                status = (int)proxyResponse.StatusCode,
            },
            cancellationToken: cancellationToken);
        return false;
    }

    private static void RemoveUnsafeHeaders(HttpHeaders headers)
    {
        foreach (var header in headers.ToArray())
        {
            if (!SafeRequestHeaders.Contains(header.Key))
            {
                headers.Remove(header.Key);
            }
        }
    }

    private static void SanitizeResponseHeaders(IHeaderDictionary headers)
    {
        foreach (var header in headers.Keys.ToArray())
        {
            if (header.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                || header.Equals("Server", StringComparison.OrdinalIgnoreCase)
                || header.Equals("X-Powered-By", StringComparison.OrdinalIgnoreCase)
                || header.Equals(MarkerHeader, StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("X-Internal-", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("X-Service-", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("X-User-", StringComparison.OrdinalIgnoreCase))
            {
                headers.Remove(header);
            }
        }
    }
}
