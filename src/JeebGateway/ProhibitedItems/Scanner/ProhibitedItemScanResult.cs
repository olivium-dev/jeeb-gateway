namespace JeebGateway.ProhibitedItems.Scanner;

public enum ProhibitedMatchType
{
    Exact,
    Synonym,
    Fuzzy
}

public class ProhibitedItemMatch
{
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }
    public required string Category { get; init; }
    /// <summary>The catalog term or synonym that produced the hit.</summary>
    public required string MatchedTerm { get; init; }
    /// <summary>The substring of the input that triggered the match.</summary>
    public required string Evidence { get; init; }
    public required ProhibitedMatchType MatchType { get; init; }
    /// <summary>1.0 for exact / synonym, &lt;1 for fuzzy hits scaled by edit distance.</summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Moderation severity of the matched lexicon entry (JEB-63). Additive —
    /// defaults to <see cref="ProhibitedSeverity.Block"/> for matches produced
    /// before this field existed (fail-safe to the stricter outcome). The
    /// create-time moderation gate reads this to choose between a hard
    /// <c>prohibited_item_blocked</c> 409 and a soft
    /// <c>prohibited_item_requires_ack</c> 409. The advisory /prohibited-items/scan
    /// endpoint ignores it (it never blocks).
    /// </summary>
    public ProhibitedSeverity Severity { get; init; } = ProhibitedSeverity.Block;
}

public class ProhibitedItemScanResult
{
    public required IReadOnlyList<ProhibitedItemMatch> Matches { get; init; }
    /// <summary>
    /// True when at least one match cleared the review threshold. Callers
    /// MUST NOT auto-block on this flag; it only indicates that admin review
    /// is warranted.
    /// </summary>
    public required bool RequiresReview { get; init; }

    /// <summary>
    /// JEB-63 create-gate helper: the highest severity across the matches that
    /// cleared the review threshold (<see cref="RequiresReview"/>), or null when
    /// there is no review-grade match. <see cref="ProhibitedSeverity.Block"/>
    /// outranks <see cref="ProhibitedSeverity.Warn"/>. Below-threshold report-only
    /// hits never gate a create — they mirror the advisory-scan "we noticed
    /// something" hint, not a hard/soft block.
    /// </summary>
    public ProhibitedSeverity? GatingSeverity =>
        !RequiresReview
            ? null
            : Matches.Any(m => m.Severity == ProhibitedSeverity.Block)
                ? ProhibitedSeverity.Block
                : ProhibitedSeverity.Warn;
}
