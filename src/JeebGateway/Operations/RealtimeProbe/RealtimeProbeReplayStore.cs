using StackExchange.Redis;

namespace JeebGateway.Operations.RealtimeProbe;

internal enum RealtimeProbeReplayReservation
{
    Acquired,
    Replay,
    Unavailable,
}

internal interface IRealtimeProbeReplayStore
{
    Task<RealtimeProbeReplayReservation> TryReserveAsync(
        string nonce,
        CancellationToken cancellationToken);
}

/// <summary>Tiny adapter that keeps StackExchange.Redis out of endpoint tests.</summary>
internal interface IRealtimeProbeRedisClient
{
    Task<bool> SetIfAbsentAsync(
        string key,
        string value,
        TimeSpan expiry,
        CancellationToken cancellationToken);
}

internal sealed class StackExchangeRealtimeProbeRedisClient : IRealtimeProbeRedisClient
{
    private readonly Func<IConnectionMultiplexer?> _connectionFactory;

    public StackExchangeRealtimeProbeRedisClient(
        Func<IConnectionMultiplexer?> connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<bool> SetIfAbsentAsync(
        string key,
        string value,
        TimeSpan expiry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Resolve lazily inside the replay store's guarded call. The gateway's
        // existing multiplexer registration connects synchronously; resolving it
        // during endpoint parameter activation could otherwise escape as a 500.
        var connection = _connectionFactory();
        if (connection is null)
        {
            throw new InvalidOperationException("The staging Redis connection is unavailable.");
        }

        if (expiry != RedisRealtimeProbeReplayStore.ReservationTtl)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiry),
                "The realtime probe replay reservation must use the exact 120-second TTL.");
        }

        // Keep the wire operation exact and reviewable: SET <key> 1 NX EX 120.
        // A null Redis result is the normal "already exists" replay outcome.
        var command = connection.GetDatabase().ExecuteAsync(
            "SET",
            new object[] { key, value, "NX", "EX", 120L },
            CommandFlags.DemandMaster);
        var result = await command.WaitAsync(cancellationToken);
        return !result.IsNull
            && string.Equals((string?)result, "OK", StringComparison.Ordinal);
    }
}

/// <summary>
/// Atomic replay gate: Redis SET key 1 NX EX 120. Any Redis/configuration fault
/// fails closed so an unverifiable request can never mint credentials.
/// </summary>
internal sealed class RedisRealtimeProbeReplayStore : IRealtimeProbeReplayStore
{
    internal const string KeyPrefix = "jeeb:ops:realtime-probe:nonce:";
    internal static readonly TimeSpan ReservationTtl = TimeSpan.FromSeconds(120);

    private readonly IRealtimeProbeRedisClient _redis;
    private readonly ILogger<RedisRealtimeProbeReplayStore> _logger;

    public RedisRealtimeProbeReplayStore(
        IRealtimeProbeRedisClient redis,
        ILogger<RedisRealtimeProbeReplayStore> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<RealtimeProbeReplayReservation> TryReserveAsync(
        string nonce,
        CancellationToken cancellationToken)
    {
        try
        {
            var acquired = await _redis.SetIfAbsentAsync(
                KeyPrefix + nonce,
                "1",
                ReservationTtl,
                cancellationToken);
            return acquired
                ? RealtimeProbeReplayReservation.Acquired
                : RealtimeProbeReplayReservation.Replay;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Do not log the nonce/key: it came from an authentication header.
            _logger.LogWarning(
                "Staging realtime probe replay reservation failed closed ({FailureType}).",
                exception.GetType().Name);
            return RealtimeProbeReplayReservation.Unavailable;
        }
    }
}
