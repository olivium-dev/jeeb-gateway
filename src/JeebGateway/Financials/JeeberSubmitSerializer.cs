using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Financials;

/// <summary>C1-F2 — striped async mutex keyed by jeeber id: one jeeber's enumerate → cap →
/// check/hold → submit runs serially, while distinct jeebers never block each other.</summary>
/// <remarks>Scope is THIS process (single-instance MSI) — NOT a distributed lock; with multiple
/// replicas the durable hold is the only guarantee. Register as a singleton.</remarks>
public sealed class JeeberSubmitSerializer
{
    // Stripes are never evicted: one small semaphore per distinct jeeber id seen since boot.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Waits for this jeeber's stripe; dispose the handle to release it. Honours ct — a
    /// cancelled wait throws and nothing is acquired or released.</summary>
    public async Task<IDisposable> AcquireAsync(string jeeberId, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(jeeberId ?? string.Empty, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Stripe(gate);
    }

    /// <summary>Release-once handle: a double dispose must never inflate the semaphore count.</summary>
    private sealed class Stripe : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Stripe(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
