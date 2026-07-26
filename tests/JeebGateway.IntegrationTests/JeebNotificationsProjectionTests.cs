using System.Collections.Generic;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.JeebNotifications;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Unit coverage of the generic→Jeeb notifications-inbox projection that lives in the
/// gateway (ADR-0001 thin map): the mobile <c>{ id, type, title, body, ts, read, ref }</c>
/// row shape the <c>DioNotificationsRepository._item</c> parser reads, the upstream
/// <c>status</c>→boolean <c>read</c> reduction, and the cold-start empty page used when
/// the upstream returns no rows. These bypass HTTP/DI — mirroring
/// <see cref="JeebReviewsProjectionTests"/> / <see cref="JeebWalletProjectionTests"/>.
/// </summary>
public class JeebNotificationsProjectionTests
{
    private static UpstreamNotificationRow Row(
        string id = "n-1",
        string? type = "offer",
        string? title = "New offer",
        string? body = "You have an offer",
        string? ts = "2026-06-20T10:00:00Z",
        string? status = "delivered",
        string? @ref = "delivery-9")
        => new()
        {
            Id = id, Type = type, Title = title, Body = body,
            Timestamp = ts, Status = status, Ref = @ref,
        };

    // ── empty / cold-start page ───────────────────────────────────────────────────

    [Fact]
    public void ProjectPage_Null_Rows_Is_Empty_Page()
    {
        var page = JeebNotificationsProjection.ProjectPage(null, page: 1, pageSize: 20);

        page.Items.Should().BeEmpty();
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
        page.TotalCount.Should().Be(0);
        page.TotalPages.Should().Be(1);
    }

    [Fact]
    public void ProjectPage_Clamps_NonPositive_Paging_To_Safe_Defaults()
    {
        var page = JeebNotificationsProjection.ProjectPage(new List<UpstreamNotificationRow>(), page: 0, pageSize: 0);

        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
    }

    // ── row projection (mobile shape) ─────────────────────────────────────────────

    [Fact]
    public void ProjectItem_Maps_All_Fields_To_Mobile_Shape()
    {
        var item = JeebNotificationsProjection.ProjectItem(Row(status: "delivered"));

        item.Id.Should().Be("n-1");
        item.Type.Should().Be("offer");
        item.Title.Should().Be("New offer");
        item.Body.Should().Be("You have an offer");
        item.Ts.Should().Be("2026-06-20T10:00:00Z");
        item.Ref.Should().Be("delivery-9");
        // status != "read" → unread row.
        item.Read.Should().BeFalse();
    }

    [Theory]
    [InlineData("read", true)]
    [InlineData("READ", true)]
    [InlineData(" read ", true)]
    [InlineData("delivered", false)]
    [InlineData("unread", false)]
    [InlineData(null, false)]
    public void IsRead_Reduces_Upstream_Status_To_Boolean(string? status, bool expected)
    {
        JeebNotificationsProjection.IsRead(status).Should().Be(expected);
    }

    [Fact]
    public void ProjectItem_Read_Status_Yields_Read_True()
    {
        var item = JeebNotificationsProjection.ProjectItem(Row(status: "read"));
        item.Read.Should().BeTrue();
    }

    [Fact]
    public void ProjectItem_Blank_Optional_Fields_Become_Null_Or_Empty()
    {
        var item = JeebNotificationsProjection.ProjectItem(Row(type: "  ", @ref: "", title: null, body: null, ts: null));

        // Optional type/ref blank → null (omitted on the wire); required strings → empty.
        item.Type.Should().BeNull();
        item.Ref.Should().BeNull();
        item.Title.Should().BeEmpty();
        item.Body.Should().BeEmpty();
        item.Ts.Should().BeEmpty();
        item.Id.Should().Be("n-1");
    }

    // ── page aggregation ──────────────────────────────────────────────────────────

