namespace JeebGateway.Services.Cdn;

/// <summary>
/// JEBV4-259 (approach B) — resolves the CLIENT-FACING absolute upload URL for a
/// signed-PUT ticket minted by cdn-service, and owns the 1:1 path mapping between
/// the gateway's streaming-proxy route and cdn-service's signed-PUT endpoint.
///
/// <para>
/// ROOT CAUSE (RCA, JEBV4-259 comment 16561). cdn-service's Local provider mints a
/// RELATIVE, host-less <c>upload_url = /api/ImageUpload/put-signed/{objectRef}?exp=&amp;ct=&amp;sig=</c>
/// (PresignedPutSigner.cs). cdn-service is internal-only
/// (<c>Services:Cdn:BaseUrl = http://192.168.2.50:10072/</c>) and has NO edge route —
/// edge nginx routes ALL <c>/api/*</c> to the gateway. A mobile client that treats
/// that relative URL as absolute joins it to the gateway base and PUTs the bytes
/// back at the gateway edge, which has no such route → the upload wall.
/// </para>
///
/// <para>
/// FIX (approach B — owner decision JDB-18). The gateway rewrites a relative /
/// internal upload_url to an ABSOLUTE gateway route
/// (<c>{gatewayPublicBase}/api/cdn/put-signed/{objectRef}?…</c>) that the client can
/// reach; the gateway then STREAMS those bytes to cdn-service internally
/// (<see cref="JeebGateway.Controllers.CdnUploadProxyController"/>). An upload_url
/// that is ALREADY a publicly reachable absolute URL (e.g. a future S3 / approach-A
/// pre-signed URL) is passed through untouched so the bytes still bypass the gateway.
/// This class is a pure function (no I/O, no ASP.NET types) so the rewrite is unit
/// tested directly.
/// </para>
/// </summary>
public static class CdnUploadUrlResolver
{
    /// <summary>
    /// cdn-service's signed-PUT path prefix (PresignedPutSigner.cs). The gateway
    /// proxy route (<see cref="GatewayProxyPathPrefix"/>) mirrors it 1:1, so a
    /// minted objectRef survives the round-trip unchanged.
    /// </summary>
    public const string CdnPutSignedPathPrefix = "api/ImageUpload/put-signed/";

    /// <summary>
    /// The gateway streaming-proxy path prefix the client PUTs to. Kept as a
    /// segment-for-segment mirror of <see cref="CdnPutSignedPathPrefix"/> so the
    /// forward (mint-time rewrite) and reverse (PUT-time reconstruction) mappings
    /// stay symmetric.
    /// </summary>
    public const string GatewayProxyPathPrefix = "api/cdn/put-signed/";

    /// <summary>
    /// The name of the dedicated, resilience-free <see cref="System.Net.Http.HttpClient"/>
    /// the streaming proxy dials cdn-service with. Deliberately carries NO retry
    /// handler (a retried request cannot rewind the client's upload body stream)
    /// and NO bearer / X-Service-Auth (the signed-PUT URL is bearer-free).
    /// </summary>
    public const string ProxyHttpClientName = "cdn-proxy";

