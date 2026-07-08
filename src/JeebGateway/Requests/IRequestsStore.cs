using JeebGateway.Requests.OtpHandover;

namespace JeebGateway.Requests;

/// <summary>
/// Storage abstraction for delivery requests. The default in-memory
/// implementation is intended for early-MVP local runs and integration
/// tests; production wiring will hit Postgres directly using the schema
/// in db/migrations/0004.
///
/// The contract is intentionally minimal. The downstream delivery-service owns
/// the full state machine; historical active-request and active-delivery caps are
/// retained only as unreachable 409 plumbing when callers pass unlimited limits.
/// </summary>
public interface IRequestsStore
{
    /// <summary>
    /// Returns the number of requests for <paramref name="clientId"/>
    /// whose status is in <see cref="RequestStatus.ActiveStates"/>
    /// (anything before <c>delivered</c>). Retained for read/ops surfaces.
    /// </summary>
    Task<int> CountActiveForClientAsync(string clientId, CancellationToken ct);

    /// <summary>
    /// Persists a new request in the <c>pending</c> state.
    /// </summary>
    Task<DeliveryRequest> CreateAsync(CreateRequestInput input, CancellationToken ct);

    /// <summary>
    /// Atomic insert path. The historical BR-9 cap is retired, so gateway callers
    /// pass <see cref="int.MaxValue"/>; the exception shape remains for compatibility.
    /// </summary>
    Task<DeliveryRequest> TryCreateWithLimitAsync(
        CreateRequestInput input,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// Test/admin helper: forcibly set a request's status. Used by the
    /// integration tests to flip a request out of an active state.
    ///
    /// Returns false (and leaves the row untouched) when the request is
    /// already in a terminal state — terminal rows are immutable so the
    /// expiry guarantee from T-backend-028 ("expired requests cannot
    /// receive new offers") cannot be undone by a stray transition.
    /// </summary>
    Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct);

    /// <summary>
    /// Stamps the winning <paramref name="jeeberId"/> onto the local request
    /// read-model row. This is the WRITE counterpart that makes
    /// <see cref="ListForJeeberAsync"/> able to surface an accepted delivery to
    /// the jeeber on the UPSTREAM accept path (FeatureFlags:UseUpstream:Offer=on):
    /// that path commits the single-winner transition in offer-service and only
    /// projects the STATUS locally (<see cref="SetStatusAsync"/>) — it never wrote
    /// the assignee, so the local row's <c>JeeberId</c> stayed null and the jeeber's
    /// Jobs/Deliveries list came back empty. The legacy in-memory accept path
    /// (<see cref="TryAcceptByJeeberAsync"/>) already stamped JeeberId; this exposes
    /// the same write to the upstream composer in the gateway BFF.
    ///
    /// <para>Guards: returns false (row untouched) when the request is unknown.
    /// Never clears an assignee — a null/blank <paramref name="jeeberId"/> is a no-op
    /// returning false, so a missing upstream actor id can never overwrite a
    /// previously-resolved jeeber. Idempotent: re-stamping the same id is a no-op
    /// success.</para>
    /// </summary>
    Task<bool> SetJeeberIdAsync(string requestId, string jeeberId, CancellationToken ct);

    /// <summary>
    /// fix/client-visibility (run-22 P1): stamps the ACCEPTED offer's fee onto the
    /// local request/delivery row at accept time (see
    /// <see cref="DeliveryRequest.AcceptedFee"/>). Write counterpart of the
    /// delivery-read <c>amount</c> enrichment: the receipt read (which happens
    /// AFTER the delivery goes terminal, and possibly by the non-owner party)
    /// falls back to this snapshot when the live offers-store lookup cannot
    /// resolve the accepted offer. Returns false (row untouched) when the request
    /// is unknown or <paramref name="fee"/> is not positive; idempotent —
    /// re-stamping overwrites with the same accepted fee.
    /// </summary>
    Task<bool> TrySetAcceptedFeeAsync(string requestId, decimal fee, CancellationToken ct);

