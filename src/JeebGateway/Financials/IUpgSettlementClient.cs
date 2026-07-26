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
/// The committed UPG spec and its NSwag-generated client were deleted on
/// 2026-07-26 (owner directive — no unified_payment_gateway coupling in Jeeb);
/// they had zero call sites. <see cref="UpgSettlementClient"/> remains the
/// hand-coded transport, carrying the SAME bearer + X-Service-Auth + resilience
/// pipeline as every other typed client.
///
/// This type is NOT dead code: it is reached whenever
/// <c>FeatureFlags:UseUpstream:Payments</c> is true (default false, so dormant —
/// but `FeatureFlags__UseUpstream__Payments=true` arms it). Retiring it needs a
/// replacement settlement destination first; see
/// docs/batches/b02-20260726/UPG-REMOVAL.md.
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
