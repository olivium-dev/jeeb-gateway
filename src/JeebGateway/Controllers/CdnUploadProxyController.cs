using JeebGateway.Services.Cdn;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// JEBV4-259 (approach B, owner decision JDB-18) — the KYC-photo streaming upload
/// proxy. The mobile client PUTs the raw image bytes here (to the absolute
/// <c>upload_url</c> the broker minted, <see cref="CdnController.BrokerUploadUrl"/>)
/// and the gateway STREAMS them to cdn-service's internal signed-PUT endpoint,
/// then relays cdn's status + body back. This exists because cdn-service is
/// internal-only (no edge route) and its Local provider mints a relative,
/// host-less upload URL the client cannot reach directly (RCA, comment 16561).
///
/// <para><b>GR-1 (gateway tiny) discipline.</b> This is a DUMB PIPE: no business
/// logic, no auth decision, no durable state. The body is streamed
/// (<see cref="System.Net.Http.StreamContent"/> over <c>Request.Body</c>) — a KYC
/// photo is never buffered whole in gateway memory — and capped at
/// <see cref="MaxUploadBytes"/> as defence-in-depth. cdn-service remains the
/// record-of-truth: it validates the HMAC signature (over <c>exp/ct/sig</c>) and
/// owns accept/reject. The gateway forwards the inbound query string verbatim so
/// that signature validates unchanged.</para>
///
/// <para><b>Auth.</b> The signed-PUT URL is deliberately bearer-free — the HMAC
/// signature in the query IS the authorization (standard pre-signed-PUT model).
/// The route is therefore <see cref="AllowAnonymousAttribute"/> so it opts out of
/// the gateway's global <c>RequireAuthenticatedUser</c> fallback policy; a request
/// without a valid signature is rejected by cdn-service, not the gateway.</para>
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/cdn")]
public sealed class CdnUploadProxyController : ControllerBase
{
    /// <summary>
    /// Defence-in-depth cap on a proxied KYC upload (GR-1: keep the gateway tiny;
    /// cdn-service enforces the authoritative size/type policy). 15 MB comfortably
    /// covers an ID photo / scanned document; anything larger is rejected with 413
    /// before any bytes reach cdn.
    /// </summary>
    public const long MaxUploadBytes = 15L * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CdnUploadProxyController> _logger;

    public CdnUploadProxyController(
        IHttpClientFactory httpClientFactory,
        ILogger<CdnUploadProxyController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Stream a signed KYC-photo PUT through to cdn-service. <paramref name="objectPath"/>
    /// is the <c>{objectRef}</c> tail of cdn's signed-PUT path; the inbound query
    /// string (<c>exp/ct/sig</c>) is forwarded verbatim so the signature validates.
    /// </summary>
    [HttpPut("put-signed/{**objectPath}")]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> PutSigned(string objectPath, CancellationToken ct)
    {
        // SSRF guard: objectPath is appended to cdn's FIXED signed-PUT prefix; a
        // "../" segment could otherwise redirect the proxied PUT at a different,
        // unsigned cdn endpoint. Reject traversal (and an empty ref) up front.
        if (string.IsNullOrWhiteSpace(objectPath)
            || objectPath.Contains("..", StringComparison.Ordinal))
        {
            return Problem(
                title: "Invalid upload path",
                detail: "The upload object reference is missing or malformed.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var client = _httpClientFactory.CreateClient(CdnUploadUrlResolver.ProxyHttpClientName);
        if (client.BaseAddress is null)
        {
            // cdn base unconfigured (placeholder host) — never dial an unroutable host.
            _logger.LogError("KYC upload proxy: cdn-service base address is not configured.");
            return Problem(
                title: "CDN upstream not configured",
                detail: "The asset store upload endpoint is not configured in this environment.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // Rebuild cdn's signed-PUT target: {cdnBase}api/ImageUpload/put-signed/{objectPath}{?exp&ct&sig}.
        // The query string is forwarded verbatim so the HMAC signature validates.
        var relative = CdnUploadUrlResolver.ToCdnPutSignedPath(objectPath) + Request.QueryString.Value;
        var upstreamUri = new Uri(client.BaseAddress, relative);

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Put, upstreamUri)
        {
            // STREAM the client body straight through — no MemoryStream, no byte[] of
            // the whole photo (GR-1: gateway holds no upload buffer).
            Content = new StreamContent(Request.Body),
        };

        // Forward the client's Content-Type (cdn signs over the ?ct query param and
        // does not validate the header, but forwarding the real media type is
        // correct and honours the required_headers contract the broker advertises).
        var contentType = string.IsNullOrWhiteSpace(Request.ContentType)
            ? "application/octet-stream"
            : Request.ContentType;
        upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        // Forward a known Content-Length when present; otherwise the request is
        // chunk-streamed. (Never trust it as a size gate — RequestSizeLimit does that.)
        if (Request.ContentLength is > 0)
        {
            upstreamRequest.Content.Headers.ContentLength = Request.ContentLength;
        }

        HttpResponseMessage upstreamResponse;
        try
        {
            // ResponseHeadersRead so cdn's response body is streamed back, not buffered.
            upstreamResponse = await client.SendAsync(
                upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex,
                "KYC upload proxy: streaming PUT to cdn-service failed for objectRef {ObjectPath}.",
                objectPath);
            return Problem(
                title: "CDN upstream unavailable",
                detail: "The asset store could not be reached to store the upload.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // Relay cdn's status + body verbatim (streamed). Dumb pipe: whatever cdn
        // decides (2xx accept, 4xx bad-signature/expired) is what the client sees.
        using (upstreamResponse)
        {
            Response.StatusCode = (int)upstreamResponse.StatusCode;

            var responseContentType = upstreamResponse.Content.Headers.ContentType?.ToString();
            if (!string.IsNullOrWhiteSpace(responseContentType))
            {
                Response.ContentType = responseContentType;
            }

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(Response.Body, ct);
        }

        return new EmptyResult();
    }
}
