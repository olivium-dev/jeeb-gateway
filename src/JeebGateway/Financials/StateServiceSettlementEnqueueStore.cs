using System.Text.Json;
using JeebGateway.StateService.Idempotency;

namespace JeebGateway.Financials;

/// <summary>
/// Settlement intent marker persisted in jeeb-state-service's external
/// idempotency store. No intent is cached in the gateway process.
/// </summary>
public sealed class StateServiceSettlementEnqueueStore : ISettlementEnqueueStore
{
    private const string Prefix = "settlement-intent:";
    private const int TtlSeconds = 365 * 24 * 60 * 60;
    private readonly IExternalIdempotencyStore _owner;

    public StateServiceSettlementEnqueueStore(IExternalIdempotencyStore owner) => _owner = owner;

    public async Task<bool> TryEnqueueAsync(
        string deliveryId, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        var outcome = await _owner.PutOrGetAsync(
            Prefix + deliveryId,
            StatusCodes.Status201Created,
            JsonSerializer.Serialize(new { deliveryId, enqueuedAt = at.ToUniversalTime() }),
            TtlSeconds,
            ct);
        return outcome.Inserted;
    }

    public async Task<bool> IsEnqueuedAsync(string deliveryId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        return await _owner.GetAsync(Prefix + deliveryId, ct) is not null;
    }
}
