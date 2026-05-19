namespace JeebGateway.Ratings;

/// <summary>
/// Per-delivery rating state. Holds both sides' ratings plus the delivered-at
/// stamp used as the anchor for the 7-day rating window.
///
/// <para><see cref="AutoRevealedAt"/> is stamped by the T-BE-025 / JEB-61 cron
/// the first time a delivery transitions out of <c>pending_*</c> after the
/// blind window has elapsed without both sides submitting. The stamp acts as
/// the idempotency key: a second cron pass over the same row finds
/// <see cref="AutoRevealedAt"/> already set and skips the flip and the
/// notification. Once stamped, both parties (whichever sides actually
/// submitted) become visible — the missing side's <see cref="RatingEntry"/>
/// stays null so no synthetic score is ever invented.</para>
/// </summary>
public sealed class RatingPair
{
    public required string DeliveryId { get; init; }
    public required string ClientId { get; init; }
    public required string JeeberId { get; init; }
    public required DateTimeOffset DeliveredAt { get; init; }
    public RatingEntry? ClientRating { get; set; }
    public RatingEntry? JeeberRating { get; set; }
    public DateTimeOffset? AutoRevealedAt { get; set; }
}

/// <summary>
/// Storage abstraction for the gateway's mutual-blind rating state.
///
/// MVP wiring is an in-memory <see cref="InMemoryRatingStore"/>. Production
/// wiring will hit Postgres directly (one row per delivery, two nullable
/// jsonb columns for the two sides) and the
/// <see cref="JeebGateway.Services.Clients.IScoreServiceClient"/> remains
/// authoritative for the canonical rating storage.
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

    /// <summary>
    /// T-BE-025 / JEB-61 — enumerates rating pairs eligible for auto-reveal,
    /// i.e. rows that are not already auto-revealed, that have not yet had
    /// both sides submit, AND whose blind window
    /// (<see cref="RatingPair.DeliveredAt"/> + 7 days) closed at or before
    /// <paramref name="asOf"/>. Returns a stable snapshot so the cron can
    /// iterate without observing concurrent submissions.
    /// </summary>
    Task<IReadOnlyList<RatingPair>> ListPendingAutoRevealAsync(
        DateTimeOffset asOf,
        TimeSpan ratingWindow,
        CancellationToken ct);

    /// <summary>
    /// T-BE-025 / JEB-61 — atomically stamps <see cref="RatingPair.AutoRevealedAt"/>
    /// if it is still null. Returns <c>true</c> on first stamp; subsequent
    /// calls (including from a second cron pass) return <c>false</c> without
    /// mutating state. This is the idempotency contract that protects AC2
    /// ("no double reveal").
    /// </summary>
    Task<bool> TryMarkAutoRevealedAsync(
        string deliveryId,
        DateTimeOffset at,
        CancellationToken ct);
}
