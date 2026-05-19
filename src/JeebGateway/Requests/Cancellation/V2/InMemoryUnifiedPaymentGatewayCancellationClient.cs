using System.Collections.Concurrent;

namespace JeebGateway.Requests.Cancellation.V2;

/// <summary>
/// MVP in-memory <see cref="IUnifiedPaymentGatewayCancellationClient"/>
/// keyed by <see cref="CancellationFeePostRequest.IdempotencyKey"/>. A
/// second post with the same key returns <see cref="CancellationFeePostOutcome.AlreadyPosted"/>
/// — production HTTP adapter behaves identically because the downstream
/// endpoint honours the Idempotency-Key header.
///
/// Test surface: callers can inspect <see cref="Posted"/> to assert the
/// fee was actually attempted with the expected amount and idempotency key.
/// </summary>
public sealed class InMemoryUnifiedPaymentGatewayCancellationClient
    : IUnifiedPaymentGatewayCancellationClient
{
    private readonly ConcurrentDictionary<string, CancellationFeePostRequest> _posted = new();

    /// <summary>Inspect the rows that have been recorded (test helper).</summary>
    public IReadOnlyCollection<CancellationFeePostRequest> Posted => _posted.Values.ToArray();

    public Task<CancellationFeePostResult> PostCancellationFeeAsync(
        CancellationFeePostRequest request,
        CancellationToken ct)
    {
        var txId = $"upg-cancel-{request.IdempotencyKey}";
        if (_posted.TryAdd(request.IdempotencyKey, request))
        {
            return Task.FromResult(CancellationFeePostResults.Posted(txId));
        }
        return Task.FromResult(CancellationFeePostResults.AlreadyPosted(txId));
    }
}
