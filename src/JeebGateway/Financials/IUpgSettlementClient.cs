namespace JeebGateway.Financials;

/// <summary>
/// Typed client over the Unified Payment Gateway's GENERIC external-settlement
/// endpoint (JEB-1484): <c>POST /api/v1/payments/settlements/record</c>.
///
/// GR3 — payments flow through UPG. The Jeeb fee policy (commission tiers,
/// insurance, minimum fee, rounding) is computed HERE in the gateway
/// (<see cref="CommissionCalculator"/> / <see cref="SettlementService"/>); UPG
/// is a product-agnostic ledger that records pre-computed gross/fee/net keyed
/// by <c>(source, externalRef)</c> and performs NO settlement math.
///
/// GR4 — this consumer should be the NSwag-generated
/// <c>ServiceUnifiedPaymentGatewayClient</c> regenerated from
/// <c>contracts/unified-payment-gateway.openapi.json</c> via
/// <c>scripts/regenerate-clients.sh</c>. The hand-coded
/// <see cref="UpgSettlementClient"/> is an interim transport carrying the
/// SAME bearer + X-Service-Auth + resilience pipeline as every other typed
/// client; NSwag regeneration is deferred to CI (the build host has no dotnet
/// nswag tool). This mirrors the established OfferServiceClient / BanServiceClient
/// hand-coded-client precedent and is tracked as GR4 debt.
/// </summary>
public interface IUpgSettlementClient
{
    Task<UpgSettlementResponse> RecordSettlementAsync(UpgSettlementRequest request, CancellationToken ct);
}

/// <summary>
/// Generic external-settlement record request. Amounts are sent as
/// invariant-culture decimal strings to preserve exact fractional values
/// across the JSON boundary (UPG stores Decimals).
/// </summary>
public sealed class UpgSettlementRequest
{
    public required string Source { get; init; }
    public required string ExternalRef { get; init; }
    public string? PayeeRef { get; init; }
    public required decimal GrossAmount { get; init; }
    public decimal? FeeAmount { get; init; }
    public decimal? NetAmount { get; init; }
    public required string Currency { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed class UpgSettlementResponse
{
    /// <summary>The UPG-side settlement record id (the envelope's <c>data.id</c>).</summary>
    public required string SettlementId { get; init; }
    public string? Status { get; init; }
}
