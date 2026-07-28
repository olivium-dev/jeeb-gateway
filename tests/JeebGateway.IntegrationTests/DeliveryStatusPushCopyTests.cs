using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// "Status changed from Picked to Picked." — the copy bug, pinned.
///
/// <para>Two customer screenshots carried this sentence, and a second one reading
/// <c>"Status changed from AtDoor to AtDoor."</c>. The previous status was being rendered
/// as the new one.</para>
///
/// <para><b>Root cause (pinned by <see cref="GetAsync_Returns_The_LIVE_Row_That_SetStatusAsync_Mutates"/>).</b>
/// <c>DeliveriesController.PatchStatusViaDeliveryServiceAsync</c> pre-read the delivery row
/// for the push recipients, then committed the transition, then mirrored the new status
/// with <c>_store.SetStatusAsync</c>, and only THEN read <c>notifyRow.Status</c> as the
/// push's "previous". <see cref="InMemoryRequestsStore.GetAsync"/> hands back the
/// dictionary's own object with no defensive copy and <c>SetStatusAsync</c> mutates
/// <c>existing.Status</c> in place — so the "previous" it read was the value the mirror had
/// already written. The fix snapshots the status STRING at pre-read time.</para>
///
/// <para><b>Second line of defence (<see cref="DeliveryStatusPushCopy"/>).</b> from == to
/// is reachable with no aliasing defect at all — a client idempotently re-PATCHing the
/// status a delivery already holds. The copy no longer asserts a change in that case.</para>
///
/// Deliberately host-free unit tests: no WebApplicationFactory, no container, no upstream.
/// </summary>
public class DeliveryStatusPushCopyTests
{
    [Theory]
    [InlineData("Picked")]
    [InlineData("AtDoor")]
    [InlineData("InTransit")]
    public void Identical_From_And_To_Never_Renders_As_A_Change(string status)
    {
        var body = DeliveryStatusPushCopy.StatusChangeBody(status, status);

        body.Should().NotContain("changed from",
            "\"Status changed from X to X.\" is never a true sentence — it is the exact "
            + "string two customer devices displayed");
        body.Should().Be($"Status is now {status}.");
    }

    [Fact]
    public void Case_Insensitive_Match_Also_Counts_As_No_Change()
    {
        // The wire vocabulary is not consistently cased across the read-model and the
        // canonical tokens (e.g. "delivered" vs "Delivered"), so an ordinal compare
        // would let the false sentence back in through the side door.
        DeliveryStatusPushCopy.StatusChangeBody("atdoor", "AtDoor")
            .Should().Be("Status is now AtDoor.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unknown_Previous_Status_States_The_Current_One(string? previous)
    {
        DeliveryStatusPushCopy.StatusChangeBody(previous, "InTransit")
            .Should().Be("Status is now InTransit.",
                "with no known 'from' there is no transition to describe");
    }

    [Fact]
    public void A_Real_Transition_Still_Reads_As_A_Transition()
    {
        // NEGATIVE TEST: the fix must not flatten every push into "Status is now …".
        DeliveryStatusPushCopy.StatusChangeBody("Picked", "InTransit")
            .Should().Be("Status changed from Picked to InTransit.");
    }

    /// <summary>
    /// THE ALIASING MECHANISM, pinned. If this ever stops holding (the store starts
    /// returning defensive copies) the controller's snapshot becomes redundant rather
    /// than wrong — but while it holds, reading <c>row.Status</c> AFTER the mirror write
    /// is guaranteed to read the NEW status, which is the bug.
    /// </summary>
    [Fact]
    public async Task GetAsync_Returns_The_LIVE_Row_That_SetStatusAsync_Mutates()
    {
        var store = new InMemoryRequestsStore(TimeProvider.System);
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = "client-copy-bug",
            Description = "Pick up the parcel",
            RecipientPhone = "+9613123456"
        }, default);

        (await store.SetStatusAsync(created.Id, RequestStatus.PickedUp, default))
            .Should().BeTrue();

        var preRead = await store.GetAsync(created.Id, default);
        preRead.Should().NotBeNull();

        // What the controller now does: snapshot the STRING before anything writes.
        var snapshot = preRead!.Status;

        // The status mirror that runs after a committed transition.
        (await store.SetStatusAsync(created.Id, RequestStatus.HeadingOff, default))
            .Should().BeTrue();

        preRead.Status.Should().Be(RequestStatus.HeadingOff,
            "GetAsync returned the LIVE instance — the mirror advanced it in place, which "
            + "is why reading it here as the push's 'previous' produced from == to");
        snapshot.Should().Be(RequestStatus.PickedUp,
            "the snapshot is immune to the in-place mutation, which is the fix");

        DeliveryStatusPushCopy.StatusChangeBody(snapshot, preRead.Status)
            .Should().Be($"Status changed from {RequestStatus.PickedUp} to {RequestStatus.HeadingOff}.");
        DeliveryStatusPushCopy.StatusChangeBody(preRead.Status, preRead.Status)
            .Should().Be($"Status is now {RequestStatus.HeadingOff}.",
                "the pre-fix read path — now it at least cannot state a falsehood");
    }
}
