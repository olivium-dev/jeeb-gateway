namespace JeebGateway.Requests.Cancellation.V2;

/// <summary>
/// T-BE-030 (JEB-66) gateway-side adapter for
/// <c>olivium-dev/unified_payment_gateway</c>'s cancellation-fee endpoint:
/// <c>POST /v1/payments/cod_jeeb/fee</c> (extended in T-BE-020 alongside
/// the C10 work). Posts the cancellation fee against the user's wallet
/// in the COD-Jeeb ledger.
///
/// Production wiring (TODO T-backend-bff-payments — tracked in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) swaps
/// the in-memory implementation with an HTTP adapter over the
/// NSwag-generated <c>ServiceUnifiedPaymentGatewayClient</c>
/// (<c>ServiceUnifiedPaymentGatewayApi:BaseUrl</c> config key), going
/// through the standard Polly resilience pipeline (retry-with-jitter +
/// circuit breaker + 10 s per-attempt timeout).
/// </summary>
public interface IUnifiedPaymentGatewayCancellationClient
{
    /// <summary>
    /// Records the cancellation fee against <paramref name="userId"/>'s
    /// wallet. Idempotency-Key is <paramref name="idempotencyKey"/> so a
    /// retry inside the Polly pipeline does not double-bill.
    ///
    /// Returns <see cref="CancellationFeePostResult.Posted"/> on success;
    /// <see cref="CancellationFeePostResult.AlreadyPosted"/> when the
    /// downstream has already accepted the same idempotency key (treated
    /// as success by the controller — the row exists once);
    /// <see cref="CancellationFeePostResult.Failed"/> when the downstream
    /// rejects (e.g. wallet not provisioned). Failures are logged but do
    /// not roll back the cancellation — fee collection is best-effort
    /// per Q-OPEN-2 (the policy never sacrifices a cancel for a fee).
    /// </summary>
    Task<CancellationFeePostResult> PostCancellationFeeAsync(
        CancellationFeePostRequest request,
        CancellationToken ct);
}

public sealed record CancellationFeePostRequest(
    string UserId,
    string DeliveryId,
    decimal Amount,
    string Currency,
    string IdempotencyKey,
    string? Reason,
    DateTimeOffset At);

public enum CancellationFeePostOutcome
{
    Posted,
    AlreadyPosted,
    Failed,
}

public sealed record CancellationFeePostResult(
    CancellationFeePostOutcome Outcome,
    string? TransactionId,
    string? Error);

public static class CancellationFeePostResults
{
    public static CancellationFeePostResult Posted(string transactionId) =>
        new(CancellationFeePostOutcome.Posted, transactionId, null);

    public static CancellationFeePostResult AlreadyPosted(string transactionId) =>
        new(CancellationFeePostOutcome.AlreadyPosted, transactionId, null);

    public static CancellationFeePostResult Failed(string error) =>
        new(CancellationFeePostOutcome.Failed, null, error);
}
