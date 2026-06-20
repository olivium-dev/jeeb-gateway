using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JeebGateway.JeebNotifications;

/// <summary>
/// One inbox row for <c>GET /v1/notifications</c>. Keys match the exact shape the
/// mobile <c>DioNotificationsRepository._item</c> reads (JM-057):
/// <c>{ id, type, title, body, ts, read, ref }</c>.
///
/// <para>
/// The mobile parser is tolerant — it ALSO accepts <c>kind</c> for <c>type</c>,
/// <c>message</c> for <c>body</c>, <c>timestamp</c>/<c>createdAt</c> for <c>ts</c>,
/// and <c>targetId</c>/<c>deliveryId</c> for <c>ref</c> — but the gateway emits the
/// canonical camelCase shape. Crucially the mobile reads <c>read</c> as a BOOLEAN
/// (<c>json['read'] == true</c>), so the gateway projects the upstream
/// notification-service <c>status</c> string ("read"/"delivered"/…) into a boolean
/// here rather than leaking the upstream status vocabulary to the client.
/// </para>
/// </summary>
public sealed class JeebNotificationItemResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The opaque notification type (mobile <c>_kind</c> switch maps it; unknown → <c>unknown</c> kind, rendered gracefully).</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    /// <summary>ISO-8601 timestamp (mobile reads <c>ts</c>, tolerates <c>timestamp</c>/<c>createdAt</c>).</summary>
    [JsonPropertyName("ts")]
    public string Ts { get; set; } = string.Empty;

    /// <summary>Drives the unread badge. Projected from the upstream <c>status</c> ("read" → true).</summary>
    [JsonPropertyName("read")]
    public bool Read { get; set; }

    /// <summary>Deep-link target id (order/delivery/dispute/request); omitted when absent.</summary>
    [JsonPropertyName("ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ref { get; set; }
}

/// <summary>
/// The inbox envelope for <c>GET /v1/notifications</c>. The mobile
/// <c>DioNotificationsRepository.fetchNotifications</c> reads the <c>items</c> array
/// (it also tolerates <c>notifications</c>); the paging fields are emitted for
/// parity with the rest of the Jeeb gateway surface and future client paging.
/// </summary>
public sealed class JeebNotificationsPageResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<JeebNotificationItemResponse> Items { get; set; } = new List<JeebNotificationItemResponse>();

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}

/// <summary>
/// A normalized, transport-free intermediate row the <see cref="JeebNotificationsProjection"/>
/// shapes into <see cref="JeebNotificationItemResponse"/>. The controller extracts these
/// from the (Newtonsoft <c>JObject</c>) upstream payload; keeping the projection over this
/// plain shape lets it be unit-tested without HTTP/JSON (ADR-0001 thin map; mirrors
/// <c>JeebReviewsProjectionTests</c>).
/// </summary>
public sealed class UpstreamNotificationRow
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? Timestamp { get; set; }

    /// <summary>The upstream status string ("read", "delivered", "unread", …); "read" (any case) ⇒ read.</summary>
    public string? Status { get; set; }
    public string? Ref { get; set; }
}
