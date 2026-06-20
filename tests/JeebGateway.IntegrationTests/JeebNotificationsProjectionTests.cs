using System.Collections.Generic;
using FluentAssertions;
using JeebGateway.JeebNotifications;
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
}
