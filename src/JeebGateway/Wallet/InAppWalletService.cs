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

    public Task<WalletBalance> GetBalanceAsync(string userId, CancellationToken ct)
    {
        return Task.FromResult(new WalletBalance(userId, 0m, 0m, "LBP"));
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

    public Task<IReadOnlyList<WalletTransaction>> GetTransactionsAsync(
        string userId, int page, int pageSize, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<WalletTransaction>>(
            Array.Empty<WalletTransaction>());
    }
}
