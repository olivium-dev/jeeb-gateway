namespace JeebGateway.Availability;

/// <summary>
/// Abstraction for the offer-service's offer ledger. Callers:
///
/// 1. The auto-offline sweeper (T-backend-023). When a Jeeber goes offline
///    we withdraw any in-flight offers so matching does not keep waiting
///    for a response from someone unreachable.
/// 2. The offer-accept endpoint (T-backend-039, BR-10). The Jeeber
///    accepts a specific offer they were extended; the gateway needs to
///    resolve <c>offerId → (jeeberId, requestId)</c> to authorize the
///    caller and bind the request to the Jeeber.
/// 3. The offer-submission endpoints (T-backend-010, FR-6.*). The Jeeber
///    submits a bid (<see cref="TrySubmitAsync"/>) and may retract it
///    before acceptance (<see cref="TryWithdrawAsync"/>). The gateway
///    enforces "max 20 offers per request" and "one live offer per Jeeber
///    per request" inside the store so the check and the write cannot race.
/// </summary>
public interface IPendingOffersStore
{
    /// <summary>
    /// Withdraws every <see cref="PendingOfferStatus.Pending"/> offer for
    /// the given Jeeber (auto-offline path). Already-accepted offers are
    /// not touched. Returns the number of offers transitioned.
    /// </summary>
    Task<int> WithdrawForJeeberAsync(string jeeberId, CancellationToken ct);

    /// <summary>
    /// Single-offer lookup used by the accept endpoint. Returns null when
    /// the offer is unknown.
    /// </summary>
    Task<PendingOffer?> GetAsync(string offerId, CancellationToken ct);

