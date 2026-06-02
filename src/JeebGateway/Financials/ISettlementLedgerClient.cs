namespace JeebGateway.Financials;

/// <summary>
/// Cash-settlement ledger contract consumed by <see cref="SettlementService"/>
/// (T-backend-016 / JEEB-34). Posts a single best-effort double-entry ledger
/// record per settled cash delivery.
///
/// This lives in the Financials module (not the Wallet integration) because
/// cash settlement is a Jeeb product concern, not part of the shared
/// wallet-service surface that the gateway now mirrors from the salehly
/// sibling. The wallet integration (Controllers/WalletController.cs +
/// Services/ServiceWalletClient.cs) proxies the upstream wallet API
/// byte-for-byte and intentionally does not expose a Jeeb-specific ledger
/// route, so settlement keeps its own slim contract here.
///
/// The default <see cref="InMemorySettlementLedgerClient"/> records the entry
/// in-process and returns a generated ledger id; SettlementService already
/// treats the post as best-effort (idempotent on the settlement id) and
/// persists the settlement row as the gateway-side system of record.
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
