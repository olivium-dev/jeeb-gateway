namespace JeebGateway.Availability;

/// <summary>
/// Lifecycle states for an offer in the reverse auction (SM-2). An offer moves
/// from <see cref="Pending"/> exactly once — to one of:
/// <list type="bullet">
///   <item><see cref="Accepted"/> — the request-owning Client awarded the
///     delivery to this bid (the auction's single winner).</item>
///   <item><see cref="Superseded"/> — a COMPETING bid on the SAME request lost
///     the auction because a different offer was accepted (ACC-02: "accepting an
///     offer marks all OTHER offers <c>superseded</c>"). This is distinct from
///     <see cref="Withdrawn"/>: the Jeeber did not retract it; the request closed
///     around a different winner. The mobile app renders a "not selected" banner
///     for superseded, vs "you withdrew this" for withdrawn.</item>
///   <item><see cref="Withdrawn"/> — the auto-offline sweeper, a same-Jeeber
///     sibling-commit, or the Jeeber's own DELETE retracted the bid.</item>
/// </list>
/// </summary>
public static class PendingOfferStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";

    /// <summary>
    /// SM-2 / ACC-02 — a competing offer on the same request that lost the
    /// auction because another offer was accepted. Terminal, like
    /// <see cref="Withdrawn"/>, but carries the distinct "not selected" meaning.
    /// </summary>
    public const string Superseded = "superseded";

    public const string Withdrawn = "withdrawn";
}

/// <summary>
/// MVP shape of an offer record. The downstream offer-service owns the
/// canonical model — the gateway keeps the bid fields (<see cref="Fee"/>,
/// <see cref="EtaMinutes"/>, <see cref="Note"/>) so the reverse-auction
/// flow (T-backend-010, FR-6.*) can run end-to-end while the offer-service
/// is being built out.
/// </summary>
public class PendingOffer
{
    public required string Id { get; init; }
    public required string RequestId { get; init; }
    public required string JeeberId { get; init; }
    public required string Status { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Quoted fee the Jeeber asks the Client to pay (gross). Must be
    /// strictly positive; the controller enforces the MVP floor of
    /// $1 (BR / T-backend-010 acceptance criteria) and the DB CHECK
    /// in 0007_init_offers enforces <c>fee &gt; 0</c>.
    /// </summary>
    public decimal Fee { get; set; }

    /// <summary>
    /// Quoted pickup-to-dropoff estimate in minutes (FR-6.2). Must be
    /// strictly positive — mirrors the DB CHECK <c>eta_minutes &gt; 0</c>.
    /// Mutable so an in-memory edit (SM-2 amend) can revise the ETA in place.
    /// </summary>
    public int EtaMinutes { get; set; }

    /// <summary>
    /// Optional free-text message from the Jeeber to the Client. Mutable so an
    /// in-memory edit (SM-2 / OFF-04 amend) can revise the note in place; capped
    /// at 500 chars to match the DB CHECK in 0007.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// SM-2 / JEB-1474 — number of edits applied to this bid. The product cap is
    /// 2 edits (<c>OffersController.OfferEditCap</c>); the 3rd edit attempt is
    /// rejected with <c>422 edit_limit_reached</c>. Counted in the in-memory
    /// store under the write lock so concurrent edits cannot both slip past the
    /// cap. The upstream offer-service owns its own counter when the Offer flag
    /// is on; this field backs the flag-OFF in-memory edit path.
    /// </summary>
    public int EditCount { get; set; }
}
