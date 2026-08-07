using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.IntegrationTests.Fakes;
using Xunit;

namespace JeebGateway.IntegrationTests.Availability;

/// <summary>RC-2a — RecordInteractionAsync is touch-existing-only (lockstep with Postgres):
/// a plain GET must never CREATE a roster row; row creation is GoOnlineAsync's alone.</summary>
public class InMemoryAvailabilityStoreTests
{
    private static InMemoryAvailabilityStore NewStore()
        => new(new InMemoryGeoIndex(), new FakePendingOffersStore(TimeProvider.System), TimeProvider.System);

    private static GoOnlineRequest OnlineRequest(string zone = "beirut")
        => new() { VehicleType = VehicleType.Car, Zone = zone, Longitude = 35.50, Latitude = 33.88 };

    [Fact] // (f) — a never-seen user must not be seeded onto the fan-out roster by a read.
    public async Task RecordInteraction_On_NeverSeen_User_Creates_No_Row()
    {
        var store = NewStore();

        await store.RecordInteractionAsync("ghost", DateTimeOffset.UtcNow, CancellationToken.None);

        (await store.ListKnownJeebersAsync(DateTimeOffset.MinValue, CancellationToken.None))
            .Should().BeEmpty("row creation is GoOnlineAsync's alone");
        (await store.ListOnlineAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact] // (f) — on an existing row only the interaction watermark moves.
    public async Task RecordInteraction_On_Existing_Row_Updates_LastInteractionAt_Only()
    {
        var store = NewStore();
        await store.GoOnlineAsync("j1", OnlineRequest(), CancellationToken.None);
        var before = await store.GetAsync("j1", CancellationToken.None);

        var at = before.LastInteractionAt!.Value.AddMinutes(5);
        await store.RecordInteractionAsync("j1", at, CancellationToken.None);

        var after = await store.GetAsync("j1", CancellationToken.None);
        after.LastInteractionAt.Should().Be(at, "the auto-offline watermark must keep advancing");
        after.IsOnline.Should().BeTrue();
        after.Zone.Should().Be("beirut");
        after.VehicleType.Should().Be(VehicleType.Car);
        after.LastSeenAt.Should().Be(before.LastSeenAt);
    }

    [Fact] // (f) — the capability-gated toggle path still creates rows.
    public async Task GoOnline_Still_Creates_A_Row()
    {
        var store = NewStore();
        await store.RecordInteractionAsync("j2", DateTimeOffset.UtcNow, CancellationToken.None);

        await store.GoOnlineAsync("j2", OnlineRequest(zone: "tripoli"), CancellationToken.None);

        (await store.GetAsync("j2", CancellationToken.None)).IsOnline.Should().BeTrue();
        (await store.ListKnownJeebersAsync(DateTimeOffset.MinValue, CancellationToken.None))
            .Should().ContainSingle(r => r.UserId == "j2");
    }
}
