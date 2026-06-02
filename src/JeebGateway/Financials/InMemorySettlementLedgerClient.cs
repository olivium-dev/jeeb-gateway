using System.Collections.Concurrent;

namespace JeebGateway.Financials;

/// <summary>
/// In-process <see cref="ISettlementLedgerClient"/>. Records one ledger entry
/// per settlement id and is idempotent on <see cref="LedgerEntryRequest.IdempotencyKey"/>:
/// replaying the same key returns the original entry rather than posting twice.
///
/// SettlementService persists the settlement row as the gateway-side system of
/// record and treats the ledger post as best-effort, so an in-process ledger is
/// sufficient for the gateway (a pure BFF holds no wallet state). When a
/// canonical double-entry book is required it is owned by the upstream
/// wallet-service, reachable through the mirrored
/// <see cref="JeebGateway.service.ServiceWallet.ServiceWalletClient"/> transaction
/// surface — not by re-introducing a hand-rolled HttpClient here.
/// </summary>
public sealed class InMemorySettlementLedgerClient : ISettlementLedgerClient
{
    private readonly ConcurrentDictionary<string, LedgerEntryResponse> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public InMemorySettlementLedgerClient(TimeProvider clock)
    {
        _clock = clock;
    }

    public Task<LedgerEntryResponse> PostLedgerEntryAsync(LedgerEntryRequest request, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(request.IdempotencyKey, _ => new LedgerEntryResponse
        {
            LedgerEntryId = Guid.NewGuid().ToString(),
            PostedAt = _clock.GetUtcNow(),
        });
        return Task.FromResult(entry);
    }
}
