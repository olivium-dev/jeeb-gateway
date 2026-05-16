namespace JeebGateway.Matching;

/// <summary>
/// Tuning knobs for the geo-matching engine (T-backend-008). Defaults
/// match the ticket's acceptance criteria.
/// </summary>
public sealed class MatchingOptions
{
    public const string SectionName = "Matching";

    /// <summary>
    /// Maximum number of Jeebers to notify per request. The matching
    /// engine sorts by proximity-then-rating and pushes to the top N.
    /// Defaults to the on-call observed sweet spot — large enough to
    /// guarantee multiple offers in a sparse zone, small enough that a
    /// dense zone does not blow out push quota.
    /// </summary>
    public int MaxNotified { get; set; } = 50;

    /// <summary>
    /// AC: "Push sent to matched Jeebers within 2 seconds". The matching
    /// engine bounds the whole fan-out under this deadline; partial
    /// completion is reported back as the notified count.
    /// </summary>
    public TimeSpan PushFanoutSla { get; set; } = TimeSpan.FromSeconds(2);
}