    [Fact]
    public void ProjectPage_Preserves_Order_And_Counts_Rows_When_No_Upstream_Total()
    {
        var rows = new List<UpstreamNotificationRow>
        {
            Row(id: "n-1", status: "read"),
            Row(id: "n-2", status: "delivered"),
        };

        var page = JeebNotificationsProjection.ProjectPage(rows, page: 1, pageSize: 20);

        page.Items.Should().HaveCount(2);
        page.Items[0].Id.Should().Be("n-1");
        page.Items[0].Read.Should().BeTrue();
        page.Items[1].Id.Should().Be("n-2");
        page.Items[1].Read.Should().BeFalse();
        page.TotalCount.Should().Be(2);
        page.TotalPages.Should().Be(1);
    }

    [Fact]
    public void ProjectPage_Uses_Upstream_Total_For_Paging()
    {
        var rows = new List<UpstreamNotificationRow> { Row(id: "n-1") };

        var page = JeebNotificationsProjection.ProjectPage(rows, page: 1, pageSize: 10, upstreamTotal: 25);

        page.TotalCount.Should().Be(25);
        page.TotalPages.Should().Be(3); // ceil(25/10)
    }

    // ── FM-1 real-wire extraction (R2 / R19 / D5) ──────────────────────────

    [Fact]
    public void ExtractRows_CapturedOfferRow_NormalizesTypeTimestampAndPayloadRef()
    {
        var wire = Fm1NotificationWireFixtures.CapturedOfferReceived();

        var (rows, total) = JeebNotificationsInboxController.ExtractRowsForTests(wire);

        rows.Should().HaveCount(3);
        rows[0].Id.Should().Be("00468148-d722-445a-97a1-4e39b87dafb3");
        rows[0].Type.Should().Be("offer");
        rows[0].Timestamp.Should().Be("2026-07-26T00:00:00.0000000");
        rows[0].Ref.Should().Be("OFR-PROBE-3");
        total.Should().Be(3);
    }

    [Fact]
    public void ExtractRows_ConstructedDegeneratePayloadIds_LeaveRefNull_NoThrow()
    {
        var wire = Fm1NotificationWireFixtures.ConstructedDegenerateOfferPayloads();

        var act = () => JeebNotificationsInboxController.ExtractRowsForTests(wire);

        var result = act.Should().NotThrow().Subject;
        result.Rows.Should().HaveCount(6);
        result.Rows.Should().OnlyContain(row => row.Ref == null);
    }

    [Fact]
    public void ExtractRows_ConstructedWireExistingTopLevelAliasWinsOverPayloadFallback()
    {
        var wire = Fm1NotificationWireFixtures.DeliveryWithTopLevelAndPayloadIds();

        var (rows, _) = JeebNotificationsInboxController.ExtractRowsForTests(wire);

        rows[0].Type.Should().Be("delivery_status_updated");
        rows[0].Ref.Should().Be("TOP-LEVEL-DELIVERY");
    }

    [Fact]
    public void ExtractRows_ConstructedOfferAcceptedDoesNotHoistOfferIdIntoChatRef()
    {
        var wire = Fm1NotificationWireFixtures.OfferAccepted();

        var (rows, _) = JeebNotificationsInboxController.ExtractRowsForTests(wire);

        rows[0].Type.Should().Be("offer_accepted");
        rows[0].Ref.Should().BeNull();
    }

    [Fact]
    public void ExtractRows_CapturedDuplicateNotificationIdsKeepFirstRowOnly()
    {
        var wire = Fm1NotificationWireFixtures.CapturedA5DuplicateEnvelope();

        var (rows, total) = JeebNotificationsInboxController.ExtractRowsForTests(wire);

        rows.Should().ContainSingle();
        rows[0].Id.Should().Be("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0001");
        total.Should().Be(2);
    }

    /// <summary>
    /// AC-4: the captured <c>jeeb.offer_received</c> payload remains numeric and
    /// location-exact at the closest payload-bearing boundary after the gateway's
    /// real <c>ExtractRows</c> and inbox projection path.
    /// </summary>
    [Fact]
    public void AC4()
    {
        var wire = Fm1NotificationWireFixtures.CapturedAc4Ac5LiveRoundTrip();

        var payload = ProjectAndSelectPayload(
            wire,
            wireType: "jeeb.offer_received",
            projectedType: "offer",
            notificationId: "ac400000-0000-4000-8000-000000000004");

        AssertNumericAmountAndExactPickup(payload, "offer_amount");
    }

