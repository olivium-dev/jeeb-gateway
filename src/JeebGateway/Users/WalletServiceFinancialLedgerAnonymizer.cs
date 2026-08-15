using System.Net;

namespace JeebGateway.Users;

public sealed class WalletLedgerCloseConflictException : Exception
{
    public WalletLedgerCloseConflictException()
        : base("Wallet holder still has a non-zero balance or pending transaction.")
    {
    }
}

/// <summary>
/// Stateless account-closure adapter over wallet-service's owner operation.
/// Wallet-service chooses and persists its own pseudonym; the gateway sends
/// only the opaque holder id and never claims an upstream row count.
/// </summary>
public sealed class WalletServiceFinancialLedgerAnonymizer(
    IHttpClientFactory clients) : IFinancialLedgerAnonymizer
{
    public const string HttpClientName = "ServiceWalletClient";

    public async Task<int> AnonymizeForUserAsync(
        string userId,
        string anonymizedHash,
        CancellationToken ct)
    {
        // The second parameter belongs to the legacy gateway contract and is
        // deliberately not forwarded. Wallet-service owns its pseudonym.
        _ = anonymizedHash;
        if (!Guid.TryParse(userId, out var holderId) || holderId == Guid.Empty)
            throw new InvalidOperationException(
                "Wallet-service account closure requires a non-empty GUID holder id.");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"Wallet/holder/{holderId:D}/close");
        using var response = await clients.CreateClient(HttpClientName).SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new WalletLedgerCloseConflictException();
        response.EnsureSuccessStatusCode();

        // Success means the owner completed (or idempotently replayed) its
        // closure. Zero means "count not exposed", not "nothing happened";
        // the orchestrator treats HTTP success as the only completion signal.
        return 0;
    }

    public Task<int> CountRowsForUserAsync(string userId, CancellationToken ct) =>
        throw new NotSupportedException(
            "Wallet-service does not expose gateway-facing ledger row counts.");

    public Task<int> CountRowsForHashAsync(string anonymizedHash, CancellationToken ct) =>
        throw new NotSupportedException(
            "Wallet-service owns its pseudonym and does not expose row counts by pseudonym.");
}
