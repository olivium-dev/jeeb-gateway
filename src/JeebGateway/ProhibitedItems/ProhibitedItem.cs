namespace JeebGateway.ProhibitedItems;

/// <summary>
/// Moderation severity for a gateway-owned prohibited-items lexicon entry
/// (JEB-63). The create-time moderation gate maps severity to an HTTP outcome:
///   <list type="bullet">
///     <item><see cref="Block"/> — hard reject the create with 409
///       <c>prohibited_item_blocked</c>; an ack must NOT override it (AC7).</item>
///     <item><see cref="Warn"/> — soft gate; the create is 409
///       <c>prohibited_item_requires_ack</c> until the caller has acknowledged
///       the current lexicon version, then it is allowed (AC3).</item>
///   </list>
/// Additive on the existing catalog row. The lexicon stays gateway-owned (the
/// N11 boundary guard requires the lexicon to live ONLY under the gateway's
/// list key <see cref="JeebModerationList.ListKey"/> = <c>jeeb-prohibited-items</c>,
/// never in ban-service), so severity is a gateway concern.
/// </summary>
public enum ProhibitedSeverity
{
    Warn,
    Block
}

public class ProhibitedItem
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Moderation severity (JEB-63). Defaults to <see cref="ProhibitedSeverity.Block"/>
    /// so a pre-existing catalog row created before this field existed is treated
    /// as the stricter hard-reject outcome until an admin classifies it. Additive —
    /// older create/update paths that don't set it keep working unchanged.
    /// </summary>
    public ProhibitedSeverity Severity { get; set; } = ProhibitedSeverity.Block;

    public bool Active { get; set; } = true;
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
