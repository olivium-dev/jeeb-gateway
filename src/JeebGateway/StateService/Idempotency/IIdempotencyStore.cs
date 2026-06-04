namespace JeebGateway.StateService.Idempotency;

/// <summary>
/// Gateway-wide exactly-once primitive (ADR-001-rev2 §item 7, JEB-1493).
/// Semantics live here; durability lives in jeeb-state-service's
/// <c>idempotency_keys</c> table (<c>INSERT … ON CONFLICT (key) DO NOTHING
/// RETURNING</c>). The gateway holds NO row itself.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically records the first response for <paramref name="key"/>, or
    /// returns the original on replay. The returned record's
    /// <see cref="IdempotencyOutcome.Inserted"/> is <c>true</c> only for the
    /// caller that won the race; all replays get <c>false</c> plus the
    /// originally-stored status + body.
    /// </summary>
    Task<IdempotencyOutcome> PutOrGetAsync(
        string key,
        int statusCode,
        string responseBodyJson,
        int ttlSeconds,
        CancellationToken ct);

    /// <summary>Reads a stored response, or null when the key was never seen.</summary>
    Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct);
}

/// <summary>Result of an idempotency check.</summary>
public sealed class IdempotencyOutcome
{
    /// <summary>True only for the writer that created the row; false on replay.</summary>
    public required bool Inserted { get; init; }

    /// <summary>The stored HTTP status of the original response.</summary>
    public required int StatusCode { get; init; }

    /// <summary>The stored response body (raw JSON), replayed verbatim.</summary>
    public required string ResponseBodyJson { get; init; }
}
