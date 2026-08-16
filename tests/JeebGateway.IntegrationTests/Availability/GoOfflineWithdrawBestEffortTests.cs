using System.Text.RegularExpressions;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.IntegrationTests.Fakes;
using Xunit;

namespace JeebGateway.IntegrationTests.Availability;

/// <summary>
/// gwdbx W3-13 (A14 device proof) — found on LIVE, not in CI.
///
/// <para>An idle jeeber WAS flipped offline in the gateway's own table, but the
/// upstream mirror never followed and the auto-offline push never fired. The live
/// stack trace put the throw at <c>AutoOfflineSweeper.SweepOnceAsync</c> line 85,
/// i.e. inside <c>store.GoOfflineAsync</c>: the durable offline row commits, then
/// <c>WithdrawForJeeberAsync</c> throws <see cref="NotSupportedException"/>
/// (offer-service has no bulk withdraw route, JEBV4-148) and unwinds the method —
/// so the sweeper's following <c>MirrorIdleOfflineAsync</c> and
/// <c>NotifyAutoOfflineAsync</c> calls were never reached.</para>
///
/// <para>Consequence, and why this blocks the W3-13 read flip: delivery-service kept
/// listing an auto-offlined jeeber as online, so flipping the read to upstream would
/// have made auto-offline inert rather than authoritative.</para>
///
/// <para><see cref="AutoOfflineSweeperTests"/> covers the sweeper surviving a record
/// that genuinely faults. This covers the layer beneath: a withdraw with no upstream
/// implementation is not a fault at all, because the offline write already committed.</para>
/// </summary>
public class GoOfflineWithdrawBestEffortTests
{
    // Re-listing the interface re-maps it for the derived type, so the store's
    // interface-typed call reaches this override rather than the base fake.
    private sealed class ThrowingWithdrawOffersStore : FakePendingOffersStore, IPendingOffersStore
    {
        public ThrowingWithdrawOffersStore() : base(TimeProvider.System) { }

        Task<int> IPendingOffersStore.WithdrawForJeeberAsync(string jeeberId, CancellationToken ct)
            => throw new NotSupportedException(
                "offer-service exposes no bulk withdraw-for-jeeber route");
    }

    [Fact]
    public async Task GoOffline_Still_Returns_When_Withdraw_Has_No_Upstream_Route()
    {
        var offers = new ThrowingWithdrawOffersStore();
        var store = new InMemoryAvailabilityStore(
            new InMemoryGeoIndex(), offers, TimeProvider.System);

        await store.GoOnlineAsync("jeeber-1", new GoOnlineRequest
        {
            VehicleType = VehicleType.Car,
            Zone = "beirut-central",
            Latitude = 33.8886,
            Longitude = 35.4955
        }, CancellationToken.None);

        // Pre-fix this throws, and the sweeper's mirror + push never run.
        var result = await store.GoOfflineAsync(
            "jeeber-1", GoOfflineReason.AutoOfflineInactive, CancellationToken.None);

        result.WasOnline.Should().BeTrue("the caller decides whether to mirror and push from this bit");
        result.WithdrawnOffers.Should().Be(0, "no route means nothing was withdrawn — not that the call failed");
        result.Availability.IsOnline.Should().BeFalse();

        var after = await store.GetAsync("jeeber-1", CancellationToken.None);
        after.IsOnline.Should().BeFalse("the offline flip must survive the withdraw having no implementation");
    }

    [Fact]
    public async Task Transient_Withdraw_Faults_Still_Propagate()
    {
        // Control: absorbing NotSupportedException must not turn into absorbing
        // everything, or a real upstream outage would go silent.
        var store = new InMemoryAvailabilityStore(
            new InMemoryGeoIndex(), new ThrowingTransientOffersStore(), TimeProvider.System);

        await store.GoOnlineAsync("jeeber-2", new GoOnlineRequest
        {
            VehicleType = VehicleType.Car,
            Zone = "beirut-central",
            Latitude = 33.8886,
            Longitude = 35.4955
        }, CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            store.GoOfflineAsync("jeeber-2", GoOfflineReason.AutoOfflineInactive, CancellationToken.None));
    }

    private sealed class ThrowingTransientOffersStore : FakePendingOffersStore, IPendingOffersStore
    {
        public ThrowingTransientOffersStore() : base(TimeProvider.System) { }

        Task<int> IPendingOffersStore.WithdrawForJeeberAsync(string jeeberId, CancellationToken ct)
            => throw new HttpRequestException("offer-service unreachable");
    }

    // DELETED — PostgresStore_Routes_Its_Withdraw_Through_The_Same_Best_Effort_Guard.
    // It read src/JeebGateway/Availability/PostgresAvailabilityStore.cs to pin structurally
    // that the Postgres twin routed its withdraw through WithdrawBestEffortAsync rather than a
    // raw await. W5-11 (8cba63b) deleted that file and the whole GatewayPostgres seam, so the
    // subject no longer exists in any form. The behavioural cases above still cover the guard
    // on the surviving in-memory store, which is now the only availability store there is.

}