    /// <summary>
    /// Marks <paramref name="offerId"/> accepted and withdraws every other
    /// pending offer the Jeeber held — once a Jeeber commits to a request,
    /// their other in-flight offers become stale. Returns false when the
    /// offer is unknown or no longer in <see cref="PendingOfferStatus.Pending"/>.
    /// </summary>
    Task<bool> AcceptAsync(string offerId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// SM-2 / ACC-02 accept-and-supersede (in-memory auction-close authority).
    /// Marks <paramref name="offerId"/> accepted and, in the SAME critical
    /// section, marks every OTHER pending offer on the SAME request
    /// <see cref="PendingOfferStatus.Superseded"/> (the competing bids that lost
    /// the auction). This is the request-scoped close — distinct from
    /// <see cref="AcceptAsync"/>, which only retracts the winning Jeeber's own
    /// siblings. Idempotency / re-accept: when the offer is already accepted, the
    /// outcome is <see cref="AcceptOfferStatus.AlreadyAccepted"/> carrying the
    /// winning offer's Jeeber id (the controller maps this to <c>409
    /// already_accepted</c> with the winner). When a DIFFERENT offer on the
    /// request already won, this offer reads <see cref="PendingOfferStatus.Superseded"/>
    /// and the outcome is likewise <see cref="AcceptOfferStatus.AlreadyAccepted"/>
    /// with the request's winner. Returns <see cref="AcceptOfferStatus.NotFound"/>
    /// for an unknown offer and <see cref="AcceptOfferStatus.NotPending"/> for a
    /// withdrawn one.
    /// </summary>
    Task<AcceptOfferOutcome> AcceptWithSupersedeAsync(
        string offerId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// SM-2 / JEB-1474 in-memory offer edit with the 2-edit cap. Applies the
    /// supplied non-null fields (<paramref name="fee"/> / <paramref name="etaMinutes"/>
    /// / <paramref name="note"/>) to a pending offer owned by
    /// <paramref name="jeeberId"/>, incrementing the edit counter under the write
    /// lock. The 3rd edit attempt (count already at <paramref name="maxEdits"/>)
    /// is rejected with <see cref="EditOfferStatus.EditLimitReached"/> (controller
    /// → <c>422 edit_limit_reached</c>) WITHOUT mutating the offer. Other
    /// outcomes: <see cref="EditOfferStatus.NotFound"/> (unknown id / wrong
    /// request), <see cref="EditOfferStatus.NotOwned"/> (a different Jeeber),
    /// <see cref="EditOfferStatus.NotPending"/> (accepted/withdrawn/superseded).
    /// </summary>
    Task<EditOfferOutcome> TryEditAsync(
        string offerId,
        string requestId,
        string jeeberId,
        decimal? fee,
        int? etaMinutes,
        string? note,
        int maxEdits,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>
    /// Atomic submit (T-backend-010). Under the store's write lock the
    /// store checks:
    /// <list type="bullet">
    ///   <item>The request does not already hold <paramref name="maxPerRequest"/>
    ///     live (<see cref="PendingOfferStatus.Pending"/>) offers — throws
    ///     <see cref="TooManyOffersForRequestException"/> if so.</item>
    ///   <item>This <paramref name="jeeberId"/> does not already have a live
    ///     offer on this request — throws <see cref="DuplicateOfferException"/>
    ///     if so. A previously <see cref="PendingOfferStatus.Withdrawn"/> offer
    ///     does NOT block re-submission (acceptance criterion: withdraw+re-offer).</item>
    /// </list>
    /// Returns the new offer in the <see cref="PendingOfferStatus.Pending"/> state.
    ///
    /// <para><paramref name="clientId"/> is the request creator's id, threaded
    /// through purely so the upstream-backed store
    /// (<see cref="UpstreamPendingOffersStore"/>) can mirror the request into
    /// offer-service (OS-1) when the submit 404s because the request row was
    /// never mirrored. It is optional and ignored by the in-memory store; when
    /// null the upstream store cannot self-heal a missing mirror and the 404
    /// surfaces unchanged.</para>
    /// </summary>
    Task<PendingOffer> TrySubmitAsync(
        string requestId,
        string jeeberId,
        decimal fee,
        int etaMinutes,
        string? note,
        int maxPerRequest,
        DateTimeOffset at,
        CancellationToken ct,
        string? clientId = null);

    /// <summary>
    /// Atomic withdraw (T-backend-010). Only the owning Jeeber may retract
    /// the offer, and only while it is still <see cref="PendingOfferStatus.Pending"/>.
    /// Returns <see cref="WithdrawOfferOutcome.Withdrawn"/> on success.
    /// Returns <see cref="WithdrawOfferOutcome.NotFound"/> when the id is
    /// unknown (or the offer belongs to a different request than the one
    /// in the URL — keeps the surface 404-clean for callers).
    /// Returns <see cref="WithdrawOfferOutcome.NotOwned"/> when a different
    /// Jeeber tries to withdraw the offer (controller maps to 403).
    /// Returns <see cref="WithdrawOfferOutcome.NotPending"/> when the offer
    /// has already moved to accepted / withdrawn (controller maps to 409).
    /// </summary>
    Task<WithdrawOfferOutcome> TryWithdrawAsync(
        string offerId,
        string requestId,
        string jeeberId,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>
    /// Returns every offer (any status) attached to <paramref name="requestId"/>,
    /// newest-first. Used by integration tests and the future Client
    /// "see all bids" listing endpoint.
    /// </summary>
    Task<IReadOnlyList<PendingOffer>> ListForRequestAsync(
        string requestId, CancellationToken ct);

    /// <summary>
    /// fix/offer-visibility (run-23 CHECK C) — every offer <paramref name="jeeberId"/>
    /// has submitted, in ANY status (pending / accepted / superseded / withdrawn),
    /// newest-first. This is the jeeber "my-offers" read: after the customer accepts a
    /// competing bid, the losing jeeber's own offer MUST stay visible in its terminal
    /// state — a list that silently drops terminal rows makes the jeeber's bid vanish
    /// (the run-23 defect this fixes).
    ///
    /// <para>NON-BREAKING EXTENSION (same pattern as <see cref="ExpireForRequestAsync"/>):
    /// a default interface method so existing implementers / fakes compile unchanged —
    /// they inherit the safe empty list. The in-memory store overrides it with a full
    /// any-status scan; the upstream (offer-service) store overrides it with the
    /// routing-index + owner-scoped request-list composition (offer-service exposes no
    /// jeeber-scoped list route).</para>
    ///
    /// <para>DEGRADE-DON'T-FAIL: an upstream blip yields the offers that could be
    /// resolved (possibly none), never a 5xx.</para>
    /// </summary>
    Task<IReadOnlyList<PendingOffer>> ListForJeeberAsync(
        string jeeberId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PendingOffer>>(Array.Empty<PendingOffer>());

    /// <summary>
    /// T-backend-028 follow-up — close every still-live (<see cref="PendingOfferStatus.Pending"/>)
    /// offer on <paramref name="requestId"/> when the request itself reaches a terminal
    /// state (expiry). Once the request can no longer be accepted, its outstanding bids
    /// are stale: leaving them <c>pending</c> lets a jeeber keep believing their bid is
    /// live and lets a late accept race a dead request. Each such offer is transitioned to
    /// <see cref="PendingOfferStatus.Superseded"/> ("not selected" — the request closed
    /// around no winner), which the mobile app already renders; no new offer status is
    /// introduced. Returns the number of offers transitioned.
    ///
    /// <para>NON-BREAKING EXTENSION (mirrors <see cref="ListOffersForJeeberAsync"/> on the
    /// offer-service client): a default interface method so existing implementers / fakes
    /// compile unchanged — they inherit the safe 0 no-op. The in-memory store overrides it;
    /// the upstream (offer-service) store inherits the no-op because offer-service owns its
    /// own request lifecycle and expires its offers server-side when its mirrored request
    /// row expires — the gateway must not double-drive that transition over the wire.</para>
    ///
    /// <para>DEGRADE-DON'T-FAIL: callers invoke this best-effort AFTER the request is
    /// already durably expired; a failure here must never undo the expiry.</para>
    /// </summary>
    Task<int> ExpireForRequestAsync(string requestId, DateTimeOffset at, CancellationToken ct)
        => Task.FromResult(0);
}

/// <summary>
/// Outcomes for <see cref="IPendingOffersStore.TryWithdrawAsync"/>. The
/// controller maps each to a distinct HTTP status / ProblemDetails type
/// so the mobile app can render the right banner.
/// </summary>
public enum WithdrawOfferOutcome
{
    Withdrawn,
    NotFound,
    NotOwned,
    NotPending
}

/// <summary>
/// SM-2 accept terminal classification for
/// <see cref="IPendingOffersStore.AcceptWithSupersedeAsync"/>.
/// </summary>
public enum AcceptOfferStatus
{
    /// <summary>This offer won; all competing offers were superseded.</summary>
    Accepted,

    /// <summary>
    /// The request's auction is already closed (this or another offer won). The
    /// controller maps this to <c>409 already_accepted</c> and surfaces the
    /// winning Jeeber id (<see cref="AcceptOfferOutcome.WinnerJeeberId"/>).
    /// </summary>
    AlreadyAccepted,

    /// <summary>Unknown offer id.</summary>
    NotFound,

    /// <summary>
    /// The offer itself is no longer pending for a reason other than an accepted
    /// winner on the request (e.g. the Jeeber withdrew it). Controller → 409.
    /// </summary>
    NotPending
}

/// <summary>
/// Result of <see cref="IPendingOffersStore.AcceptWithSupersedeAsync"/>. Carries
/// the winning Jeeber id so the controller can return it on re-accept (the SM-2
/// contract: re-accept → <c>409 already_accepted</c> returning the winner).
/// </summary>
public readonly record struct AcceptOfferOutcome(
    AcceptOfferStatus Status,
    string? WinnerJeeberId,
    int SupersededCount)
{
    public static AcceptOfferOutcome Accepted(string winnerJeeberId, int supersededCount)
        => new(AcceptOfferStatus.Accepted, winnerJeeberId, supersededCount);

    public static AcceptOfferOutcome AlreadyAccepted(string? winnerJeeberId)
        => new(AcceptOfferStatus.AlreadyAccepted, winnerJeeberId, 0);

    public static readonly AcceptOfferOutcome NotFound =
        new(AcceptOfferStatus.NotFound, null, 0);

    public static readonly AcceptOfferOutcome NotPending =
        new(AcceptOfferStatus.NotPending, null, 0);
}

/// <summary>
/// SM-2 edit terminal classification for
/// <see cref="IPendingOffersStore.TryEditAsync"/>.
/// </summary>
public enum EditOfferStatus
{
    Edited,

    /// <summary>The 2-edit cap was already reached. Controller → 422 edit_limit_reached.</summary>
    EditLimitReached,

    NotFound,
    NotOwned,
    NotPending
}

/// <summary>
/// Result of <see cref="IPendingOffersStore.TryEditAsync"/>. On
/// <see cref="EditOfferStatus.Edited"/> the mutated <see cref="Offer"/> is
/// returned for the controller's 200 projection; every negative leaves it null.
/// </summary>
public readonly record struct EditOfferOutcome(EditOfferStatus Status, PendingOffer? Offer)
{
    public static EditOfferOutcome Edited(PendingOffer offer)
        => new(EditOfferStatus.Edited, offer);

    public static readonly EditOfferOutcome EditLimitReached =
        new(EditOfferStatus.EditLimitReached, null);

    public static readonly EditOfferOutcome NotFound =
        new(EditOfferStatus.NotFound, null);

    public static readonly EditOfferOutcome NotOwned =
        new(EditOfferStatus.NotOwned, null);

    public static readonly EditOfferOutcome NotPending =
        new(EditOfferStatus.NotPending, null);
}

/// <summary>
/// T-backend-010 acceptance criterion: a request may hold at most 20 live
/// offers. The controller maps this to 409 ProblemDetails.
/// </summary>
public class TooManyOffersForRequestException : Exception
{
    public string RequestId { get; }
    public int LiveCount { get; }
    public int Limit { get; }

    public TooManyOffersForRequestException(string requestId, int liveCount, int limit)
        : base($"Request '{requestId}' already has {liveCount} live offers (limit {limit}).")
    {
        RequestId = requestId;
        LiveCount = liveCount;
        Limit = limit;
    }
}

/// <summary>
/// T-backend-010 acceptance criterion: at most one live offer per Jeeber
/// per request. Mirrors the partial unique index
/// <c>offers_request_jeeber_uniq</c> in db/migrations/0007 (the DB version
/// applies to all rows; the gateway tolerates re-submission once the prior
/// offer was withdrawn so the mobile app can correct a fat-finger bid).
/// </summary>
public class DuplicateOfferException : Exception
{
    public string RequestId { get; }
    public string JeeberId { get; }
    public string ExistingOfferId { get; }

    public DuplicateOfferException(string requestId, string jeeberId, string existingOfferId)
        : base($"Jeeber '{jeeberId}' already has a live offer on request '{requestId}' (offer '{existingOfferId}').")
    {
        RequestId = requestId;
        JeeberId = jeeberId;
        ExistingOfferId = existingOfferId;
    }
}

/// <summary>
/// sprint-009 Lane E — the request is not open for new offers (offer-service
/// <c>request_not_open</c>: the auction is already accepted, expired, or cancelled).
/// This is DISTINCT from the 20-offer cap (<see cref="TooManyOffersForRequestException"/>):
/// both surface as HTTP 409 upstream, but rendering the cap message for a closed auction
/// is misleading. The controller maps this to its own <c>request-not-open-for-offers</c>
/// ProblemDetails so the jeeber sees "the auction is closed", not "20-offer cap reached".
/// </summary>
public class RequestNotOpenForOffersException : Exception
{
    public string RequestId { get; }

    /// <summary>The offer-service error <c>code</c> that drove the classification, when present.</summary>
    public string? UpstreamCode { get; }

    public RequestNotOpenForOffersException(string requestId, string? upstreamCode)
        : base($"Request '{requestId}' is not open for new offers (upstream code '{upstreamCode ?? "request_not_open"}').")
    {
        RequestId = requestId;
        UpstreamCode = upstreamCode;
    }
}
