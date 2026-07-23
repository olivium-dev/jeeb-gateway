using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner;

/// <summary>
/// In-memory <see cref="IPartnerWalletOperationStore"/> for dev/CI/test ONLY. Holds claims in a
/// process <see cref="ConcurrentDictionary{TKey,TValue}"/> — the record evaporates on restart, so it
/// is a data-loss hole for a money path and is refused fail-closed in prod-like environments by
/// <see cref="JeebGateway.Infrastructure.StoreDurabilityGuard"/> (the durable
/// <see cref="PostgresPartnerWalletOperationStore"/> is used whenever GatewayPostgres is configured).
///
/// <para>The claim/complete/release/uncertain state machine is preserved byte-for-byte from the
/// Postgres impl: <see cref="TryClaimAsync"/> is atomic on the composite key via
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> (mirrors <c>INSERT ... ON CONFLICT DO
/// NOTHING</c>), a completed claim replays its stored result, and a pending/uncertain claim is
/// InFlight (409).</para>
/// </summary>
public sealed class InMemoryPartnerWalletOperationStore : IPartnerWalletOperationStore
{
    private enum State { Pending, Completed, Uncertain }

    private sealed record Entry(State State, PartnerWalletMoveResponse? Result);

    private readonly ConcurrentDictionary<PartnerOperationKey, Entry> _claims = new();

    public Task<PartnerOperationClaim> TryClaimAsync(
        PartnerOperationKey key, PartnerOperationIntent intent, CancellationToken ct)
    {
        var pending = new Entry(State.Pending, null);

        // Atomic first-claim: TryAdd inserts iff absent (the ON CONFLICT DO NOTHING analogue).
        if (_claims.TryAdd(key, pending))
        {
            return Task.FromResult(new PartnerOperationClaim(PartnerClaimKind.Won, null));
        }

        // Key already exists — resolve its state.
        if (_claims.TryGetValue(key, out var existing))
        {
            return existing.State switch
            {
                State.Completed => Task.FromResult(
                    new PartnerOperationClaim(PartnerClaimKind.Replay, existing.Result)),
                _ => Task.FromResult(
                    new PartnerOperationClaim(PartnerClaimKind.InFlight, null)), // pending / uncertain
            };
        }

        // Raced with a Release that removed the row between TryAdd and TryGetValue — retry once as a
        // fresh claim so the caller still gets a definite Won/Replay/InFlight (never a lost claim).
        return _claims.TryAdd(key, pending)
            ? Task.FromResult(new PartnerOperationClaim(PartnerClaimKind.Won, null))
            : Task.FromResult(new PartnerOperationClaim(PartnerClaimKind.InFlight, null));
    }

    public Task CompleteAsync(
        PartnerOperationKey key, Guid transactionId, PartnerWalletMoveResponse result, CancellationToken ct)
    {
        _claims[key] = new Entry(State.Completed, result);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(PartnerOperationKey key, CancellationToken ct)
    {
        // Only free a still-pending claim; never drop a completed/uncertain record.
        _claims.TryGetValue(key, out var existing);
        if (existing is { State: State.Pending })
        {
            _claims.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    public Task MarkUncertainAsync(PartnerOperationKey key, CancellationToken ct)
    {
        _claims.AddOrUpdate(
            key,
            _ => new Entry(State.Uncertain, null),
            (_, existing) => existing.State == State.Completed
                ? existing                                   // a completed row is never downgraded
                : new Entry(State.Uncertain, existing.Result));
        return Task.CompletedTask;
    }
}
