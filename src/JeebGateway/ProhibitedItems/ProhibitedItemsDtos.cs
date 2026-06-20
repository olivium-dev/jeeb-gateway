namespace JeebGateway.ProhibitedItems;

public class ProhibitedItemDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// JEB-63 moderation severity ("warn" | "block"). Additive — drives the
    /// gateway create-time moderation gate (block = hard reject, warn = ack-gate).
    /// </summary>
    public string Severity { get; init; } = "block";
    public required bool Active { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public class ProhibitedItemCreateRequest
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// JEB-63 moderation severity ("warn" | "block"). Optional — defaults to
    /// "block" when omitted, so existing admin create calls keep the stricter
    /// hard-reject classification. Additive.
    /// </summary>
    public string? Severity { get; set; }
}

public class ProhibitedItemUpdateRequest
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }

    /// <summary>JEB-63 moderation severity ("warn" | "block"); null = unchanged. Additive.</summary>
    public string? Severity { get; set; }
    public bool? Active { get; set; }
}

public class AdminProhibitedItemsListResponse
{
    public required IReadOnlyList<ProhibitedItemDto> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}

/// <summary>
/// Mobile read-shape. <see cref="Version"/> is the maximum updated_at across the
/// active set; the client echoes it back to /prohibited-items/acknowledge so we
/// can tell whether the user acknowledged the *current* list or a stale one.
/// </summary>
public class ProhibitedItemsListResponse
{
    public required IReadOnlyList<ProhibitedItemDto> Items { get; init; }
    public required string Version { get; init; }
    public required bool Acknowledged { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; init; }
}

public class ProhibitedItemsAcknowledgeRequest
{
    public string? Version { get; set; }
}

public class ProhibitedItemsAcknowledgeResponse
{
    public required string UserId { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset AcknowledgedAt { get; init; }
}

/// <summary>
/// WS-06 (RAT-04 / ACCT-03 / ADM-03): body for the synchronous content-moderation
/// check (<c>POST /moderation/jeeb/check</c>). Unlike the create-time gate this is
/// content-agnostic — the same lexicon scan that gates a request-create is reused to
/// moderate arbitrary text such as a display name, so there is no request/order context.
/// </summary>
public class ModerationCheckRequest
{
    /// <summary>The free text to moderate (request description, display name, etc.).</summary>
    public string? Text { get; set; }
}

/// <summary>
/// WS-06: verdict returned by <c>POST /moderation/jeeb/check</c>. <c>Decision</c> is one of
/// <c>allow</c> | <c>warn</c> | <c>block</c> and mirrors the create-gate severity mapping
/// (block ⇒ <c>prohibited_item_blocked</c>, warn ⇒ <c>prohibited_item_requires_ack</c>) so a
/// caller can pre-flight the exact outcome the create path would produce. <c>Version</c> is the
/// current lexicon version, so a warn caller can drive the version-pinned acknowledge flow.
/// </summary>
public class ModerationCheckResponse
{
    public required string Decision { get; init; }
    public required string Version { get; init; }
    public string? Reason { get; init; }
    public required IReadOnlyList<JeebGateway.Controllers.ModerationMatchDto> Matches { get; init; }
}

/// <summary>WS-06 (ADM-03): one row of a <c>POST /admin/prohibited-items/bulk-import</c> body.</summary>
public class ProhibitedItemBulkImportRow
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? Severity { get; set; }
}

/// <summary>WS-06 (ADM-03): the bulk-import envelope. <c>Items</c> must be non-empty.</summary>
public class ProhibitedItemBulkImportRequest
{
    public List<ProhibitedItemBulkImportRow>? Items { get; set; }
}

/// <summary>WS-06: per-row outcome of a bulk import. <c>Outcome</c> is <c>created</c> | <c>duplicate</c> | <c>invalid</c>.</summary>
public class ProhibitedItemBulkImportRowResult
{
    public required int Index { get; init; }
    public required string Outcome { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Error { get; init; }
}

/// <summary>WS-06 (ADM-03): aggregate bulk-import result with per-row outcomes.</summary>
public class ProhibitedItemBulkImportResponse
{
    public required int Imported { get; init; }
    public required int Skipped { get; init; }
    public required int Total { get; init; }
    public required IReadOnlyList<ProhibitedItemBulkImportRowResult> Results { get; init; }
}
