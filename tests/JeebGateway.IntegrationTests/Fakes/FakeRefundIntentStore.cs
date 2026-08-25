using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Financials.Refunds;

namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>W5 §4 — in-memory <see cref="IRefundIntentStore"/> for the state-service KV, keyed by
/// requestId; the refund sweeper's enumeration surface.</summary>
/// <remarks>Prod contract, not the raw KV: <see cref="CloseAsync"/> tombstones, and a tombstoned
/// record reads back as ABSENT from <see cref="GetAsync"/> and <see cref="ListAllAsync"/>.</remarks>
public sealed class FakeRefundIntentStore : IRefundIntentStore
{
    /// <summary>Tombstone state written instead of a DELETE (the KV has no delete).</summary>
    public const string ClosedState = "closed";

    private readonly object _gate = new();
    private readonly Dictionary<string, RefundIntent> _intents = new(StringComparer.Ordinal);

    /// <summary>Write-fault injection: when it returns true the write THROWS, driving the
    /// "intent write failed, credit still attempted" leg of §2b step 4.</summary>
    public Func<RefundIntent, bool>? FailWriteWhen { get; set; }

    public int WriteCalls { get; private set; }

    public int CloseCalls { get; private set; }

    /// <summary>Every record, tombstones included — the assertion surface, since a closed intent
    /// is deliberately invisible to the store's own reads.</summary>
    public IReadOnlyList<RefundIntent> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _intents.Values.ToArray();
            }
        }
    }

    /// <summary>Raw read that ignores the tombstone, so "the intent was closed" is observable.</summary>
    public RefundIntent? Peek(string requestId)
    {
        lock (_gate)
        {
            return _intents.TryGetValue(requestId, out var intent) ? intent : null;
        }
    }

    /// <summary>Seeds a record without running the refunder (the sweeper suites' arrange).</summary>
    public void Seed(RefundIntent intent)
    {
        lock (_gate)
        {
            _intents[intent.RequestId] = intent;
        }
    }

    public Task WriteAsync(RefundIntent intent, CancellationToken ct)
    {
        lock (_gate)
        {
            WriteCalls++;
            if (FailWriteWhen?.Invoke(intent) == true)
            {
                return Task.FromException(
                    new InvalidOperationException("simulated refund-intent write failure"));
            }

            _intents[intent.RequestId] = intent;
            return Task.CompletedTask;
        }
    }

    public Task<RefundIntent?> GetAsync(string requestId, CancellationToken ct)
    {
        var intent = Peek(requestId);
        return Task.FromResult(IsClosed(intent) ? null : intent);
    }

    public Task<IReadOnlyList<RefundIntent>> ListAllAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<RefundIntent> open = _intents.Values
                .Where(i => !IsClosed(i))
                .ToArray();
            return Task.FromResult(open);
        }
    }

    public Task CloseAsync(string requestId, CancellationToken ct)
    {
        lock (_gate)
        {
            CloseCalls++;
            // Tombstone, never a delete: the record stays in Snapshot with State=closed while
            // every store read treats it as absent.
            if (_intents.TryGetValue(requestId, out var intent))
            {
                _intents[requestId] = intent with { State = ClosedState };
            }

            return Task.CompletedTask;
        }
    }

    private static bool IsClosed(RefundIntent? intent)
        => intent is not null
            && string.Equals(intent.State, ClosedState, StringComparison.OrdinalIgnoreCase);
}