    /// <summary>
    /// Returns every request whose creation timestamp is at or before
    /// <paramref name="cutoff"/> and whose status is still in the
    /// pre-acceptance set (<c>pending</c>, <c>matched</c>). The
    /// <see cref="RequestExpirySweeper"/> uses this to find both the
    /// no-offer nudge candidates and the per-tier expiry candidates in a
    /// single scan.
    /// </summary>
    Task<IReadOnlyList<DeliveryRequest>> ListPendingCreatedAtOrBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken ct);

    /// <summary>
    /// Records that the no-offer prompt was sent for <paramref name="requestId"/>
    /// at <paramref name="at"/>. Returns true on first call, false on every
    /// subsequent call — the sweeper relies on this idempotence to avoid
    /// duplicate pushes.
    /// </summary>
    Task<bool> MarkNudgedAsync(string requestId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Atomically moves <paramref name="requestId"/> to <c>expired</c>
    /// when its current status is still pre-acceptance. Returns true when
    /// the transition occurred. Returns false when the request has
    /// already advanced to <c>accepted</c> (or beyond) or is already
    /// terminal — in either case the sweeper must NOT send the
    /// expired-notification.
    /// </summary>
    Task<bool> TryExpireAsync(string requestId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Replaces <c>ClientId</c> on every (non-active) request belonging
    /// to <paramref name="userId"/> with the deterministic pseudonym
    /// <paramref name="anonymizedHash"/>. Used by the account-deletion
    /// flow (T-backend-035) — order records are retained, but the
    /// foreign key to the user becomes a one-way hash.
    ///
    /// Returns the number of rows rewritten. Active requests are NOT
    /// rewritten; callers must ensure no active deliveries remain
    /// before invoking (the deletion store enforces this).
    /// </summary>
    Task<int> AnonymizeForClientAsync(string userId, string anonymizedHash, CancellationToken ct);

    /// <summary>
    /// Returns every request still in <see cref="RequestStatus.Scheduled"/>
    /// whose <c>ScheduledAt</c> falls at or before <paramref name="cutoff"/>.
    /// The <c>ScheduledDeliveryActivator</c> uses this to find rows whose
    /// matching window has opened (cutoff = now + MatchingBuffer).
    /// </summary>
    Task<IReadOnlyList<DeliveryRequest>> ListScheduledDueAsync(
        DateTimeOffset cutoff,
        CancellationToken ct);

    /// <summary>
    /// Atomically transitions <paramref name="requestId"/> from
    /// <see cref="RequestStatus.Scheduled"/> to <see cref="RequestStatus.Pending"/>
    /// and stamps <c>ActivatedAt</c>. Returns true exactly once per request.
    /// Returns false when the request has already been activated, cancelled
    /// before its window, or otherwise advanced out of <c>scheduled</c> — in
    /// every false case the activator must NOT fire the reminder so the
    /// Client doesn't receive a duplicate push if the activator runs at a
    /// high cadence.
    /// </summary>
    Task<bool> TryActivateScheduledAsync(string requestId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Single-row lookup used by the cancellation endpoint. Returns null
    /// when the id is unknown.
    /// </summary>
    Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct);

    /// <summary>
    /// Owner-scoped read of every request belonging to
    /// <paramref name="clientId"/>, oldest first. Backs the additive
    /// <c>GET /requests</c> list endpoint so a Client can see their own
    /// order history (active and terminal). Returns an empty list — never
    /// null — when the Client has no requests. The production Postgres
    /// store replaces this with a "WHERE client_id = ?" query; the BFF
    /// migration target (NSwag client over delivery-service) implements
    /// the same shape so the controller stays storage-agnostic.
    /// </summary>
    Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct);

    /// <summary>
    /// Jeeber-scoped read of every request this jeeber is the assigned
    /// <see cref="DeliveryRequest.JeeberId"/> on, newest-relevant first. The
    /// dual of <see cref="ListForClientAsync"/> for the driver side: backs the
    /// jeeber Orders/Jobs surfaces (<c>GET /v1/deliveries</c> and
    /// <c>GET /v1/requests?role=jeeber</c>), which previously had no way to
    /// resolve the rows a jeeber accepted (the accepted row's ClientId is the
    /// requesting client, never the jeeber, so the client-scoped read never
    /// contained it). Returns an empty list — never null — when the jeeber has
    /// no assigned deliveries. The production Postgres store replaces this with
    /// a "WHERE jeeber_id = ?" query; the BFF migration target implements the
    /// same shape so the controller stays storage-agnostic.
    /// </summary>
    Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct);

    /// <summary>
    /// Resolves the request whose chat <c>ConversationId</c> equals
    /// <paramref name="conversationId"/>, or null when none matches.
    ///
    /// <para>Backs the chat-message → push-notification trigger: the chat send
    /// handlers are keyed by conversationId, but the chat client exposes no
    /// participant-roster read by conversationId (only by correlation key ==
    /// requestId). The gateway already owns the two delivery principals on the
    /// request row — <c>ClientId</c> (the requester) and <c>JeeberId</c> (the
    /// awarded jeeber, set post-accept) — and stamps the conversation id onto the
    /// row at create / accept. This lookup recovers those principals so a new chat
    /// message can be pushed to the other party (A→B). It surfaces only the two
    /// delivery principals, not the full chat-service participant set (broadcasting
    /// offerers) which remains chat-service's authority — a follow-up.</para>
    /// </summary>
    Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct);

    /// <summary>
    /// Counts the requests where
    /// <see cref="DeliveryRequest.JeeberId"/> equals <paramref name="jeeberId"/>
    /// and the status is in <see cref="RequestStatus.JeeberActiveStates"/>
    /// (accepted / picked_up / heading_off). Surfaced on the Jeeber profile
    /// for ops triage.
    /// </summary>
    Task<int> CountActiveForJeeberAsync(string jeeberId, CancellationToken ct);

    /// <summary>
    /// Atomic accept: under the store's write lock, validates the request is still
    /// in <see cref="RequestStatus.PreAcceptanceStates"/> and transitions the row to
    /// <see cref="RequestStatus.Accepted"/> while stamping
    /// <see cref="DeliveryRequest.JeeberId"/> and
    /// <see cref="DeliveryRequest.AcceptedAt"/>.
    ///
    /// The historical active-delivery cap is retired, so gateway callers pass
    /// <see cref="int.MaxValue"/>. Returns null when the request is unknown.
    /// Returns the updated request otherwise.
    /// Throws <see cref="RequestNotAcceptableException"/> when the
    /// request is no longer in a pre-acceptance state (already accepted
    /// by someone else, cancelled, expired, …) — the caller maps this
    /// to a 409 distinct from other accept conflicts.
    /// </summary>
    Task<DeliveryRequest?> TryAcceptByJeeberAsync(
        string requestId,
        string jeeberId,
        int limit,
        DateTimeOffset at,
        CancellationToken ct);

    // JEB-1479 cut-over: the legacy single-step delivery-transition path
    // (TryTransitionAsync + the retired linear state-machine guard) was
    // removed. The canonical state machine is owned by delivery-service; the
    // gateway forwards PATCH /deliveries/{id}/status to its
    // POST /api/v1/deliveries/{id}/transition contract via the NSwag-generated
    // IDeliveryServiceClient. This store keeps backing Requests/Offers/
    // RequestOffers/Disputes/Ratings/Settlement/Cancellation/OtpHandover.

    /// <summary>
    /// T-backend-024 (JEEB-42): atomic cancellation. Under the store's
    /// write lock, validates that the row is in <paramref name="allowedFromStates"/>
    /// and then either lands it directly on <paramref name="targetStatus"/>
    /// (immediate cancel) or parks it on <see cref="RequestStatus.CancellationRequested"/>
    /// (admin-approval queue). Stamps the cancellation audit fields in the
    /// same critical section so a racing accept / status PATCH cannot land
    /// between the guard read and the write.
    ///
    /// Returns null when the id is unknown. Returns the cancellation
    /// result otherwise; <see cref="CancellationStoreOutcome.NotCancellable"/>
    /// when the row is no longer in an allowed state.
    /// </summary>
    Task<CancellationStoreResult?> TryCancelAsync(
        string requestId,
        IReadOnlySet<string> allowedFromStates,
        string targetStatus,
        string cancelledBy,
        string? reason,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>
    /// T-backend-024 admin decision. Approves or rejects a row currently
    /// in <see cref="RequestStatus.CancellationRequested"/>:
    /// <list type="bullet">
    ///   <item>approve → row transitions to <see cref="RequestStatus.Cancelled"/>.</item>
    ///   <item>reject  → row reverts to <see cref="DeliveryRequest.CancellationPreviousStatus"/>.</item>
    /// </list>
    /// Returns null when the id is unknown or the row is not in
    /// <see cref="RequestStatus.CancellationRequested"/>.
    /// </summary>
    Task<CancellationStoreResult?> TryDecideCancellationAsync(
        string requestId,
        bool approve,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>
    /// T-backend-024: paginated list of rows currently parked in
    /// <see cref="RequestStatus.CancellationRequested"/>, oldest first.
    /// </summary>
    Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingCancellationsAsync(
        int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// T-backend-024: returns every row a particular Jeeber cancelled,
    /// regardless of when. The cancellation service uses this to compute
    /// the rolling-7d count and the lifetime cancellation rate.
    /// </summary>
    Task<IReadOnlyList<DeliveryRequest>> ListJeeberCancelledAsync(
        string jeeberId, CancellationToken ct);

    /// <summary>
    /// Hand-off OTP verification (T-backend-015 / JEEB-33). Under the
    /// store's write lock the row's OTP attempt counter is incremented
    /// (on mismatch) or the row is flipped to <see cref="RequestStatus.Delivered"/>
    /// (on match). The <paramref name="maxAttempts"/> ceiling is passed
    /// in by the controller so the policy stays in
    /// <see cref="OtpHandoverOptions"/>.
    ///
    /// <list type="bullet">
    ///   <item>Returns <see cref="OtpVerificationOutcome.NotFound"/> when
    ///     <paramref name="requestId"/> is unknown.</item>
    ///   <item>Returns <see cref="OtpVerificationOutcome.NotInHandoverState"/>
    ///     when the row is not in <see cref="RequestStatus.HeadingOff"/> —
    ///     the OTP is only verifiable at the documented handover step.</item>
    ///   <item>Returns <see cref="OtpVerificationOutcome.Locked"/> when the
    ///     row was already locked OR when this call is the one that hit
    ///     <paramref name="maxAttempts"/>. The
    ///     <c>OtpVerificationResult.JustLockedOut</c> flag distinguishes
    ///     the two so the controller creates the escalation exactly once.</item>
    ///   <item>Returns <see cref="OtpVerificationOutcome.Mismatch"/> with
    ///     the remaining attempt budget on a normal wrong-OTP attempt.</item>
    ///   <item>Returns <see cref="OtpVerificationOutcome.Verified"/> with
    ///     the updated row on success — status is now
    ///     <see cref="RequestStatus.Delivered"/>.</item>
    /// </list>
    /// </summary>
    Task<OtpVerificationResult> TryVerifyOtpAsync(
        string requestId,
        string otpCode,
        int maxAttempts,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>
    /// Records that the Jeeber flagged the Client as unreachable at
    /// drop-off (T-backend-015 step 6). Idempotent — calling twice
    /// preserves the original timestamp so the 15-min sweeper window
    /// starts from the first flag, not the most recent one. Returns
    /// the updated row, or null when the id is unknown / the row is in
    /// a terminal state.
    /// </summary>
    Task<DeliveryRequest?> MarkClientUnreachableAsync(
        string requestId,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>
    /// Returns every still-active delivery whose
    /// <see cref="DeliveryRequest.ClientUnreachableAt"/> is at or before
    /// <paramref name="cutoff"/> and that has not yet been escalated.
    /// The <c>OtpHandoverSweeper</c> uses this to find rows past the
    /// 15-min unreachable window.
    /// </summary>
    Task<IReadOnlyList<DeliveryRequest>> ListUnreachableAtOrBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken ct);

    /// <summary>
    /// Atomically writes <paramref name="escalationId"/> onto
    /// <see cref="DeliveryRequest.OtpEscalationId"/> when the row has
    /// not already been escalated. Returns true on first set, false
    /// when the row was already escalated — the false case lets the
    /// sweeper skip without creating a duplicate escalation row.
    /// </summary>
    Task<bool> TrySetEscalationIdAsync(
        string requestId,
        string escalationId,
        CancellationToken ct);
}

/// <summary>
/// Outcome of <see cref="IRequestsStore.TryCancelAsync"/> /
/// <see cref="IRequestsStore.TryDecideCancellationAsync"/>.
/// </summary>
public enum CancellationStoreOutcome
{
    Committed,
    NotCancellable,
}

/// <summary>
/// Result bundle returned from store-level cancellation operations.
/// </summary>
public sealed record CancellationStoreResult(
    CancellationStoreOutcome Outcome,
    DeliveryRequest Request,
    string PreviousStatus);

public class CreateRequestInput
{
    public required string ClientId { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// S04 (JEB-1431): optional caller-supplied row id (the voice-create requestId
    /// idempotency anchor). When null the store mints a fresh GUID (existing
    /// behaviour). When set, the row is keyed by this id so the voice read-back
    /// (GET /v1/requests/{requestId}) resolves and idempotent re-submits collapse
    /// onto the same row. Additive — never required.
    /// </summary>
    public string? Id { get; init; }
    public string? Transcription { get; init; }

    /// <summary>
    /// S04 (JEB-43): STT confidence (0..1) captured at voice-create so the
    /// read-back (H2) echoes the exact value the create returned. Null on the
    /// typed-text path. Additive — never required.
    /// </summary>
    public double? TranscriptionConfidence { get; init; }
    public string? AudioUrl { get; init; }
    public IReadOnlyList<string> Photos { get; init; } = Array.Empty<string>();
    public string? TierId { get; init; }
    public GeoPoint? PickupLocation { get; init; }
    public GeoPoint? DropoffLocation { get; init; }
    public string? PickupAddress { get; init; }
    public string? DropoffAddress { get; init; }

    /// <summary>
    /// T-BE-019 (JEB-55): E.164 phone number to deliver the 4-digit
    /// handover OTP to. Optional at creation; the OTP trigger endpoint
    /// rejects with 400 when this is missing.
    /// </summary>
    public string? RecipientPhone { get; init; }

    /// <summary>
    /// Future delivery moment for T-backend-046. Null = immediate delivery
    /// (status starts as <see cref="RequestStatus.Pending"/>). When set, the
    /// store initialises the row with status <see cref="RequestStatus.Scheduled"/>;
    /// the <c>ScheduledDeliveryActivator</c> flips it to <c>pending</c> at
    /// <c>ScheduledAt - MatchingBuffer</c>.
    /// </summary>
    public DateTimeOffset? ScheduledAt { get; init; }
}

/// <summary>
/// Thrown by the controller (and surfaced as ProblemDetails) when the
/// client has already reached the retired BR-9 concurrency cap. The
/// <see cref="ActiveCount"/> field is included in the response detail
/// so dashboards / mobile clients can show the exact value.
/// </summary>
public class TooManyActiveRequestsException : Exception
{
    public int ActiveCount { get; }
    public int Limit { get; }

    public TooManyActiveRequestsException(int activeCount, int limit)
        : base($"Client already has {activeCount} active requests (limit {limit}).")
    {
        ActiveCount = activeCount;
        Limit = limit;
    }
}

/// <summary>
/// Retired BR-10 active-delivery cap: the offer-accept endpoint surfaces this as
/// ProblemDetails with HTTP 409. <see cref="ActiveCount"/> mirrors the
/// shape of <see cref="TooManyActiveRequestsException"/> so the mobile
/// app can render the exact value in the error banner.
/// </summary>
public class TooManyActiveDeliveriesException : Exception
{
    public int ActiveCount { get; }
    public int Limit { get; }

    public TooManyActiveDeliveriesException(int activeCount, int limit)
        : base($"Jeeber already has {activeCount} active deliveries (limit {limit}).")
    {
        ActiveCount = activeCount;
        Limit = limit;
    }
}

/// <summary>
/// Thrown by <see cref="IRequestsStore.TryAcceptByJeeberAsync"/> when the
/// request has moved out of the pre-acceptance set before the accept
/// landed (race with another Jeeber, with the expiry sweeper, or with
/// the Client's cancel). Distinct from <see cref="TooManyActiveDeliveriesException"/>
/// so the controller can return a different ProblemDetails type.
/// </summary>
public class RequestNotAcceptableException : Exception
{
    public string CurrentStatus { get; }

    public RequestNotAcceptableException(string currentStatus)
        : base($"Request is not in a pre-acceptance state (current={currentStatus}).")
    {
        CurrentStatus = currentStatus;
    }
}
