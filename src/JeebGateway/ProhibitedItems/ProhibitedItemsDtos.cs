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
