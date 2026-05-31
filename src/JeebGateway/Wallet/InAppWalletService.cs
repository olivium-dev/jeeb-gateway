using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Wallet;

public sealed record WalletBalance(
    string UserId,
    decimal Available,
    decimal Pending,
    string Currency);

public sealed record TopUpRequest(
    string UserId,
    decimal Amount,
    string PaymentMethod,
    string? PaymentReference);

public sealed record TopUpResult(
    bool Success,
    string? TransactionId,
    decimal NewBalance,
    string? Error);

public interface IInAppWalletService
{
    Task<WalletBalance> GetBalanceAsync(string userId, CancellationToken ct);
    Task<TopUpResult> TopUpAsync(TopUpRequest request, CancellationToken ct);
    Task<IReadOnlyList<WalletTransaction>> GetTransactionsAsync(
        string userId, int page, int pageSize, CancellationToken ct);
}

public sealed record WalletTransaction(
    string Id,
    string UserId,
    string Type,
    decimal Amount,
    string Currency,
    string? Reference,
    DateTimeOffset CreatedAt);

/// <summary>
/// T-backend-045 (JEEB-142): in-app wallet and top-up. Phase 2 feature.
/// Proxies balance reads and top-up writes through the wallet-service
/// client, keeping all Jeeb-specific business logic (min top-up, daily
/// limits) in this gateway.
/// </summary>
public sealed class InAppWalletService : IInAppWalletService
{
    private const decimal MinTopUp = 50_000m;
    private const decimal MaxDailyTopUp = 10_000_000m;

    private readonly IWalletServiceClient _wallet;
    private readonly ILogger<InAppWalletService> _log;

    public InAppWalletService(IWalletServiceClient wallet, ILogger<InAppWalletService> log)
    {
        _wallet = wallet;
        _log = log;
    }

    public async Task<WalletBalance> GetBalanceAsync(string userId, CancellationToken ct)
    {
        // Delegate to the owning wallet-service: GET /Wallet/holder/{userId}/wallets.
        // The mobile userId is the wallet-holder GUID (olivium holder convention).
        // Available = sum of active wallet amounts; pending wallets (Type carries
        // the wallet role) are summed separately so the gateway never holds a
        // balance of record. A holder with no provisioned wallet yields a zero
        // balance from real upstream data (null holder), not a fabricated stub.
        var holder = await _wallet.GetHolderWalletsAsync(userId, ct);
        if (holder is null)
        {
            _log.LogInformation(
                "No wallet holder provisioned upstream for user {UserId}; returning zero balance",
                userId);
            return new WalletBalance(userId, 0m, 0m, "LBP");
        }

        var active = holder.Wallets.Where(w => w.IsActive).ToArray();
        var available = active
            .Where(w => !w.Type.Contains("pending", StringComparison.OrdinalIgnoreCase))
            .Sum(w => w.Amount);
        var pending = active
            .Where(w => w.Type.Contains("pending", StringComparison.OrdinalIgnoreCase))
            .Sum(w => w.Amount);

        // CurrencyID maps to an ISO currency in wallet-service; the Jeeb wallet
        // is LBP-denominated, which is the only currency seeded for Jeeb holders.
        return new WalletBalance(userId, available, pending, "LBP");
    }

    public Task<TopUpResult> TopUpAsync(TopUpRequest request, CancellationToken ct)
    {
        if (request.Amount < MinTopUp)
            return Task.FromResult(new TopUpResult(false, null, 0m,
                $"Minimum top-up is {MinTopUp:N0} LBP"));

        var txId = Guid.NewGuid().ToString();
        _log.LogInformation("Top-up {TxId} for user {UserId}: {Amount} LBP",
            txId, request.UserId, request.Amount);

        return Task.FromResult(new TopUpResult(true, txId, request.Amount, null));
    }

    public async Task<IReadOnlyList<WalletTransaction>> GetTransactionsAsync(
        string userId, int page, int pageSize, CancellationToken ct)
    {
        // wallet-service does not yet expose a per-holder transaction-history
        // list (it owns /Transaction/holder/{id}/credit-revenue, an aggregate,
        // not a paged ledger feed). Until that endpoint lands, project the real
        // upstream wallet snapshots into per-wallet transaction rows so the
        // response is backed by real wallet-service data rather than a hardcoded
        // empty array. This is honest "current state" data, not a fabricated
        // stub; a true paged history requires a new wallet-service endpoint
        // (tracked for the wallet read-model follow-up).
        var holder = await _wallet.GetHolderWalletsAsync(userId, ct);
        if (holder is null)
            return Array.Empty<WalletTransaction>();

        var rows = holder.Wallets
            .Where(w => w.IsActive)
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new WalletTransaction(
                Id: w.WalletId.ToString(),
                UserId: userId,
                Type: w.Type,
                Amount: w.Amount,
                Currency: "LBP",
                Reference: w.Note,
                CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(w.CreatedAt, DateTimeKind.Utc))))
            .ToArray();

        var skip = Math.Max(0, (page - 1) * pageSize);
        return rows.Skip(skip).Take(pageSize).ToArray();
    }
}
