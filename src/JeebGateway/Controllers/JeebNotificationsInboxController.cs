using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.JeebNotifications;
using JeebGateway.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using JeebGateway.service.ServiceNotification;
using NotificationApiException = JeebGateway.service.ServiceNotification.ApiException;

namespace JeebGateway.Controllers;

/// <summary>
/// The Jeeb notifications INBOX surface the mobile app consumes
/// (<c>DioNotificationsRepository</c>, JM-057), filling out the mobile-facing path
/// shapes the existing notification controllers do not expose:
///
/// <list type="bullet">
///   <item><c>GET   /v1/notifications?userId=&amp;page=&amp;pageSize=</c> — the user's inbox page.</item>
///   <item><c>PATCH /v1/notifications/{id}/read</c> — mark one notification read.</item>
/// </list>
///
/// <para>
/// This is a SIBLING controller — it does NOT touch the existing
/// <see cref="JeebNotificationsController"/> (<c>POST /api/notifications</c> template
/// render→dispatch), <see cref="NotificationController"/>
/// (<c>api/notification/messages…</c>, the upstream-path-shaped proxy), or
/// <see cref="NotificationPreferencesController"/>. The mobile-facing paths
/// (<c>/v1/notifications</c>, <c>/v1/notifications/{id}/read</c>) are a PATH-SHAPE
/// alignment of those existing proxies, mirroring how <see cref="JeebWalletController"/>
/// / <see cref="JeebReviewsController"/> added the mobile shapes over generic clients.
/// </para>
///
/// <para>
/// ADR-0001 (STATELESS &amp; THIN): this controller authenticates, resolves the
/// caller's own id from the bearer token (or trusted-edge <c>X-User-Id</c>), maps to
/// the EXISTING generic <see cref="ServiceNotificationClient"/> receiver-list +
/// mark-read primitives, applies the Jeeb presentation projection
/// (<see cref="JeebNotificationsProjection"/>), and returns. It holds NO state, NO
/// persistence, NO session and NO domain rules. The generic notification-service
/// stays product-agnostic; all Jeeb shaping lives in the gateway projection.
/// </para>
///
/// <para>
/// Coverage note: unlike the wallet/reviews families (PR #196/#197), the generic
/// <see cref="ServiceNotificationClient"/> DOES expose both primitives this needs, so
/// these routes are wired to the real upstream — no fabricated state. The
/// mobile-tolerated fallback is only the COLD-START EMPTY page when the upstream
/// returns no rows / a null payload, and a 200 on a successful mark-read.
/// </para>
/// </summary>
[ApiController]
[Route("v1/notifications")]
[Produces("application/json")]
public sealed class JeebNotificationsInboxController : ControllerBase
{
    private readonly ServiceNotificationClient _notifications;
    private readonly ILogger<JeebNotificationsInboxController> _log;

    public JeebNotificationsInboxController(
        ServiceNotificationClient notifications,
        ILogger<JeebNotificationsInboxController> log)
    {
        _notifications = notifications;
        _log = log;
    }

    /// <summary>
    /// JEBV4-249 — map a caught upstream notification-service
    /// <see cref="NotificationApiException"/> to a sanitized RFC 7807 ProblemDetails.
    /// The upstream status is preserved (clamped to a valid 4xx/5xx; anything else →
    /// 502 Bad Gateway), but the upstream message/body is logged server-side ONLY and
    /// never echoed to the caller. Only the GENERAL catches route here; the deliberate
    /// <c>when (401 or 403) → Unauthorized()</c> and <c>when (404) → NotFound()</c>
    /// status mappers keep their behaviour. (Previously echoed the raw upstream
    /// <c>ex.Message</c> in the response detail.)
    /// </summary>
    private IActionResult UpstreamProblem(NotificationApiException ex)
    {
        var status = ex.StatusCode is >= 400 and < 600
            ? ex.StatusCode
            : StatusCodes.Status502BadGateway;

        _log.LogWarning(ex,
            "Notifications BFF: notification-service call failed on {Method} {Path} → {Status}.",
            Request.Method, Request.Path, status);

        return Problem(
            title: "The notifications request could not be completed.",
            statusCode: status);
    }

