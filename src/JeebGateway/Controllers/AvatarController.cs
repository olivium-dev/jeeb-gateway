using System.Net;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Services;
using JeebGateway.Services.Cdn;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UmServiceClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmApiException = JeebGateway.service.ServiceUserManagement.ApiException;

namespace JeebGateway.Controllers;

/// <summary>
/// F5 — the PUBLIC, server-resolved avatar read route.
///
/// <c>GET /api/users/{userId}/avatar</c> is genuinely unauthenticated
/// (<c>[AllowAnonymous]</c> + <c>[PublicEndpoint]</c>, ADR-005 §Layer2 opt-out) — a
/// deliberate posture change requiring OWNER SIGN-OFF (see the PR description).
/// It exists because every avatar render site on mobile
/// (<c>JeebAvatar</c>/<c>OmdsProfileAvatar</c> → bare <c>CachedNetworkImage</c>) issues an
/// UNAUTHENTICATED GET with no bearer passthrough — the only working CDN read path
/// today, <see cref="CdnController.GetAssetContent"/>, requires the VIEWER's own
/// bearer, which cannot be satisfied for a counterparty's avatar (or even the
/// user's own home-greeting avatar, which is rendered the same unauthenticated
/// way).
///
/// <para><b>Exposure surface (kept deliberately narrow — read the PR body for the
/// full sign-off writeup):</b></para>
/// <list type="bullet">
///   <item><description>Keyed by <c>userId</c> only — a "public-anyway" identifier
///     the app already surfaces to any offer/chat/delivery counterparty. No PII
///     (phone/email) ever appears in the URL.</description></item>
///   <item><description>The object reference streamed is resolved ENTIRELY
///     server-side from the UM profile's own <c>ProfilePic</c> field — the caller
///     supplies nothing that becomes a storage path. This is NOT
///     <see cref="CdnController.GetAssetContent"/> reopened to anonymous callers:
///     that route accepts an arbitrary client-supplied <c>objectPath</c> (safe only
///     because it is bearer-gated); this route accepts no path input at all, so
///     there is no way to enumerate another user's KYC/dispute/support-evidence
///     assets through it.</description></item>
///   <item><description>No directory listing — a single object per userId, 404 on
///     anything else.</description></item>
///   <item><description><c>Cache-Control: public, max-age</c> is set on every 200 so
///     repeat renders of the same avatar (every offer card / chat bubble / delivery
///     card that shows the same counterparty) are intermediary-cacheable rather than
///     re-hitting this route and cdn-service per render.</description></item>
/// </list>
///
/// <para><b>Resolution source (F5 validator correction 5).</b> Reads the UM profile
/// DIRECTLY (<c>_umProfile.ProfileAsync</c>, the same call
/// <c>Availability.OfferJeeberEnricher.ResolveCanonicalProfileAsync</c> already
/// makes) — NOT the gateway's local <c>IUsersStore</c> projection, which is
/// mirror-write-only, only ever hydrated on a <c>/me</c> read, and has no clear
/// semantics, so reading it here would serve stale or never-set avatars.</para>
/// </summary>
[ApiController]
[AllowAnonymous]
[PublicEndpoint(
    "F5 public avatar read — resolves a server-chosen CDN object from the UM profile "
    + "keyed by userId; accepts no client-supplied path, so it cannot serve arbitrary "
    + "CDN objects. OWNER SIGN-OFF required before this route ships to production.")]
public sealed class AvatarController : ControllerBase
{
    // Moderate, not long: an avatar can change (re-upload/remove) and this route has
    // no version-token query param of its own — mobile busts intermediary caches by
    // appending its own ?v=<epoch> to the URL it stores (F5 mobile scope, not this
    // route). Kept well under CdnController's MaxSignedUrlTtlSeconds (3600) bound.
    private const int AvatarCacheMaxAgeSeconds = 600;

    private readonly UmServiceClient _umProfile;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AvatarController> _logger;

