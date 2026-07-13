using System.Collections.Generic;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.JeebNotifications;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// F5 (JEBV4-302) — privacy leak: pure-customer callers received/read the
/// <c>jeeb_jeebers</c> new-request broadcast, which carries OTHER customers' order text.
/// The gateway closes the customer-facing READ path in
/// <see cref="JeebNotificationsInboxController.FilterJeeberBroadcasts"/>: rows whose
/// <c>type</c> is a jeeber-only broadcast (<c>new_request</c>) are dropped for any caller
/// who does not hold the jeeber ("driver") role in their available_roles. A dual-role
/// user (customer + jeeber) keeps the rows.
///
/// These are PURE tests of the filter (no HTTP, no Docker) — the authoritative,
/// deterministic half of the fix. The relay/notification-service topic-membership defect
/// (topic send → ALL users + persists non-subscriber receiver rows) is escalated to infra
/// as a separate ticket and is out of the gateway layer.
/// </summary>
public class F5PrivacyInboxFilterTests
{
    private static UpstreamNotificationRow Row(string id, string? type) => new()
    {
        Id = id,
        Type = type,
        Title = "New delivery request",
        Body = "Deliver a package from Souq to Salmiya — cash on delivery",
        Timestamp = "2026-07-13T10:00:00Z",
        Status = "unread",
        Ref = "req-" + id,
    };

    [Fact]
    public void NonJeeber_Caller_Has_NewRequest_Broadcast_Rows_Dropped()
    {
        var rows = new List<UpstreamNotificationRow>
        {
            Row("1", "new_request"),
            Row("2", "offer_received"),
            Row("3", "new_request"),
        };

        var (visible, dropped) =
            JeebNotificationsInboxController.FilterJeeberBroadcasts(rows, callerIsJeeber: false);

        dropped.Should().Be(2);
        visible.Should().ContainSingle();
        visible[0].Type.Should().Be("offer_received");
    }

    [Theory]
    [InlineData("new_request")]
    [InlineData("New_Request")]
    [InlineData(" new_request ")]
    public void Broadcast_Type_Match_Is_CaseInsensitive_And_Trimmed(string type)
    {
        var rows = new List<UpstreamNotificationRow> { Row("1", type) };

        var (visible, dropped) =
            JeebNotificationsInboxController.FilterJeeberBroadcasts(rows, callerIsJeeber: false);

        dropped.Should().Be(1);
        visible.Should().BeEmpty();
    }

    [Fact]
    public void Jeeber_Caller_Keeps_All_Rows_Including_Broadcasts()
    {
        var rows = new List<UpstreamNotificationRow>
        {
            Row("1", "new_request"),
            Row("2", "offer_received"),
        };

        var (visible, dropped) =
            JeebNotificationsInboxController.FilterJeeberBroadcasts(rows, callerIsJeeber: true);

        dropped.Should().Be(0);
        visible.Should().HaveCount(2);
    }

    [Fact]
    public void NonBroadcast_Rows_Are_Untouched_For_NonJeeber()
    {
        var rows = new List<UpstreamNotificationRow>
        {
            Row("1", "offer_received"),
            Row("2", "delivery_completed"),
            Row("3", null),
        };

        var (visible, dropped) =
            JeebNotificationsInboxController.FilterJeeberBroadcasts(rows, callerIsJeeber: false);

        dropped.Should().Be(0);
        visible.Should().HaveCount(3);
    }

    [Fact]
    public void Null_Or_Empty_Input_Yields_Empty_No_Throw()
    {
        var (v1, d1) = JeebNotificationsInboxController.FilterJeeberBroadcasts(null, callerIsJeeber: false);
        v1.Should().BeEmpty();
        d1.Should().Be(0);

        var (v2, d2) = JeebNotificationsInboxController.FilterJeeberBroadcasts(
            new List<UpstreamNotificationRow>(), callerIsJeeber: false);
        v2.Should().BeEmpty();
        d2.Should().Be(0);
    }
}
