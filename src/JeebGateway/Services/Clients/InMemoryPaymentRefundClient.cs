using System.Collections.Concurrent;

namespace JeebGateway.Services.Clients;

/// <summary>
/// TEST DOUBLE ONLY — <b>not registered in the application</b>. Records every
/// refund in process so integration tests can assert that a refund call
/// happened (T-BE-028 / JEB-64 AC2). Honours the <c>IdempotencyKey</c> contract
/// by short-circuiting replays of the same key.
///
/// <para>DANGER, and the reason this type is quarantined to tests: it REPORTS
/// SUCCESS. That is exactly the failure it was implicated in — while it was the
/// production fallback, every dispute refund (real money OUT) became a no-op the
/// system believed had worked. Since 2026-07-27 the only registered
/// <see cref="IPaymentRefundClient"/> is
/// <see cref="CashOnDeliveryNoRefundClient"/>, which throws. Do NOT register
/// this class in <c>Program.cs</c>; a test that needs it must inject it
/// explicitly via <c>ConfigureTestServices</c>.</para>
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