    /// <summary>
    /// GET /v1/notifications?userId=&amp;page=&amp;pageSize= — one page of the caller's
    /// inbox (JM-057). The authoritative receiver is the bearer/edge identity; the
    /// mobile-sent <c>userId</c> query hint is accepted but the verified caller id is
    /// used (the gateway never lets one user read another's inbox).
    /// </summary>
    [HttpGet]
    [RequireCapability(Capabilities.NotificationsReadSelf)] // ADR-005 §B self / any-auth
    [ProducesResponseType(typeof(JeebNotificationsPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ListNotifications(
        [FromQuery] string? userId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? 20 : pageSize;

        try
        {
            // read_status: "all" → no upstream read-state filter; the mobile inbox shows
            // read + unread rows and drives its own badge from each row's `read` flag.
            var response = await _notifications
                .Get_messages_by_receiver_messages_receiver__receiver_id__getAsync(
                    callerId,
                    page: safePage,
                    page_size: safeSize,
                    read_status: "all",
                    notification_type: null,
                    sender: null,
                    created_after: null,
                    created_before: null,
                    ct);

            var (rows, total) = ExtractRows(response);
            return Ok(JeebNotificationsProjection.ProjectPage(rows, safePage, safeSize, total));
        }
        catch (NotificationApiException ex) when (ex.StatusCode is 401 or 403)
        {
            return Unauthorized();
        }
        catch (NotificationApiException ex)
        {
            return UpstreamProblem(ex);
        }
    }

    /// <summary>
    /// PATCH /v1/notifications/{id}/read — mark a single notification read (JM-057).
    /// Maps onto the generic single mark-read primitive. The mobile repo only awaits a
    /// non-error response (optimistic local read toggle), so a 200 suffices.
    /// </summary>
    [HttpPatch("{id}/read")]
    [RequireCapability(Capabilities.NotificationsReadSelf)] // ADR-005 §B (STATE: ownership in-action)
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> MarkRead(string id, CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;

        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "id is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-notification",
            });
        }

        try
        {
            await _notifications
                .Mark_notification_read_notifications__notification_id__mark_read_patchAsync(id.Trim(), ct);
            return Ok();
        }
        catch (NotificationApiException ex) when (ex.StatusCode is 401 or 403)
        {
            return Unauthorized();
        }
        catch (NotificationApiException ex) when (ex.StatusCode == 404)
        {
            return NotFound();
        }
        catch (NotificationApiException ex)
        {
            return UpstreamProblem(ex);
        }
    }

    /// <summary>
    /// Pull the normalized inbox rows + the upstream total out of the (Newtonsoft
    /// <c>JObject</c>) receiver-list payload. Tolerant of the upstream field-name
    /// variants (snake/Pascal) and of a missing <c>items</c>/<c>total</c> — a shape it
    /// doesn't recognise yields an empty list (cold-start), never an exception. Mirrors
    /// the dynamic-extraction <see cref="NotificationController"/> already does, but
    /// returns the transport-free <see cref="UpstreamNotificationRow"/> the pure
    /// projection is tested against.
    /// </summary>
    private static (IReadOnlyList<UpstreamNotificationRow> Rows, int? Total) ExtractRows(object? response)
    {
        if (response is not JToken token)
        {
            // NSwag (Newtonsoft) deserialises the upstream `object` to a JToken; if not,
            // round-trip it so the same extraction path applies.
            if (response is null) return (Array.Empty<UpstreamNotificationRow>(), null);
            token = JToken.FromObject(response);
        }

        var root = token as JObject;
        var itemsToken = root? ["items"] ?? root? ["notifications"] ?? root? ["messages"];
        int? total = (root? ["total"] ?? root? ["totalCount"] ?? root? ["count"])?.Value<int?>();

        // Some upstreams return a bare array rather than an envelope.
        if (itemsToken is not JArray && token is JArray bareArray)
        {
            itemsToken = bareArray;
        }

        var rows = new List<UpstreamNotificationRow>();
        if (itemsToken is JArray array)
        {
            foreach (var node in array)
            {
                if (node is JObject obj)
                {
                    rows.Add(MapRow(obj));
                }
            }
        }

        return (rows, total);
    }

    /// <summary>Map one upstream <c>JObject</c> row to the normalized intermediate (tolerant of field aliases).</summary>
    private static UpstreamNotificationRow MapRow(JObject obj) => new()
    {
        Id = Str(obj, "id", "notification_id", "notificationId", "messageId", "message_id"),
        Type = Str(obj, "type", "notification_type", "notificationType", "kind"),
        Title = Str(obj, "title", "subject"),
        Body = Str(obj, "body", "message", "description", "subtitle"),
        Timestamp = Str(obj, "ts", "timestamp", "createdAt", "created_at"),
        Status = Str(obj, "status", "read_status", "readStatus"),
        Ref = Str(obj, "ref", "targetId", "target_id", "deliveryId", "delivery_id",
            "entityId", "entity_id", "referenceId", "reference_id"),
    };

    private static string? Str(JObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var t = obj[key];
            if (t is null || t.Type == JTokenType.Null) continue;
            var s = t.Type == JTokenType.Date
                ? t.Value<DateTimeOffset>().ToString("o")
                : t.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }
}
