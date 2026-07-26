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

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"user_id":"FM1-PROBE-b02-20260726"}""")]
    [InlineData("""{"offer_id":null}""")]
    [InlineData("""{"offer_id":""}""")]
    [InlineData("""{"offer_id":[]}""")]
    [InlineData("""{"offer_id":{"valueKind":1}}""")]
    public void ExtractRows_CapturedWireDegeneratePayloadIds_LeaveRefNull_NoThrow(
        string payloadJson)
    {
        var wire = Fm1NotificationWireFixtures.OfferReceivedWithPayload(payloadJson);

        var act = () => JeebNotificationsInboxController.ExtractRowsForTests(wire);

        var result = act.Should().NotThrow().Subject;
        result.Rows[0].Ref.Should().BeNull();
    }

    [Fact]
    public void ExtractRows_CapturedWireExistingTopLevelAliasWinsOverPayloadFallback()
    {
        var wire = Fm1NotificationWireFixtures.DeliveryWithTopLevelAndPayloadIds();

        var (rows, _) = JeebNotificationsInboxController.ExtractRowsForTests(wire);

        rows[0].Type.Should().Be("delivery_status_updated");
        rows[0].Ref.Should().Be("TOP-LEVEL-DELIVERY");
    }

    [Fact]
    public void ExtractRows_CapturedOfferAcceptedDoesNotHoistOfferIdIntoChatRef()
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
}

/// <summary>
/// Raw notification-service wire fixtures captured read-only from MSI on 2026-07-26.
/// Sources: receivers FM1-PROBE-b02-20260726 and FM1-PROBE-A5A6-b02.
/// Degenerate cases mutate the raw JSON string before parsing; no hand-built
/// <see cref="JObject"/> crosses the serialization boundary under test (R19).
/// </summary>
internal static class Fm1NotificationWireFixtures
{
    private const string Probe3Payload =
        """{"user_id":"FM1-PROBE-b02-20260726","offer_id":"OFR-PROBE-3","client_name":"probe","pickup_location":"A","delivery_location":"B","offer_amount":1.5,"delivery_fee":0.5,"estimated_duration":"10m","created_at":"2026-07-26T00:00:00"}""";

    private const string CapturedOfferReceivedJson =
        """
        {"receiver_id":"FM1-PROBE-b02-20260726","page":1,"page_size":20,"total_messages":3,"total_pages":1,"has_next":false,"has_previous":false,"read_status_filter":"all","message_counts":{"total_for_receiver":3,"read":0,"unread":3},"filters_applied":{},"messages":[{"_id":"6a65638e35aea126c97f0655","sender":"jeeb-gateway","receiver":"FM1-PROBE-b02-20260726","notification_id":"00468148-d722-445a-97a1-4e39b87dafb3","title":"probe3","subtitle":"probe3","description":"FM-1 probe3 - safe to delete","media_links":["jeeb://orders/REQ-PROBE-XYZ"],"status":"not delivered","deactivated":false,"notification_type":"jeeb.offer_received","senderProfilePicture":"","nickname":"","payload":{"user_id":"FM1-PROBE-b02-20260726","offer_id":"OFR-PROBE-3","client_name":"probe","pickup_location":"A","delivery_location":"B","offer_amount":1.5,"delivery_fee":0.5,"estimated_duration":"10m","created_at":"2026-07-26T00:00:00"}},{"_id":"6a65636135aea126c97f0654","sender":"jeeb-gateway","receiver":"FM1-PROBE-b02-20260726","notification_id":"bf7575e8-90a4-48ba-8020-f45106650bae","title":"probe","subtitle":"probe","description":"FM-1 control probe - safe to delete","media_links":[],"status":"not delivered","deactivated":false,"notification_type":"jeeb.offer_received","senderProfilePicture":null,"nickname":null,"payload":{"user_id":"FM1-PROBE-b02-20260726","offer_id":"OFR-PROBE-2","client_name":"probe","pickup_location":"A","delivery_location":"B","offer_amount":1.5,"delivery_fee":0.5,"estimated_duration":"10m","created_at":"2026-07-26T00:00:00"}},{"_id":"6a65635635aea126c97f0653","sender":"jeeb-gateway","receiver":"FM1-PROBE-b02-20260726","notification_id":"69f1deb2-3d83-4358-937d-d85f7f00eb46","title":"probe","subtitle":"probe","description":"FM-1 extensibility probe - safe to delete","media_links":[],"status":"not delivered","deactivated":false,"notification_type":"jeeb.offer_received","senderProfilePicture":null,"nickname":null,"payload":{"user_id":"FM1-PROBE-b02-20260726","offer_id":"OFR-PROBE-1","client_name":"probe","pickup_location":"A","delivery_location":"B","offer_amount":1.5,"delivery_fee":0.5,"estimated_duration":"10m","created_at":"2026-07-26T00:00:00"}}]}
        """;

    private const string CapturedA5DuplicateJson =
        """
        {"receiver_id":"FM1-PROBE-A5A6-b02","page":1,"page_size":20,"total_messages":2,"total_pages":1,"has_next":false,"has_previous":false,"read_status_filter":"all","message_counts":{"total_for_receiver":2,"read":0,"unread":2},"filters_applied":{},"messages":[{"_id":"6a656f1a35aea126c97f0658","sender":"jeeb-gateway","receiver":"FM1-PROBE-A5A6-b02","notification_id":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0001","title":"a5","subtitle":"a5","description":"A5/A6 probe - safe to delete","media_links":[],"status":"not delivered","deactivated":false,"notification_type":"jeeb.offer_received","senderProfilePicture":"","nickname":"","payload":{"user_id":"FM1-PROBE-A5A6-b02","offer_id":"OFR-A5","client_name":"c","pickup_location":"A","delivery_location":"B","offer_amount":12.5,"delivery_fee":0.5,"estimated_duration":"10m","created_at":"2026-07-26T00:00:00"}},{"_id":"6a656f1a35aea126c97f0657","sender":"jeeb-gateway","receiver":"FM1-PROBE-A5A6-b02","notification_id":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0001","title":"a5","subtitle":"a5","description":"A5/A6 probe - safe to delete","media_links":[],"status":"not delivered","deactivated":false,"notification_type":"jeeb.offer_received","senderProfilePicture":"","nickname":"","payload":{"user_id":"FM1-PROBE-A5A6-b02","offer_id":"OFR-A5","client_name":"c","pickup_location":"A","delivery_location":"B","offer_amount":12.5,"delivery_fee":0.5,"estimated_duration":"10m","created_at":"2026-07-26T00:00:00"}}]}
        """;

    public static JObject CapturedOfferReceived()
        => JObject.Parse(CapturedOfferReceivedJson);

    public static JObject CapturedA5DuplicateEnvelope()
        => JObject.Parse(CapturedA5DuplicateJson);

    public static JObject OfferReceivedWithPayload(string payloadJson)
        => JObject.Parse(CapturedOfferReceivedJson.Replace(
            $@"""payload"":{Probe3Payload}",
            $@"""payload"":{payloadJson}",
            StringComparison.Ordinal));

    public static JObject DeliveryWithTopLevelAndPayloadIds()
    {
        var wire = CapturedOfferReceivedJson
            .Replace(
                @"""notification_type"":""jeeb.offer_received""",
                @"""notification_type"":""jeeb.delivery_status_updated""",
                StringComparison.Ordinal)
            .Replace(
                @"""nickname"":"""",""payload"":",
                @"""nickname"":"""",""deliveryId"":""TOP-LEVEL-DELIVERY"",""payload"":",
                StringComparison.Ordinal)
            .Replace(
                $@"""payload"":{Probe3Payload}",
                @"""payload"":{""delivery_id"":""PAYLOAD-DELIVERY""}",
                StringComparison.Ordinal);

        return JObject.Parse(wire);
    }

    public static JObject OfferAccepted()
        => JObject.Parse(CapturedOfferReceivedJson.Replace(
            @"""notification_type"":""jeeb.offer_received""",
            @"""notification_type"":""jeeb.offer_accepted""",
            StringComparison.Ordinal));

    public static JObject JeeberBroadcast()
        => JObject.Parse(CapturedOfferReceivedJson.Replace(
            @"""notification_type"":""jeeb.offer_received""",
            @"""notification_type"":""jeeb.new_request""",
            StringComparison.Ordinal));

    public static JObject OffersSharingOneOfferId()
        => JObject.Parse(CapturedOfferReceivedJson
            .Replace("OFR-PROBE-2", "OFR-PROBE-3", StringComparison.Ordinal)
            .Replace("OFR-PROBE-1", "OFR-PROBE-3", StringComparison.Ordinal));
}
