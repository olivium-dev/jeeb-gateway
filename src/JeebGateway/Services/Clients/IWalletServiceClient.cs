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

    /// <summary>
    /// Reads the system wallet holder and all its associated wallets from
    /// wallet-service Postgres via <c>GET /system-wallet</c>.
    /// Returns null when the system wallet holder does not yet exist in the
    /// jeeb-wallet database (e.g., before the seed migration has run).
    /// </summary>
    Task<SystemWalletResponse?> GetSystemWalletAsync(CancellationToken ct);
}

// ---------------------------------------------------------------------------
// DTOs for GET /system-wallet
// ---------------------------------------------------------------------------

/// <summary>
/// Gateway-side projection of wallet-service's <c>AddWalletHolderResponse</c>.
/// Field names match the wire JSON produced by wallet-service
/// (<c>WalletHolder</c> + <c>Wallets</c>).
/// </summary>
public sealed class SystemWalletResponse
{
    public SystemWalletHolder? WalletHolder { get; init; }
    public IReadOnlyList<SystemWallet> Wallets { get; init; } = [];
}

public sealed class SystemWalletHolder
{
    public Guid HolderId { get; init; }
    public string HolderName { get; init; } = string.Empty;
    public string HolderType { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class SystemWallet
{
    public Guid WalletId { get; init; }
    public Guid HolderId { get; init; }
    public int CurrencyID { get; init; }
    public decimal Amount { get; init; }
    public string Type { get; init; } = string.Empty;
    public string? Note { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
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
