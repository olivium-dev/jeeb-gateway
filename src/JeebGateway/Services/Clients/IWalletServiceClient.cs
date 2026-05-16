namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the wallet-service ledger surface. The gateway posts
/// a single entry per cash settlement (T-backend-016 / JEEB-34); the
/// wallet-service owns the canonical double-entry book and exposes the
/// ledger primitives that every olivium product line (Jeeb, rahmah,
/// cremat, salehly) consumes.
///
/// Hand-coded ahead of the NSwag-generated client — the wallet-service
/// OpenAPI spec under <c>contracts/wallet-service.openapi.json</c> is a
/// placeholder until the upstream spec is published. Replace this
/// interface implementation in the T-backend-bff-wallet migration.
/// </summary>
public interface IWalletServiceClient
{
    Task<LedgerEntryResponse> PostLedgerEntryAsync(LedgerEntryRequest request, CancellationToken ct);
}

/// <summary>
/// Wire shape for POST /ledger/entries (BFF aggregation pattern).
/// All monetary fields are in the same currency as <see cref="Currency"/>.
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
    /// Caller-supplied idempotency key. wallet-service returns the
    /// existing entry when the same key is replayed so retries don't
    /// double-post. The gateway uses the settlement id here.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}

public sealed class LedgerEntryResponse
{
    public required string LedgerEntryId { get; init; }
    public DateTimeOffset? PostedAt { get; init; }
}
