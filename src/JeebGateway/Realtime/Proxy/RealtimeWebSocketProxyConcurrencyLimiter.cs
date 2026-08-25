using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace JeebGateway.Realtime.Proxy;

/// <summary>
/// Queue-free global and per-client-IP concurrency bounds for long-lived
/// WebSocket upgrades. The tracked-IP map is capped; excess distinct IPs share
/// one conservative overflow partition instead of growing memory without bound.
/// </summary>
internal sealed class RealtimeWebSocketProxyConcurrencyLimiter
{
    private const string UnknownClient = "unknown";

    private readonly SemaphoreSlim _global;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perIp =
        new(StringComparer.Ordinal);
    private readonly object _partitionGate = new();
    private readonly SemaphoreSlim _overflow;
    private readonly int _perIpLimit;
    private readonly int _maximumTrackedIps;

    public RealtimeWebSocketProxyConcurrencyLimiter(
        IOptions<RealtimeWebSocketProxyOptions> options)
    {
        var value = options.Value;
        var globalLimit = Math.Clamp(value.GlobalConcurrencyLimit, 1, 4096);
        _perIpLimit = Math.Clamp(value.PerIpConcurrencyLimit, 1, 64);
        _maximumTrackedIps = Math.Clamp(value.MaximumTrackedClientIps, 64, 65536);
        _global = new SemaphoreSlim(globalLimit, globalLimit);
        _overflow = new SemaphoreSlim(_perIpLimit, _perIpLimit);
    }

    public Lease? TryAcquire(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_global.Wait(0))
        {
            return null;
        }

        var key = context.Connection.RemoteIpAddress?.ToString() ?? UnknownClient;
        var partition = ResolvePartition(key);
        if (!partition.Wait(0))
        {
            _global.Release();
            return null;
        }

        return new Lease(_global, partition);
    }

    private SemaphoreSlim ResolvePartition(string key)
    {
        if (_perIp.TryGetValue(key, out var existing))
        {
            return existing;
        }

        lock (_partitionGate)
        {
            if (_perIp.TryGetValue(key, out existing))
            {
                return existing;
            }

            if (_perIp.Count >= _maximumTrackedIps)
            {
                return _overflow;
            }

            return _perIp.GetOrAdd(
                key,
                static (_, limit) => new SemaphoreSlim(limit, limit),
                _perIpLimit);
        }
    }

    internal sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _global;
        private SemaphoreSlim? _partition;

        public Lease(SemaphoreSlim global, SemaphoreSlim partition)
        {
            _global = global;
            _partition = partition;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _partition, null)?.Release();
            Interlocked.Exchange(ref _global, null)?.Release();
        }
    }
}
