using JeebGateway.Infrastructure;

namespace JeebGateway.StateService.Idempotency;

/// <summary>Fail-closed state owner used when no state-service is configured.</summary>
public sealed class UnavailableIdempotencyStore : IExternalIdempotencyStore
{
    public Task<IdempotencyOutcome> PutOrGetAsync(string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct) => Fail<IdempotencyOutcome>();
    public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct) => Fail<IdempotencyOutcome?>();
    public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(string prefix, CancellationToken ct) => Fail<IReadOnlyList<IdempotencyOutcome>>();

    private static Task<T> Fail<T>() => Task.FromException<T>(
        new OwnerCapabilityUnavailableException("jeeb-state-service idempotency"));
}
