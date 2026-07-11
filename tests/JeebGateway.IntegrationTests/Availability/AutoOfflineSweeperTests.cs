using FluentAssertions;
using JeebGateway.Availability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Availability;

/// <summary>
/// JEBV4-148 — per-record resilience in <see cref="AutoOfflineSweeper"/>.
///
/// <para>At Offer=true the live <c>UpstreamPendingOffersStore.WithdrawForJeeberAsync</c>
/// throws <see cref="NotSupportedException"/> (offer-service has no bulk
/// withdraw-for-jeeber route), so <c>IAvailabilityStore.GoOfflineAsync</c> throws
/// for that Jeeber. Before the fix that throw escaped the per-record
/// <c>foreach</c> and aborted the ENTIRE sweep cycle — only the outer
/// <see cref="AutoOfflineSweeper.SweepOnceAsync"/> guard stopped a crash — so
/// every remaining stale Jeeber was left online and auto-offline stayed
/// perpetually degraded.</para>
///
/// <para>The fix wraps the per-record GoOffline + notify in a try/catch that logs
/// and skips a faulting record (mirroring the N13 best-effort mirror in
/// <c>AvailabilityController.TryGoOfflineMirrorAsync</c>) while propagating
/// cancellation. These are pure unit tests over a scripted store — no host, no
/// Docker.</para>
/// </summary>
public class AutoOfflineSweeperTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sweep_Continues_Over_Remaining_Records_When_First_GoOffline_Throws()
    {
        // Three stale online Jeebers; the FIRST throws on GoOfflineAsync,
        // simulating the Offer=true upstream NotSupportedException.
        var store = new ScriptedAvailabilityStore(
            online: new[] { StaleRecord("jeeber-1"), StaleRecord("jeeber-2"), StaleRecord("jeeber-3") },
            throwForUserId: "jeeber-1");
        var sweeper = BuildSweeper(store, out var notifier);

        // Must NOT throw — the sweep completes despite the first record faulting.
        await sweeper.SweepOnceAsync(CancellationToken.None);

        // Every record was attempted, including the two AFTER the throwing one.
        // (Pre-fix this is ["jeeber-1"] only, because the throw aborts the loop.)
        store.GoOfflineCalls.Should().Equal(
            new[] { "jeeber-1", "jeeber-2", "jeeber-3" },
            "one faulting record must not abort the sweep over the rest");

        // The two that succeeded still fired their auto-offline notification.
        notifier.Sent.Select(s => s.UserId).Should()
            .BeEquivalentTo(new[] { "jeeber-2", "jeeber-3" });
    }

    [Fact]
    public async Task Sweep_Propagates_Cancellation_Instead_Of_Swallowing_It()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var store = new ScriptedAvailabilityStore(
            online: new[] { StaleRecord("jeeber-1"), StaleRecord("jeeber-2") },
            throwForUserId: "jeeber-1",
            throwWith: new OperationCanceledException());
        var sweeper = BuildSweeper(store, out _);

        // A cancellation during a record must NOT be caught-and-skipped as a
        // per-record miss; it propagates so ExecuteAsync can break the loop.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sweeper.SweepOnceAsync(cts.Token));

        store.GoOfflineCalls.Should().Equal(
            new[] { "jeeber-1" },
            "cancellation stops the sweep, it does not skip to the next record");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static JeeberAvailability StaleRecord(string userId) => new()
    {
        UserId = userId,
        IsOnline = true,
        // Idle for an hour — well past the default 30-min inactivity window,
        // so the sweeper attempts to flip this record offline.
        LastInteractionAt = Now - TimeSpan.FromHours(1),
        LastSeenAt = Now - TimeSpan.FromHours(1),
    };

    private static AutoOfflineSweeper BuildSweeper(
        IAvailabilityStore store,
        out InMemoryAutoOfflineNotifier notifier)
    {
        notifier = new InMemoryAutoOfflineNotifier();

        // A real DI container so the sweeper's CreateScope()/GetRequiredService
        // path runs exactly as in production, just against the scripted store.
        var services = new ServiceCollection();
        services.AddSingleton<IAvailabilityStore>(store);
        services.AddSingleton<IAutoOfflineNotifier>(notifier);
        var provider = services.BuildServiceProvider();

        return new AutoOfflineSweeper(
            provider,
            new FixedClock(Now),
            Options.Create(new AutoOfflineOptions()),
            NullLogger<AutoOfflineSweeper>.Instance);
    }

    /// <summary>
    /// In-memory <see cref="IAvailabilityStore"/> that returns a fixed online
    /// snapshot, records every <c>GoOfflineAsync</c> call in order, and throws a
    /// configured exception for one target Jeeber id. Only the two members the
    /// sweeper touches are implemented.
    /// </summary>
    private sealed class ScriptedAvailabilityStore : IAvailabilityStore
    {
        private readonly IReadOnlyList<JeeberAvailability> _online;
        private readonly string _throwForUserId;
        private readonly Exception _throwWith;
        private readonly List<string> _goOfflineCalls = new();

        public ScriptedAvailabilityStore(
            IReadOnlyList<JeeberAvailability> online,
            string throwForUserId,
            Exception? throwWith = null)
        {
            _online = online;
            _throwForUserId = throwForUserId;
            _throwWith = throwWith ?? new NotSupportedException(
                "offer-service exposes no bulk withdraw-for-jeeber route (Offer=true).");
        }

        public IReadOnlyList<string> GoOfflineCalls => _goOfflineCalls;

        public Task<IReadOnlyList<JeeberAvailability>> ListOnlineAsync(CancellationToken ct)
            => Task.FromResult(_online);

        public Task<GoOfflineResult> GoOfflineAsync(string userId, GoOfflineReason reason, CancellationToken ct)
        {
            _goOfflineCalls.Add(userId);
            if (userId == _throwForUserId)
            {
                throw _throwWith;
            }

            return Task.FromResult(new GoOfflineResult
            {
                Availability = new JeeberAvailability { UserId = userId },
                WithdrawnOffers = 0,
                WasOnline = true,
            });
        }

        // Not exercised by the sweeper.
        public Task<JeeberAvailability> GetAsync(string userId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<GoOnlineResult> GoOnlineAsync(string userId, GoOnlineRequest request, CancellationToken ct)
            => throw new NotImplementedException();

        public Task RecordInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