    /// <summary>
    /// AC-5: the captured <c>jeeb.offer_accepted</c> payload remains numeric and
    /// location-exact at the closest payload-bearing boundary after the gateway's
    /// real <c>ExtractRows</c> and inbox projection path.
    /// </summary>
    [Fact]
    public void AC5()
    {
        var wire = Fm1NotificationWireFixtures.CapturedAc4Ac5LiveRoundTrip();

        var payload = ProjectAndSelectPayload(
            wire,
            wireType: "jeeb.offer_accepted",
            projectedType: "offer_accepted",
            notificationId: "ac500000-0000-4000-8000-000000000005");

        AssertNumericAmountAndExactPickup(payload, "accepted_amount");
    }

    private static JObject ProjectAndSelectPayload(
        JObject wire,
        string wireType,
        string projectedType,
        string notificationId)
    {
        var (rows, total) = JeebNotificationsInboxController.ExtractRowsForTests(wire);
        var page = JeebNotificationsProjection.ProjectPage(rows, page: 1, pageSize: 10, total);

        total.Should().Be(2);
        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle(item =>
            item.Id == notificationId && item.Type == projectedType);

        var messages = wire["messages"].Should().BeOfType<JArray>().Subject;
        var message = messages.Children<JObject>().Single(item =>
            item.Value<string>("notification_type") == wireType);
        return message["payload"].Should().BeOfType<JObject>().Subject;
    }

    private static void AssertNumericAmountAndExactPickup(JObject payload, string amountField)
    {
        var amount = payload[amountField];
        amount.Should().NotBeNull();
        amount.Should().BeOfType<JValue>();
        amount!.Type.Should().NotBe(JTokenType.String);
        amount.Type.Should().NotBe(JTokenType.Object);
        amount.Type.Should().NotBe(JTokenType.Array);
        amount.Type.Should().Be(JTokenType.Float);
        amount.Value<decimal>().Should().Be(12.5m);
        amount.ToString(Newtonsoft.Json.Formatting.None).Should().Be("12.5");

        var pickupLocation = payload["pickup_location"];
        pickupLocation.Should().NotBeNull();
        pickupLocation!.Type.Should().Be(JTokenType.String);
        pickupLocation.Value<string>().Should().Be("Hamra, Beirut");
    }
}

/// <summary>
/// Literal notification wire fixtures. Files prefixed <c>captured-</c> are exact
/// read-only MSI responses; files prefixed <c>constructed-</c> are explicitly
/// labelled environmental-limit cases that the strict live schema cannot store.
/// </summary>
internal static class Fm1NotificationWireFixtures
{
    public static JObject CapturedOfferReceived()
        => Load("captured-offer-received-page.json");

    public static JObject CapturedA5DuplicateEnvelope()
        => Load("captured-a5-duplicate-page.json");

    public static JObject CapturedAc4Ac5LiveRoundTrip()
        => Load("captured-ac4-ac5-live-roundtrip-page.json");

    public static JObject ConstructedDegenerateOfferPayloads()
        => Load("constructed-degenerate-offer-payloads-page.json");

    public static JObject DeliveryWithTopLevelAndPayloadIds()
        => Load("constructed-delivery-precedence-page.json");

    public static JObject OfferAccepted()
        => Load("constructed-offer-accepted-page.json");

    public static JObject JeeberBroadcast()
        => Load("constructed-jeeber-broadcast-page.json");

    public static JObject OffersSharingOneOfferId()
        => Load("constructed-shared-offer-id-page.json");

    public static JObject ConstructedOfferWithTopLevelRequestRef()
        => Load("constructed-top-level-offer-ref-page.json");

    public static JObject ConstructedOfferResolutionCap()
        => Load("constructed-offer-resolution-cap-page.json");

    private static JObject Load(string fileName)
        => JObject.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "FM1",
            fileName)));
}
