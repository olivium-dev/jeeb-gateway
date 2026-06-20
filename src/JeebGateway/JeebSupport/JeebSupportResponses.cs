using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JeebGateway.JeebSupport;

/// <summary>
/// Request body for <c>POST /v1/support/tickets</c> (JM-063). Keys mirror the
/// exact shape the mobile <c>DioSupportRepository.submitTicket</c> sends:
/// <c>{ category, body, orderRef?, attachments? }</c>.
///
/// <para><b>DTO-drift reconciliation (gateway is the authoritative shape).</b>
/// Mobile sends the order link as <c>orderRef</c>; the canonical/mock contract
/// names it <c>orderId</c>. This DTO accepts BOTH (<see cref="OrderRef"/> +
/// <see cref="OrderId"/>) so the order link is never silently dropped; the
/// projection canonicalizes to a single stored <c>orderId</c>. Mobile also
/// sends the category enum names <c>delivery</c>/<c>kycAppeal</c> which the
/// canonical catalog spells <c>order</c>/<c>kyc</c>; the projection reconciles
/// those names rather than 400-ing the live submit.</para>
/// </summary>
public sealed class CreateSupportTicketRequest
{
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>Mobile's name for the optional linked order/delivery.</summary>
    [JsonPropertyName("orderRef")]
    public string? OrderRef { get; set; }

    /// <summary>Canonical name for the optional linked order/delivery (also accepted).</summary>
    [JsonPropertyName("orderId")]
    public string? OrderId { get; set; }

    /// <summary>Optional subject line (mobile does not send one today).</summary>
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    /// <summary>Optional local attachment references.</summary>
    [JsonPropertyName("attachments")]
    public IReadOnlyList<string>? Attachments { get; set; }
}

/// <summary>
/// One support ticket on the wire for <c>POST /v1/support/tickets</c> (201) and
/// <c>GET /v1/support/tickets/{id}</c> (200). The canonical ticket shape:
/// <c>{ id, ticketNumber, userId, category, subject, body, attachments,
/// orderId, status, createdAt, updatedAt }</c>.
///
/// <para>Mobile's <c>DioSupportRepository</c> only reads <c>id</c> (it also
/// tolerates <c>ticketId</c>) and <c>status</c> on submit; the full shape is
/// emitted for the list/detail surface and forward-compat.</para>
/// </summary>
public sealed class SupportTicketResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ticketNumber")]
    public string TicketNumber { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The canonical category (e.g. <c>order</c>/<c>kyc</c>), after reconciliation.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("attachments")]
    public IReadOnlyList<string> Attachments { get; set; } = Array.Empty<string>();

    /// <summary>Canonical order link (reconciled from mobile's <c>orderRef</c>/<c>orderId</c>).</summary>
    [JsonPropertyName("orderId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrderId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "open";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>
/// The paged envelope for <c>GET /v1/support/tickets</c>. Mirrors the rest of
/// the Jeeb gateway surface (wallet/reviews/notifications) and the mock support
/// contract: <c>{ items, page, pageSize, totalCount, totalPages, cursor }</c>.
/// </summary>
public sealed class SupportTicketsPageResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<SupportTicketResponse> Items { get; set; } = new List<SupportTicketResponse>();

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }
}

/// <summary>One category the picker renders for <c>GET /v1/support/categories</c>.</summary>
public sealed class SupportCategoryResponse
{
    /// <summary>The canonical category id sent back on create (e.g. <c>order</c>).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Human label for the picker.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

/// <summary>The categories catalog envelope: <c>{ items: [...] }</c> (gateway-owned, no state).</summary>
public sealed class SupportCategoriesResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<SupportCategoryResponse> Items { get; set; } = new List<SupportCategoryResponse>();
}

/// <summary>
/// A normalized, transport-free support-ticket row — the projection's stored
/// shape (it round-trips verbatim through the jeeb-state-service opaque KV) and
/// the unit of the pure projection (ADR-0001 thin map; tested without HTTP/DI,
/// mirroring <c>JeebNotificationsProjectionTests</c>). Distinct from the wire
/// DTO only so the store can persist/read it independent of MVC serialization.
/// </summary>
public sealed class SupportTicketRow
{
    public string Id { get; set; } = string.Empty;
    public string TicketNumber { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public string Body { get; set; } = string.Empty;
    public IReadOnlyList<string> Attachments { get; set; } = Array.Empty<string>();
    public string? OrderId { get; set; }
    public string Status { get; set; } = "open";
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}
