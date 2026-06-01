namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed client over the real offer-service (Elixir/Phoenix, host port 10063,
/// liveness <c>/health</c>). The offer-service is the canonical owner of the
/// reverse-auction offer ledger; the gateway proxies the offer record-of-truth
/// through this client when <c>FeatureFlags:UseUpstream:Offer</c> is on
/// (see <see cref="JeebGateway.Services.UpstreamFeatureFlags.Offer"/>).
///
/// Hand-coded (not NSwag-generated) because offer-service exposes no OpenAPI
/// document — <c>/swagger/v1/swagger.json</c> and <c>/openapi.json</c> both 404;
/// only <c>/health</c>, <c>/healthz</c>, <c>/readyz</c> and the four
/// <c>/api/v1/...</c> auction routes exist. The contract below was lifted
/// directly from <c>offer-service/lib/offer_service_web/router.ex</c> and
/// <c>controllers/offer_controller.ex</c>. Follows the
/// <see cref="NotificationServiceClient"/> hand-coded precedent.
///
/// Auth seam: offer-service trusts a gateway-injected <c>x-user-id</c> header
/// (its <c>AuthenticatedUser</c> plug), NOT the mobile JWT bearer. The acting
/// user id is therefore passed explicitly into every call and set on the
/// outbound request inside <see cref="OfferServiceClient"/>.
///
/// Wire shape (snake_case): the upstream serializes
/// <c>fee_cents</c> (integer cents), <c>eta_minutes</c>, <c>note</c>,
/// <c>status</c> (one of submitted / edited / withdrawn / accepted / rejected /
/// expired / pending), <c>request_id</c>, <c>jeeber_id</c>, <c>created_at</c>,
/// <c>updated_at</c>, <c>withdrawn_at</c>.
/// </summary>
public interface IOfferServiceClient
{
    /// <summary>
    /// POST /api/v1/requests/{requestId}/offers — submit a bid as
    /// <paramref name="actingUserId"/>. Returns the created offer on 201.
    /// Translates upstream conflict codes (<c>request_not_open</c>,
    /// duplicate-offer) into the same exceptions the in-memory store throws so
    /// the controller's catch blocks stay unchanged.
    /// </summary>
    Task<OfferWire> SubmitAsync(
        string actingUserId,
        string requestId,
        long feeCents,
        int etaMinutes,
        string? note,
        CancellationToken ct);

    /// <summary>
    /// DELETE /api/v1/requests/{requestId}/offers/{offerId} — withdraw a bid.
    /// Returns the withdraw outcome mapped from the upstream HTTP status:
    /// 200 → Withdrawn, 404 → NotFound, 403 → NotOwned, 409/410 → NotPending.
    /// </summary>
    Task<OfferWithdrawResult> WithdrawAsync(
        string actingUserId,
        string requestId,
        string offerId,
        CancellationToken ct);

    /// <summary>
    /// POST /api/v1/requests/{requestId}/offers/{offerId}/accept — atomic
    /// auction close. The <c>Idempotency-Key</c> header is mandatory upstream;
    /// callers supply <paramref name="idempotencyKey"/> (>= 8 chars). Returns
    /// the accept-result envelope on 200.
    /// </summary>
    Task<OfferAcceptWire> AcceptAsync(
        string actingUserId,
        string requestId,
        string offerId,
        string idempotencyKey,
        CancellationToken ct);
}

/// <summary>
/// Decoded offer-service offer record (cents-based). Mapped to the gateway's
/// dollar-based <see cref="JeebGateway.Availability.PendingOffer"/> by
/// <see cref="JeebGateway.Availability.UpstreamPendingOffersStore"/>.
/// </summary>
public sealed class OfferWire
{
    public required string Id { get; init; }
    public required string RequestId { get; init; }
    public required string JeeberId { get; init; }
    public long FeeCents { get; init; }
    public int EtaMinutes { get; init; }
    public string? Note { get; init; }
    public required string Status { get; init; }
    public int EditsCount { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? WithdrawnAt { get; init; }
}

/// <summary>
/// Minimal decode of the accept envelope. The gateway's accept path runs its
/// own request-transition orchestration (BR-1 / BR-10, OTP, chat) so it only
/// needs to know the accept succeeded and which offer won.
/// </summary>
public sealed class OfferAcceptWire
{
    public required string AcceptedOfferId { get; init; }
    public string? ChatThreadId { get; init; }
    public string? OtpCode { get; init; }
    public IReadOnlyList<string> RejectedOfferIds { get; init; } = Array.Empty<string>();
    public bool Replayed { get; init; }
}

/// <summary>Outcome of a withdraw, mirroring the upstream HTTP status mapping.</summary>
public enum OfferWithdrawResult
{
    Withdrawn,
    NotFound,
    NotOwned,
    NotPending
}
