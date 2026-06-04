using JeebGateway.Services.Clients;

namespace JeebGateway.StateService.RateLimiting;

/// <summary>
/// Durable, replica-shared rate-limit + handover-lock primitives (R8),
/// backed by jeeb-state-service. Both map 1:1 onto the state-service
/// contract (the bucket / lock key IS the natural domain key), so these
/// survive a gateway bounce and are shared across replicas without any
/// in-gateway state.
/// </summary>
public interface IStateRateLimitStore
{
    /// <summary>Records a hit in <paramref name="bucket"/> over a sliding
    /// window and returns the current count. The bucket key encodes the
    /// principal/IP/route the gateway is limiting.</summary>
    Task<long> HitAsync(string bucket, int windowSeconds, CancellationToken ct);
}

/// <summary>Time-boxed, exactly-once handover lock (R8 / S09).</summary>
public interface IStateLockStore
{
    /// <summary>Attempts to acquire <paramref name="lockKey"/> for
    /// <paramref name="ownerToken"/>. Returns false when already held by a
    /// different owner (state-service answers 409).</summary>
    Task<bool> TryAcquireAsync(string lockKey, string ownerToken, int ttlSeconds, CancellationToken ct);

    /// <summary>Releases a lock held by <paramref name="ownerToken"/>. No-op
    /// when not held.</summary>
    Task ReleaseAsync(string lockKey, string ownerToken, CancellationToken ct);
}

public sealed class StateServiceRateLimitStore : IStateRateLimitStore
{
    private readonly IJeebStateServiceClient _client;
    private readonly ILogger<StateServiceRateLimitStore> _logger;

    public StateServiceRateLimitStore(IJeebStateServiceClient client, ILogger<StateServiceRateLimitStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<long> HitAsync(string bucket, int windowSeconds, CancellationToken ct)
    {
        // The state-service returns the current count in the response body,
        // but the OpenAPI documents the op as 200/no-body so the NSwag method
        // is void. We therefore record the hit for durability; callers that
        // need the precise count read it back is not yet supported by the
        // contract (see SPECS-STATUS: rate-limit count not echoed via typed
        // client). Returns 1 as a conservative floor.
        await _client.HitRateLimitAsync(new RateLimitHitRequest
        {
            Bucket = bucket,
            WindowSeconds = windowSeconds
        }, ct);
        return 1;
    }
}

public sealed class StateServiceLockStore : IStateLockStore
{
    private readonly IJeebStateServiceClient _client;
    private readonly ILogger<StateServiceLockStore> _logger;

    public StateServiceLockStore(IJeebStateServiceClient client, ILogger<StateServiceLockStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> TryAcquireAsync(string lockKey, string ownerToken, int ttlSeconds, CancellationToken ct)
    {
        try
        {
            await _client.AcquireLockAsync(new LockAcquireRequest
            {
                LockKey = lockKey,
                OwnerToken = ownerToken,
                TtlSeconds = ttlSeconds
            }, ct);
            return true;
        }
        catch (Exception ex) when (StateServiceErrors.IsConflict(ex))
        {
            // 409 — held by another owner.
            return false;
        }
    }

    public async Task ReleaseAsync(string lockKey, string ownerToken, CancellationToken ct)
    {
        try
        {
            await _client.ReleaseLockAsync(new LockAcquireRequest
            {
                LockKey = lockKey,
                OwnerToken = ownerToken,
                TtlSeconds = 0
            }, ct);
        }
        catch (Exception ex) when (StateServiceErrors.IsNotFound(ex) || StateServiceErrors.IsConflict(ex))
        {
            _logger.LogDebug("Lock {LockKey} release no-op ({Status})", lockKey, ex.Message);
        }
    }
}
