using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Financials.Holds;

namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>W3/T2 — in-memory <see cref="IHoldIntentStore"/> for the state-service KV. It is the
/// sweeper's enumeration surface, so tombstones stay visible as a short-TTL overwrite would.</summary>
public sealed class FakeHoldIntentStore : IHoldIntentStore
{
    /// <summary>Tombstone state the design writes instead of a DELETE (no KV DELETE exists).</summary>
    public const string ClosedState = "closed";

    private readonly object _gate = new();
    private readonly Dictionary<string, HoldIntent> _intents = new(StringComparer.Ordinal);

    /// <summary>One-shot write fault: the intent write is REQUIRED, so its failure must map to
    /// E5 503 with nothing placed and no live offer.</summary>
    public bool FailNextWrite { get; set; }

    public int WriteCalls { get; private set; }

    public int CloseCalls { get; private set; }

    /// <summary>Every record, tombstones included — what a prefix scan would really return.</summary>
    public IReadOnlyList<HoldIntent> Snapshot()
    {
        lock (_gate)
        {
            return _intents.Values.ToArray();
        }
    }

    /// <summary>Direct read for assertions; a closed record reads back with
    /// <see cref="ClosedState"/> so "intent closed" is observable.</summary>
    public HoldIntent? Peek(string offerId)
    {
        lock (_gate)
        {
            return _intents.TryGetValue(offerId, out var intent) ? intent : null;
        }
    }

    /// <summary>Seeds a record without going through the placement path (sweeper arranges).</summary>
    public void Seed(HoldIntent intent)
    {
        lock (_gate)
        {
            _intents[intent.OfferId] = intent;
        }
    }

    public Task WriteAsync(HoldIntent intent, CancellationToken ct)
    {
        lock (_gate)
        {
            WriteCalls++;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                return Task.FromException(
                    new InvalidOperationException("simulated hold-intent write failure"));
            }

            _intents[intent.OfferId] = intent;
            return Task.CompletedTask;
        }
    }

    public Task<HoldIntent?> GetAsync(string offerId, CancellationToken ct)
        => Task.FromResult(Peek(offerId));

    /// <summary>Prefix-scan outage: the HOLD pass must skip, and the refund pass must still run.</summary>
    public bool FailEnumeration { get; set; }

    public Task<IReadOnlyList<HoldIntent>> ListAllAsync(CancellationToken ct)
        => FailEnumeration
            ? Task.FromException<IReadOnlyList<HoldIntent>>(
                new InvalidOperationException("simulated hold-intent enumeration failure"))
            : Task.FromResult(Snapshot());

    public Task CloseAsync(string offerId, CancellationToken ct)
    {
        lock (_gate)
        {
            CloseCalls++;
            // Tombstone, not a delete: the record stays enumerable with State=closed, which
            // the sweeper and the abort tool treat as absent.
            if (_intents.TryGetValue(offerId, out var intent))
            {
                _intents[offerId] = intent with { State = ClosedState };
            }

            return Task.CompletedTask;
        }
    }
}
