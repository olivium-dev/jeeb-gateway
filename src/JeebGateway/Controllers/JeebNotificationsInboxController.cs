using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.JeebNotifications;
using JeebGateway.Notifications;
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
/// empty page requires an explicit upstream list. Malformed successful payloads
/// return a sanitized dependency-contract failure.
/// </para>
/// </summary>
[ApiController]
[Route("v1/notifications")]
// W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
[Route("notifications")]
[Produces("application/json")]
public sealed class JeebNotificationsInboxController : ControllerBase
{
    // Resolve at most 25% of the capped 20-row page so synchronous index work cannot
    // multiply across an offer-heavy page; later rows degrade safely to the shell.
    internal const int MaxOfferResolutionRowsPerPage = 5;

    private readonly ServiceNotificationClient _notifications;
    private readonly IOfferRequestIndex _offerRequestIndex;
    private readonly ILogger<JeebNotificationsInboxController> _log;

    public JeebNotificationsInboxController(
        ServiceNotificationClient notifications,
        IOfferRequestIndex offerRequestIndex,
        ILogger<JeebNotificationsInboxController> log)
    {
        _notifications = notifications;
        _offerRequestIndex = offerRequestIndex;
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

            var (rows, total, offerRouteCandidates) = ExtractRows(response);
            ResolveOfferRequestRefs(offerRouteCandidates);

            // F5 (JEBV4-302) PRIVACY FILTER — the "finding jeebers" new-request broadcast
            // is fanned to the jeeb_jeebers FCM topic (see NewRequestPushNotifier). A
            // downstream relay defect resolves that topic send to ALL users and persists a
            // receiver row per user, so a pure-customer caller's inbox can surface a
            // new_request row carrying ANOTHER customer's order text. Until the relay/
            // notification-service is fixed to scope topic delivery to actual subscribers
            // (jeebers only) — escalated as a separate infra ticket — the gateway drops
            // jeeber-only broadcast rows for any caller who does NOT hold the jeeber
            // ("driver") role in their available_roles. A dual-role user (customer +
            // jeeber) keeps the rows because GetRoles returns the FULL available-role set.
            // This closes the customer-facing READ path of the leak deterministically.
            var isJeeber = UserIdentity.HasRole(HttpContext, Roles.Jeeber);
            var (visibleRows, dropped) = FilterJeeberBroadcasts(rows, isJeeber);

            // When rows were dropped the upstream grand total is no longer authoritative
            // for this caller; fall back to the on-page count so the projected total does
            // not advertise notifications the caller can never see. (Mobile tolerates a
            // total derived from the page — same cold-start path.)
            var effectiveTotal = dropped > 0 ? (int?)null : total;
            return Ok(JeebNotificationsProjection.ProjectPage(visibleRows, safePage, safeSize, effectiveTotal));
        }
        catch (NotificationApiException ex) when (ex.StatusCode is 401 or 403)
        {
            return Unauthorized();
        }
        catch (NotificationApiException ex)
        {
            return UpstreamProblem(ex);
        }
        catch (NotificationContractException)
        {
            return Problem(title: "The notifications response could not be verified.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://jeeb.dev/errors/notification-contract-invalid");
        }
    }

    private void ResolveOfferRequestRefs(
        IReadOnlyList<OfferRouteCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            candidate.Row.Ref = null;
        }

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        var rowsToResolve = Math.Min(candidates.Count, MaxOfferResolutionRowsPerPage);
        for (var index = 0; index < rowsToResolve; index++)
        {
            var candidate = candidates[index];
            if (!resolved.TryGetValue(candidate.OfferId, out var requestId))
            {
                try
                {
                    requestId = _offerRequestIndex.ResolveRequestId(candidate.OfferId);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Notification inbox offer route resolution failed for offer {OfferId}.",
                        candidate.OfferId);
                    requestId = null;
                }
                resolved[candidate.OfferId] = requestId;
            }

            candidate.Row.Ref = NotificationDeepLinkResolver.ValidateEntityId(requestId);
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
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

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
            var notificationId = id.Trim();
            if (!await NotificationBelongsToCallerAsync(callerId, notificationId, ct))
            {
                return NotFound();
            }

            await _notifications
                .Mark_notification_read_notifications__notification_id__mark_read_patchAsync(notificationId, ct);
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
        catch (NotificationContractException)
        {
            return Problem(title: "The notifications response could not be verified.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://jeeb.dev/errors/notification-contract-invalid");
        }
    }

    private async Task<bool> NotificationBelongsToCallerAsync(
        string callerId,
        string notificationId,
        CancellationToken ct)
    {
        const int pageSize = 100;

        for (var page = 1; ; page++)
        {
            var response = await _notifications
                .Get_messages_by_receiver_messages_receiver__receiver_id__getAsync(
                    callerId,
                    page,
                    pageSize,
                    read_status: "all",
                    notification_type: null,
                    sender: null,
                    created_after: null,
                    created_before: null,
                    ct);

            var (rows, total, _) = ExtractRows(response);
            if (rows.Any(row =>
                    string.Equals(row.Id?.Trim(), notificationId, StringComparison.Ordinal)))
            {
                return true;
            }

            if (rows.Count < pageSize
                || total.HasValue && (long)page * pageSize >= total.Value)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// F5 (JEBV4-302). Notification <c>type</c> values that are jeeber-only broadcasts —
    /// pushed to the <c>jeeb_jeebers</c> topic for the reverse-auction "finding jeebers"
    /// flow and NEVER meant for a customer's inbox. A caller lacking the jeeber
    /// ("driver") role must not see these rows (they carry other customers' order text).
    /// Matched case-insensitively; kept as a set so sibling jeeber-broadcast types can be
    /// added without touching the filter logic. BOTH spellings are live: the legacy notifier
    /// writes <c>new_request</c>, the generic events route writes <c>jeeb.new_request</c>.
    /// </summary>
    private static readonly HashSet<string> JeeberBroadcastTypes =
        new(StringComparer.OrdinalIgnoreCase) { "new_request", "jeeb.new_request" };

    /// <summary>
    /// Drop jeeber-only broadcast rows (see <see cref="JeeberBroadcastTypes"/>) for a
    /// caller who is not a jeeber. When <paramref name="callerIsJeeber"/> is true the rows
    /// pass through untouched. Returns the visible rows and how many were dropped so the
    /// caller can decide whether the upstream total is still trustworthy. Pure and
    /// null-tolerant — a null/empty input yields an empty list.
    /// </summary>
    internal static (IReadOnlyList<UpstreamNotificationRow> Visible, int Dropped) FilterJeeberBroadcasts(
        IReadOnlyList<UpstreamNotificationRow>? rows, bool callerIsJeeber)
    {
        if (rows is null || rows.Count == 0)
        {
            return (Array.Empty<UpstreamNotificationRow>(), 0);
        }

        if (callerIsJeeber)
        {
            return (rows, 0);
        }

        var visible = new List<UpstreamNotificationRow>(rows.Count);
        var dropped = 0;
        foreach (var row in rows)
        {
            var type = row?.Type?.Trim();
            if (type is not null && JeeberBroadcastTypes.Contains(type))
            {
                dropped++;
                continue;
            }
            if (row is not null) visible.Add(row);
        }

        return (visible, dropped);
    }

    /// <summary>
    /// Pull the normalized inbox rows + the upstream total out of the (Newtonsoft
    /// <c>JObject</c>) receiver-list payload. Tolerant of the upstream field-name
    /// variants and an optional total. Unknown envelopes and malformed rows fail
    /// explicitly instead of being interpreted as a successful empty inbox. Mirrors
    /// the dynamic-extraction <see cref="NotificationController"/> already does, but
    /// returns the transport-free <see cref="UpstreamNotificationRow"/> the pure
    /// projection is tested against.
    /// </summary>
    private static (
        IReadOnlyList<UpstreamNotificationRow> Rows,
        int? Total,
        IReadOnlyList<OfferRouteCandidate> OfferRouteCandidates) ExtractRows(object? response)
    {
        if (response is not JToken token)
        {
            // NSwag (Newtonsoft) deserialises the upstream `object` to a JToken; if not,
            // round-trip it so the same extraction path applies.
            if (response is null)
            {
                throw new NotificationContractException();
            }
            token = JToken.FromObject(response);
        }

        var root = token as JObject;
        var itemsToken = root? ["items"] ?? root? ["notifications"] ?? root? ["messages"];
        var totalToken = root? ["total"] ?? root? ["totalCount"] ?? root? ["count"] ?? root? ["total_messages"];
        int? total = null;
        if (totalToken is not null && totalToken.Type != JTokenType.Null)
        {
            if (totalToken.Type != JTokenType.Integer || !int.TryParse(totalToken.ToString(), out var parsedTotal)
                || parsedTotal < 0)
                throw new NotificationContractException();
            total = parsedTotal;
        }

        // Some upstreams return a bare array rather than an envelope.
        if (itemsToken is not JArray && token is JArray bareArray)
        {
            itemsToken = bareArray;
        }

        var rows = new List<UpstreamNotificationRow>();
        var offerRouteCandidates = new List<OfferRouteCandidate>();
        if (itemsToken is not JArray array || total < array.Count)
            throw new NotificationContractException();
        foreach (var node in array)
        {
            if (node is not JObject obj) throw new NotificationContractException();
            var row = MapRow(obj);
            rows.Add(row);
            var payloadOfferId = NormalizeMappedRow(row, obj);
            if (payloadOfferId is not null)
                offerRouteCandidates.Add(new OfferRouteCandidate(row, payloadOfferId));
        }

        DeduplicateByNotificationId(rows);
        offerRouteCandidates.RemoveAll(candidate =>
            !rows.Any(row => ReferenceEquals(row, candidate.Row)));
        return (rows, total, offerRouteCandidates);
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
        Ref = TargetScalar(obj, "ref", "targetId", "target_id", "deliveryId", "delivery_id",
            "entityId", "entity_id", "referenceId", "reference_id", "caseId", "case_id"),
    };

    internal static (IReadOnlyList<UpstreamNotificationRow> Rows, int? Total)
        ExtractRowsForTests(object? response)
    {
        var (rows, total, _) = ExtractRows(response);
        return (rows, total);
    }

    private static string? NormalizeMappedRow(UpstreamNotificationRow row, JObject obj)
    {
        var payload = OptionalObject(obj["payload"]);
        var metadata = OptionalObject(obj["metadata"]);
        if (string.Equals(row.Type?.Trim(), "text_message", StringComparison.OrdinalIgnoreCase))
        {
            row.Type = StrScalar(metadata, "event_type", "business_type", "notification_type")
                ?? StrScalar(payload, "businessType", "business_type", "notificationType",
                    "notification_type", "message_type")
                ?? row.Type;
        }
        row.Type = NormalizeType(row.Type);
        row.DeepLink = ExplicitLink(metadata, "deep_link", "deepLink")
            ?? ExplicitLink(payload, "deepLink", "deep_link");
        row.Timestamp ??= StrScalar(payload, "created_at");
        row.Timestamp ??= StrScalar(obj, "at");
        row.Timestamp ??= ObjectIdTimestamp(StrScalar(obj, "_id"));
        if (row.Type?.StartsWith("jeeb.dispute.", StringComparison.OrdinalIgnoreCase) == true
            || row.Type?.StartsWith("jeeb.support.", StringComparison.OrdinalIgnoreCase) == true)
            row.Ref ??= TargetScalar(metadata, "case_id", "caseId");
        if (string.Equals(row.Type, "offer", StringComparison.OrdinalIgnoreCase))
        {
            row.Ref = TargetScalar(payload, "request_id", "requestId")
                ?? TargetScalar(obj, "requestId", "request_id") ?? row.Ref;
            if (row.Ref is not null) return null;
            var payloadOfferId = TargetScalar(payload, "offer_id");
            row.Ref = payloadOfferId;
            return payloadOfferId;
        }

        // A typed request/delivery destination has stronger provenance than a
        // generic legacy ref alias. Never let a conversation/offer alias win it.
        row.Ref = TargetRef(obj, payload, row.Type) ?? row.Ref;
        return null;
    }

    private static void DeduplicateByNotificationId(List<UpstreamNotificationRow> rows)
    {
        var seenNotificationIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Count;)
        {
            var notificationId = rows[index].Id?.Trim();
            if (!string.IsNullOrEmpty(notificationId)
                && !seenNotificationIds.Add(notificationId))
            {
                rows.RemoveAt(index);
                continue;
            }

            index++;
        }
    }

    private static string? NormalizeType(string? wireType)
    {
        var trimmed = wireType?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || !trimmed.StartsWith("jeeb.", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var jeebType = trimmed["jeeb.".Length..].Trim().ToLowerInvariant();
        if (jeebType.StartsWith("dispute.", StringComparison.Ordinal)
            || jeebType.StartsWith("support.", StringComparison.Ordinal))
            return "jeeb." + jeebType;
        return jeebType == "offer_received" ? "offer" : jeebType;
    }

    // Request-addressed routes never hoist an offer_id or conversationId into ref.
    private static string? TargetRef(JObject obj, JObject? payload, string? type)
        => type?.ToLowerInvariant() switch
        {
            "offer_accepted" => TargetScalar(payload, "request_id", "requestId")
                ?? TargetScalar(obj, "requestId", "request_id"),
            "new_request" or "chat" or "chat_message" or "request.try_expand_tier"
                or "request.expired" or "request_expired" or "request_expiry" or "request_expiring" or "offer_lost"
                => TargetScalar(obj, "requestId", "request_id") ?? TargetScalar(payload, "requestId", "request_id"),
            "delivery" or "delivery_status_updated" or "cancellation_decision"
                => TargetScalar(obj, "delivery_id", "deliveryId", "order_id")
                    ?? TargetScalar(payload, "delivery_id", "deliveryId", "order_id")
                    ?? TargetScalar(obj, "requestId", "request_id") ?? TargetScalar(payload, "requestId", "request_id"),
            "dispute_resolved" => TargetScalar(payload, "dispute_id"),
            "settlement_paid" => TargetScalar(payload, "settlement_id"),
            "kyc_approved" or "kyc_rejected" => TargetScalar(payload, "kyc_id"),
            _ when type?.StartsWith("jeeb.dispute.", StringComparison.OrdinalIgnoreCase) == true
                || type?.StartsWith("jeeb.support.", StringComparison.OrdinalIgnoreCase) == true
                => TargetScalar(payload, "caseId", "case_id", "member_id"),
            _ => null,
        };

    private static JObject? OptionalObject(JToken? token) => token is null || token.Type == JTokenType.Null
        ? null : token as JObject ?? throw new NotificationContractException();

    private static string? TargetScalar(JObject? obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = obj?[key];
            if (token is null || token.Type == JTokenType.Null) continue;
            if (token.Type != JTokenType.String) throw new NotificationContractException();
            var value = NotificationDeepLinkResolver.ValidateEntityId(token.Value<string>());
            if (value is not null) return value;
        }
        return null;
    }

    private static string? ExplicitLink(JObject? obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = obj?[key];
            if (token is null || token.Type == JTokenType.Null) continue;
            if (token.Type != JTokenType.String) throw new NotificationContractException();
            return NotificationDeepLinkResolver.ValidateExplicitLink(token.Value<string>()!);
        }
        return null;
    }

    private static string? ObjectIdTimestamp(string? id)
    {
        if (id is null || id.Length != 24 || !id.All(Uri.IsHexDigit)
            || !uint.TryParse(id[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var seconds))
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("o", CultureInfo.InvariantCulture);
    }

    private sealed record OfferRouteCandidate(
        UpstreamNotificationRow Row,
        string OfferId);

    private static string? StrScalar(JObject? obj, params string[] keys)
    {
        if (obj is null) return null;

        foreach (var key in keys)
        {
            var token = obj[key];
            if (token is not JValue scalar
                || token.Type is not (
                    JTokenType.String
                    or JTokenType.Integer
                    or JTokenType.Float
                    or JTokenType.Date
                    or JTokenType.Guid))
            {
                continue;
            }

            var value = scalar.Value switch
            {
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("o"),
                DateTime dateTime => dateTime.ToString("o", CultureInfo.InvariantCulture),
                _ => Convert.ToString(scalar.Value, CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

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
