namespace JeebGateway.Financials;

/// <summary>
/// Jeeb product settlement boundary consumed by <see cref="SettlementService"/>. The active
/// implementation translates this request into explicit generic wallet transaction legs;
/// wallet-service owns the durable header/details, idempotency and balances. Legacy local
/// implementations remain only as migration source code and are never registered.
/// </summary>
public interface ISettlementLedgerClient
{
    Task<LedgerEntryResponse> PostLedgerEntryAsync(LedgerEntryRequest request, CancellationToken ct);
}

/// <summary>
/// Wire shape for a cash-settlement ledger entry. All monetary fields are in
/// the same currency as <see cref="Currency"/>.
/// </summary>
public sealed class LedgerEntryRequest
{
    public required string DeliveryId { get; init; }
    public required string JeeberId { get; init; }
    public required string ClientId { get; init; }
    public required string EntryType { get; init; }
    public required decimal GoodsCost { get; init; }
    public required decimal Commission { get; init; }
    public required decimal Insurance { get; init; }
    public required decimal Total { get; init; }
    public required string Currency { get; init; }
    public required string PaymentMethod { get; init; }

    /// <summary>
    /// Caller-supplied idempotency key. The same key replayed returns the
    /// existing entry so retries don't double-post. The gateway uses the
    /// settlement id here.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}

public sealed class LedgerEntryResponse
{
    public required string LedgerEntryId { get; init; }
    public DateTimeOffset? PostedAt { get; init; }
}