    /// <summary>
    /// Resolve the absolute URL the mobile client should PUT the bytes to.
    /// </summary>
    /// <param name="cdnUploadUrl">The <c>upload_url</c> minted by cdn-service (relative or absolute).</param>
    /// <param name="cdnInternalBase">The configured internal cdn-service base (<c>Services:Cdn:BaseUrl</c>), used to detect an absolute-but-internal (unreachable) URL. May be null.</param>
    /// <param name="gatewayPublicBase">The gateway's public origin as the client sees it, e.g. <c>https://jeeb.fds-1.com</c> (no trailing slash required).</param>
    /// <returns>An absolute URL the client can reach: cdn's own URL when already public, else the gateway streaming-proxy route.</returns>
    /// <exception cref="InvalidOperationException">cdn returned an empty upload_url, or a relative/internal URL that is not the recognised signed-PUT path (refuse to mint an unreachable URL).</exception>
    public static string Resolve(string? cdnUploadUrl, Uri? cdnInternalBase, string gatewayPublicBase)
    {
        if (string.IsNullOrWhiteSpace(cdnUploadUrl))
        {
            throw new InvalidOperationException("cdn-service returned an empty upload_url.");
        }

        // IMPORTANT: Uri.TryCreate(relative, UriKind.Absolute) returns TRUE for a
        // leading-slash path on Unix — it parses "/api/..." as file:///api/... So a
        // URL only counts as a publicly reachable absolute upload target if it is
        // ALSO http/https with a real host. Otherwise (file scheme, empty host, or a
        // genuinely relative URL) it is the Local-provider case → proxy it.
        if (Uri.TryCreate(cdnUploadUrl, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(abs.Host))
        {
            var isInternal = cdnInternalBase is not null
                && string.Equals(abs.Host, cdnInternalBase.Host, StringComparison.OrdinalIgnoreCase)
                && abs.Port == cdnInternalBase.Port;

            // A genuinely public absolute URL is reachable by the client directly:
            // pass it through so the bytes still bypass the gateway (approach A / S3).
            if (!isInternal)
            {
                return cdnUploadUrl;
            }

            // Absolute but pointing at the internal-only cdn host → unreachable by
            // the client; proxy its path+query through the gateway.
            return RewriteToGatewayProxy(abs.PathAndQuery, gatewayPublicBase);
        }

        // Relative / host-less (the current Local-provider case) → proxy.
        return RewriteToGatewayProxy(cdnUploadUrl, gatewayPublicBase);
    }

    /// <summary>
    /// Reverse mapping used by the streaming proxy: turn the gateway route's
    /// captured <c>{**objectPath}</c> back into cdn-service's signed-PUT relative
    /// path (WITHOUT query — the caller appends the verbatim inbound query string
    /// so the HMAC signature cdn signed over <c>exp/ct/sig</c> validates unchanged).
    /// </summary>
    public static string ToCdnPutSignedPath(string objectPath) =>
        CdnPutSignedPathPrefix + objectPath;

    /// <summary>
    /// SSRF / path-traversal fail-closed check on the CANONICALIZED upstream target
    /// (CWE-22 / CWE-918). The streaming proxy builds its cdn target with
    /// <c>new Uri(cdnBase, "api/ImageUpload/put-signed/{objectPath}{?query}")</c>;
    /// <see cref="System.Uri"/> percent-decodes (<c>%2e</c> → <c>.</c>) and collapses
    /// dot-segments at that point, so a double-encoded traversal
    /// (<c>%252e%252e</c> → Kestrel single-decodes to <c>%2e%2e</c> → Uri decodes+collapses
    /// to <c>..</c>) can escape the fixed prefix onto an arbitrary cdn path — bypassing
    /// a guard that only inspected the pre-decode route value. This validates the URI
    /// that will ACTUALLY be dialed: it must share the cdn base's scheme, host and port
    /// (no redirected authority) AND its path must stay under the signed-PUT prefix.
    /// Pure function (no I/O, no ASP.NET types) so the exact canonicalization is unit
    /// tested directly, mirroring <see cref="Resolve"/>.
    /// </summary>
    /// <param name="upstreamUri">The fully-built target, <c>new Uri(cdnBase, relative)</c>.</param>
    /// <param name="cdnBase">The cdn-proxy client's configured <c>BaseAddress</c>.</param>
    /// <returns><c>true</c> only when the target stays on cdn's own origin and under the signed-PUT prefix.</returns>
    public static bool IsOnSignedPutPrefix(Uri upstreamUri, Uri cdnBase) =>
        string.Equals(upstreamUri.Scheme, cdnBase.Scheme, StringComparison.Ordinal)
        && string.Equals(upstreamUri.Host, cdnBase.Host, StringComparison.OrdinalIgnoreCase)
        && upstreamUri.Port == cdnBase.Port
        // Leading '/' anchors the check to cdn's own path prefix (AbsolutePath is always
        // rooted). Derived from CdnPutSignedPathPrefix so the guard can never drift from
        // the path the proxy actually mirrors.
        && upstreamUri.AbsolutePath.StartsWith("/" + CdnPutSignedPathPrefix, StringComparison.Ordinal);

    private static string RewriteToGatewayProxy(string cdnPathAndQuery, string gatewayPublicBase)
    {
        var trimmed = cdnPathAndQuery.TrimStart('/');
        var queryIndex = trimmed.IndexOf('?');
        var path = queryIndex >= 0 ? trimmed[..queryIndex] : trimmed;
        var query = queryIndex >= 0 ? trimmed[queryIndex..] : string.Empty; // keeps leading '?'

        var prefixIndex = path.IndexOf(CdnPutSignedPathPrefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex < 0)
        {
            // Not the known signed-PUT path — refuse to mint an unreachable URL.
            // Fail LOUD (502 at the broker) rather than hand the client something
            // that silently fails the same way the RCA describes.
            throw new InvalidOperationException(
                $"cdn-service returned an upload_url ('{cdnPathAndQuery}') that is neither publicly " +
                $"reachable nor the recognised signed-PUT path ('{CdnPutSignedPathPrefix}'); refusing " +
                "to mint an unreachable upload URL.");
        }

        // Everything after the cdn prefix is the objectRef (may itself contain '/').
        var objectTail = path[(prefixIndex + CdnPutSignedPathPrefix.Length)..];
        return $"{gatewayPublicBase.TrimEnd('/')}/{GatewayProxyPathPrefix}{objectTail}{query}";
    }
}
