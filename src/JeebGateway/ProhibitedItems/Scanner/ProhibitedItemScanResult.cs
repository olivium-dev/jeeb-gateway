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
}
