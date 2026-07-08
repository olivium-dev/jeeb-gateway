namespace JeebGateway.Ratings;

/// <summary>
/// Per-delivery rating state. Holds both sides' ratings plus the delivered-at
/// stamp used as the anchor for the 7-day rating window.
/// </summary>
public sealed class RatingPair
{
    public required string DeliveryId { get; init; }
    public required string ClientId { get; init; }
    public required string JeeberId { get; init; }
    public required DateTimeOffset DeliveredAt { get; init; }
    public RatingEntry? ClientRating { get; set; }
    public RatingEntry? JeeberRating { get; set; }
    public DateTimeOffset? RevealedAt { get; set; }
    public DateTimeOffset? WindowClosedAt { get; set; }
}

/// <summary>
/// Storage abstraction for the gateway's mutual-blind rating state.
///
/// MVP wiring is an in-memory <see cref="InMemoryRatingStore"/>. The optional
/// feedback-service-backed store (behind <c>FeatureFlags:UseUpstream:Ratings</c>)
/// is the upstream persistence path. The former score-taking-service client was
/// removed entirely (owner directive) and is no longer referenced.
/// </summary>
public interface IRatingStore
{
    /// <summary>
    /// Looks up the rating row for <paramref name="deliveryId"/>, or null
    /// when no rating has been submitted yet AND the row hasn't been
    /// initialised. Callers that want to seed the row should use
    /// <see cref="EnsureAsync"/>.
    /// </summary>
    Task<RatingPair?> GetAsync(string deliveryId, CancellationToken ct);

    /// <summary>
    /// Idempotent seed: returns the existing row, or creates one with the
    /// supplied party ids and delivered-at stamp. <paramref name="deliveredAt"/>
    /// is captured on FIRST call only — subsequent calls keep the original
    /// stamp so the rating window anchor is stable.
    /// </summary>
    Task<RatingPair> EnsureAsync(
        string deliveryId,
        string clientId,
        string jeeberId,
        DateTimeOffset deliveredAt,
        CancellationToken ct);

    /// <summary>
    /// Atomically writes a rating for the given party. Throws
    /// <see cref="InvalidOperationException"/> if the party has already
    /// submitted — ratings are immutable once submitted.
    /// </summary>
    Task<RatingPair> SubmitAsync(
        string deliveryId,
        bool callerIsClient,
        RatingEntry entry,
        CancellationToken ct);
}
