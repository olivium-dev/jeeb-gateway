using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Security;
using JeebGateway.Services;

namespace JeebGateway.Controllers;

/// <summary>
/// Additive, READ-ONLY pass-through proxy that exposes one DB-backed read on
/// every remaining upstream so each persistence layer (Mongo / Postgres / Redis)
/// can be exercised END-TO-END THROUGH THE GATEWAY — the product front door —
/// with a minted token. This closes the "DB-tested through the gateway" gap for
/// the services that previously had no GET seam on the gateway, WITHOUT adding a
/// typed-client method + DTO per route (which would have to be kept in lock-step
/// with each upstream's snake_case FastAPI / Phoenix / Actix wire shape).
///
/// Design:
///  - Each action dials a dedicated named <see cref="HttpClient"/> registered in
///    <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>
///    (<c>db-probe-*</c>), which carries the org-standard bearer + X-Service-Auth
///    + Polly resilience chain, so a probe call behaves exactly like every other
///    downstream call.
///  - The upstream JSON body and status code are returned VERBATIM
///    (<see cref="ContentResult"/>), so the gateway never reshapes — and never
///    drifts from — the upstream contract.
///  - When an upstream BaseUrl is not configured for the running environment, or
///    a feature flag gating it is off, the action returns a 503 ProblemDetails
///    instead of dialing an unbound host.
///
/// ADDITIVE ONLY: every route here is net-new and route-distinct from the
/// existing controllers; nothing existing is modified. All routes are
/// <see cref="AuthorizeAttribute"/>-protected (negative path: 401 without a
/// token) and proxy a single DB-backed read (happy path: non-5xx from the
/// upstream's real datastore).
/// </summary>
[ApiController]
[Produces("application/json", "application/problem+json")]
// GW12-SEC-1 (Leg-12): fail-closed environment gate. This diagnostic surface is a
// shadow read-path into every upstream datastore (OWASP-API9) AND a cross-user read
// seam (BOLA on the per-user routes below), so it must NOT be routable in production.
// [DevOnly] short-circuits every action with 404 unless Features:DevEndpoints:Enabled
// is explicitly true (committed false in EVERY environment, incl. appsettings.Production.json
// — same fail-closed pattern as DevController / TestControlPlaneController). The E2E
// harness that exercises these probes already runs with Features__DevEndpoints__Enabled=true,
// so its coverage is unaffected. Ordered before [Authorize] so a disabled surface returns
// 404 (route does not exist) rather than 401 (route exists but needs a token).
[DevOnly]
[Authorize]
// ADR-005 §A public at L2: DB-probe diagnostic reads carry no user-type gate. L1 [Authorize] is
// preserved (token still required); [PublicEndpoint] opts out of L2 + satisfies the coverage guard.
[PublicEndpoint("Authenticated DB-probe diagnostics — ADR-005 §A (no L2 user-type; L1 auth preserved).")]
public sealed class GatewayDbProbeController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly ILogger<GatewayDbProbeController> _log;

    public GatewayDbProbeController(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        ILogger<GatewayDbProbeController> log)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _flags = flags;
        _log = log;
    }

    // ── notification-service (Mongo read) ──────────────────────────────────
    /// <summary>
    /// GET /api/notification/notifications — proxies the notification-service
    /// Mongo-backed list (<c>GET /notifications</c>, host port 10026), forwarding
    /// the <c>receiver</c> / <c>page</c> / <c>page_size</c> query. Additive: the
    /// existing <c>/api/notification/messages</c> stub is left untouched.
    /// </summary>
    [HttpGet("api/notification/notifications")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetNotifications(
        [FromQuery] string? receiver,
        [FromQuery] int? page,
        [FromQuery(Name = "page_size")] int? pageSize,
        CancellationToken ct)
    {
        var q = BuildQuery(
            ("receiver", receiver),
            ("page", page?.ToString()),
            ("page_size", pageSize?.ToString()));
        return ProxyGetAsync(
            "db-probe-notification", "ServiceNotificationClient:BaseUrl",
            $"notifications{q}", ct);
    }

    // ── geolocation-service (PG read) ──────────────────────────────────────
    /// <summary>
    /// GET /locations/user/{userId} — proxies geolocation-service
    /// (<c>GET /locations/user/{user_id}</c>, host port 10060) [PG read].
    /// </summary>
    [HttpGet("locations/user/{userId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetUserLocations(string userId, CancellationToken ct)
        => ProxyGetAsync(
            "db-probe-geolocation", "Services:Geolocation:BaseUrl",
            $"locations/user/{Uri.EscapeDataString(userId)}", ct);

    // ── realtime-comunication-service (PG read) ────────────────────────────
    /// <summary>
    /// GET /realtime/admin/topics — proxies realtime-comunication-service
    /// (<c>GET /admin/topics</c>, host port 10069) [PG read]. Gated behind
    /// <c>FeatureFlags:UseUpstream:Realtime</c>: returns 503 when the flag is off.
    /// </summary>
    [HttpGet("realtime/admin/topics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetRealtimeAdminTopics(CancellationToken ct)
    {
        if (!_flags.CurrentValue.Realtime)
            return Task.FromResult(UpstreamDisabled("realtime-comunication-service"));
        return ProxyGetAsync(
            "db-probe-realtime", "Services:Realtime:BaseUrl",
            "admin/topics", ct);
    }

    // ── compliment-service (PG read) ───────────────────────────────────────
    /// <summary>
    /// GET /api/compliments/list?userId=.. — proxies compliment-service
    /// (<c>GET /api/v1/compliments/list</c>, host port 10036) [PG read]. The
    /// upstream filters by <c>partner_id_1</c>/<c>partner_id_2</c>; the gateway
    /// forwards the supplied user id as <c>partner_id_1</c>.
    /// </summary>
    [HttpGet("api/compliments/list")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetComplimentsList([FromQuery] string? userId, CancellationToken ct)
    {
        var q = BuildQuery(("partner_id_1", userId));
        return ProxyGetAsync(
            "db-probe-compliment", "Services:Compliment:BaseUrl",
            $"api/v1/compliments/list{q}", ct);
    }

    // ── ban-service (Redis read) ───────────────────────────────────────────
    /// <summary>
    /// GET /api/ban/{userId}/status — proxies ban-service
    /// (<c>GET /api/v1/ban/{user_id}/status</c>, host port 10065) [Redis read].
    /// </summary>
    [HttpGet("api/ban/{userId}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetBanStatus(string userId, CancellationToken ct)
        => ProxyGetAsync(
            "db-probe-ban", "Services:Ban:BaseUrl",
            $"api/v1/ban/{Uri.EscapeDataString(userId)}/status", ct);

    // ── one-time-password service (PG read) ────────────────────────────────
    /// <summary>
    /// GET /api/otp/status/{phoneNumber} — proxies the one-time-password service
    /// (<c>GET /api/OTP/status/{phoneNumber}</c>, host port 10037) [PG read].
    /// Route-distinct from the existing send/validate surface on
    /// <see cref="OtpController"/>.
    /// </summary>
    [HttpGet("api/otp/status/{phoneNumber}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetOtpStatus(string phoneNumber, CancellationToken ct)
        => ProxyGetAsync(
            "db-probe-otp", "Services:ServiceOTP:BaseUrl",
            $"api/OTP/status/{Uri.EscapeDataString(phoneNumber)}", ct);

    // ── shared proxy machinery ─────────────────────────────────────────────

    /// <summary>
    /// Dispatches a GET to <paramref name="relativePath"/> on the named client
    /// and returns the upstream status code + body verbatim. Surfaces a 503
    /// ProblemDetails when the upstream BaseUrl is not configured (the named
    /// client has no BaseAddress) or the upstream is unreachable.
    /// </summary>
    private async Task<IActionResult> ProxyGetAsync(
        string clientName, string baseUrlConfigKey, string relativePath, CancellationToken ct)
    {
        var baseUrl = _config[baseUrlConfigKey];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return UpstreamDisabled(clientName);
        }

        var client = _httpClientFactory.CreateClient(clientName);
        try
        {
            using var upstream = await client.GetAsync(relativePath, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await upstream.Content.ReadAsStringAsync(ct);
            var contentType = upstream.Content.Headers.ContentType?.MediaType ?? "application/json";
            return new ContentResult
            {
                StatusCode = (int)upstream.StatusCode,
                ContentType = contentType,
                Content = string.IsNullOrEmpty(body) ? null : body
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "DB-probe upstream {Client} unreachable for {Path}", clientName, relativePath);
            return Problem(
                title: "Upstream unavailable",
                detail: $"The upstream backing '{clientName}' could not be reached.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.4");
        }
    }

    private IActionResult UpstreamDisabled(string upstream) => Problem(
        title: "Upstream not configured",
        detail: $"The upstream backing '{upstream}' is not configured or is disabled in this environment.",
        statusCode: StatusCodes.Status503ServiceUnavailable,
        type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.4");

    private static string BuildQuery(params (string Key, string? Value)[] pairs)
    {
        var parts = new List<string>();
        foreach (var (key, value) in pairs)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
        }
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}