    public AvatarController(
        UmServiceClient umProfile,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        IHttpClientFactory httpClientFactory,
        ILogger<AvatarController> logger)
    {
        _umProfile = umProfile;
        _flags = flags;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/users/{userId}/avatar — 200 + streamed image bytes when the user has
    /// a <c>profile_avatar</c>-slot object on record, 404 otherwise (mobile's
    /// <c>OmdsCachedImage.errorWidget</c> already renders the initials fallback on
    /// any non-200, so a 404 here is a normal, expected outcome — never an error to
    /// escalate).
    /// </summary>
    [HttpGet("api/users/{userId}/avatar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAvatar(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return NoAvatar();
        }

        if (!_flags.CurrentValue.UserManagement || !_flags.CurrentValue.Cdn)
        {
            return Problem(
                title: "Avatar upstream disabled",
                detail: "This environment does not have user-management and/or cdn-service enabled.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var objectPath = await ResolveAvatarObjectRefAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(objectPath))
        {
            return NoAvatar();
        }

        // Same SSRF/traversal fail-closed shape as CdnController.GetAssetContent
        // (CWE-22/918). objectPath here comes from server-trusted storage (the UM
        // profile), not the caller, so a hit is a server-side data problem, not a
        // caller error — never explain why, just 404 like any other miss.
        if (objectPath.Contains("..", StringComparison.Ordinal)
            || objectPath.Contains('%')
            || objectPath.Contains('\\'))
        {
            _logger.LogError(
                "Avatar route: stored ProfilePic for {UserId} failed the traversal guard; refusing to dial cdn.",
                userId);
            return NoAvatar();
        }

        var client = _httpClientFactory.CreateClient(CdnUploadUrlResolver.ProxyHttpClientName);
        if (client.BaseAddress is null)
        {
            _logger.LogError("Avatar route: cdn-service base address is not configured.");
            return Problem(
                title: "CDN upstream not configured",
                detail: "The asset store fetch endpoint is not configured in this environment.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var upstreamUri = new Uri(
            client.BaseAddress,
            CdnUploadUrlResolver.CdnFetchPathPrefix + Uri.EscapeDataString(objectPath));

        if (!CdnUploadUrlResolver.IsOnFetchPrefix(upstreamUri, client.BaseAddress))
        {
            _logger.LogError(
                "Avatar route: resolved target for {UserId} escaped cdn's fetch prefix (path {ResolvedPath}).",
                userId, upstreamUri.AbsolutePath);
            return NoAvatar();
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.GetAsync(upstreamUri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Avatar route: fetch from cdn-service failed.");
            return Problem(
                title: "CDN upstream unavailable",
                detail: "The asset store could not be reached to serve the requested avatar.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        HttpContext.Response.RegisterForDispose(upstream);

        if (upstream.StatusCode == HttpStatusCode.NotFound)
        {
            return NoAvatar();
        }

        if (!upstream.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Avatar route: cdn-service returned {Status} for {UserId}'s avatar fetch.",
                (int)upstream.StatusCode, userId);
            return Problem(
                title: "Avatar fetch failed",
                detail: "The asset store could not serve the requested avatar.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        Response.Headers.CacheControl = $"public, max-age={AvatarCacheMaxAgeSeconds}";
        if (upstream.Headers.ETag is not null)
        {
            Response.Headers.ETag = upstream.Headers.ETag.ToString();
        }
        if (upstream.Content.Headers.LastModified is not null)
        {
            Response.Headers.LastModified = upstream.Content.Headers.LastModified.Value.ToString("R");
        }

        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var stream = await upstream.Content.ReadAsStreamAsync(ct);
        return File(stream, contentType);
    }

    /// <summary>
    /// Server-side-only resolution: the caller supplies nothing that becomes a
    /// storage path — only <c>userId</c>, and that is used solely to key the UM
    /// profile lookup. A UM miss/error is indistinguishable from "no avatar set"
    /// to the caller (both 404), so this route never confirms/denies account
    /// existence beyond what the app already exposes elsewhere.
    /// </summary>
    private async Task<string?> ResolveAvatarObjectRefAsync(string userId, CancellationToken ct)
    {
        try
        {
            var profile = await _umProfile.ProfileAsync(userId, ct);
            return string.IsNullOrWhiteSpace(profile?.ProfilePic) ? null : profile.ProfilePic.Trim();
        }
        catch (UmApiException ex)
        {
            _logger.LogInformation(
                "Avatar route: UM profile read for {UserId} returned {Status}; serving 404.",
                userId, ex.StatusCode);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Avatar route: UM profile read for {UserId} errored; serving 404.", userId);
            return null;
        }
    }

    private ObjectResult NoAvatar() => Problem(
        title: "Avatar not found",
        detail: "No avatar is set for this user.",
        statusCode: StatusCodes.Status404NotFound);
}
