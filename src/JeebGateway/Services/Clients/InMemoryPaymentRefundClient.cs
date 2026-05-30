using System.Collections.Concurrent;

namespace JeebGateway.Services.Clients;

/// <summary>
/// MVP fallback for <see cref="IPaymentRefundClient"/>. Records every
/// refund in process so integration tests can assert ledger calls
/// happened (T-BE-028 / JEB-64 AC2). Honours the <c>IdempotencyKey</c>
/// contract by short-circuiting replays of the same key.
///
/// The HTTP-backed <see cref="HttpPaymentRefundClient"/> takes over
/// once <c>Services:UnifiedPayment:BaseUrl</c> is configured.
/// </summary>
public sealed class InMemoryPaymentRefundClient : IPaymentRefundClient
{
    private readonly ConcurrentDictionary<string, RefundResult> _byKey = new(StringComparer.Ordinal);
    private readonly List<RefundRequest> _entries = new();
    private readonly object _lock = new();

    public IReadOnlyList<RefundRequest> Entries
    {
        get
        {
            lock (_lock) return _entries.ToArray();
        }
    }

    public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct)
    {
        if (_byKey.TryGetValue(request.IdempotencyKey, out var existing))
        {
            return Task.FromResult(existing);
        }

        var result = new RefundResult
        {
            Success = true,
            LedgerEntryId = $"refund-{request.IdempotencyKey}"
        };

        lock (_lock)
        {
            _entries.Add(request);
        }
        _byKey[request.IdempotencyKey] = result;
        return Task.FromResult(result);
    }
}
