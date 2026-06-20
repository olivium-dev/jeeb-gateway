using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using JeebGateway.JeebSupport;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Unit coverage of the generic→Jeeb SUPPORT projection that lives in the gateway
/// (ADR-0001/0005 thin map): the mobile <c>DioSupportRepository</c> DTO-drift
/// reconciliation (category enum + <c>orderRef</c>→<c>orderId</c>), the canonical ticket
/// shape, the cold-start empty page, and the static categories catalog. These bypass
/// HTTP/DI — mirroring <see cref="JeebNotificationsProjectionTests"/>.
/// </summary>
public class JeebSupportProjectionTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-06-20T10:00:00Z").ToUniversalTime();

    private static CreateSupportTicketRequest Req(
        string? category = "delivery",
        string? body = "My order never arrived",
        string? orderRef = "ord-42",
        string? orderId = null,
        IReadOnlyList<string>? attachments = null)
        => new()
        {
            Category = category,
            Body = body,
            OrderRef = orderRef,
            OrderId = orderId,
            Attachments = attachments,
        };

    // ── category reconciliation (mobile↔canonical drift) ──────────────────────────────

    [Theory]
    [InlineData("delivery", "order")]   // mobile enum-name drift
    [InlineData("kycAppeal", "kyc")]    // mobile enum-name drift
    [InlineData("kycappeal", "kyc")]    // case-insensitive
    [InlineData("order", "order")]      // canonical id passes through
    [InlineData("payment", "payment")]
    [InlineData("account", "account")]
    [InlineData("kyc", "kyc")]
    [InlineData("dispute", "dispute")]
    [InlineData("other", "other")]
    [InlineData("  Other  ", "other")]  // trim + case
    public void ReconcileCategory_Maps_Mobile_And_Canonical_Names(string raw, string expected)
    {
        JeebSupportProjection.ReconcileCategory(raw).Should().Be(expected);
        JeebSupportProjection.IsKnownCategory(raw).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    public void ReconcileCategory_Unknown_Or_Blank_Is_Null(string? raw)
    {
        JeebSupportProjection.ReconcileCategory(raw).Should().BeNull();
        JeebSupportProjection.IsKnownCategory(raw).Should().BeFalse();
    }

    // ── BuildRow (draft → stored row) ─────────────────────────────────────────────────

    [Fact]
    public void BuildRow_Reconciles_Category_And_OrderRef_To_OrderId()
    {
        var row = JeebSupportProjection.BuildRow("t-1", "user-9", Req(), Now);

        row.Id.Should().Be("t-1");
        row.UserId.Should().Be("user-9");
        // mobile "delivery" → canonical "order"
        row.Category.Should().Be("order");
        // mobile "orderRef" → canonical stored "orderId"
        row.OrderId.Should().Be("ord-42");
        row.Body.Should().Be("My order never arrived");
        row.Status.Should().Be("open");
        row.TicketNumber.Should().StartWith("SUP-");
        row.CreatedAt.Should().Be(row.UpdatedAt);
    }

    [Fact]
    public void BuildRow_Prefers_OrderId_When_Both_Set()
    {
        var row = JeebSupportProjection.BuildRow(
            "t-1", "u", Req(orderRef: "ref-A", orderId: "id-B"), Now);

        row.OrderId.Should().Be("id-B");
    }

    [Fact]
    public void BuildRow_Falls_Back_To_OrderRef_When_OrderId_Blank()
    {
        var row = JeebSupportProjection.BuildRow(
            "t-1", "u", Req(orderRef: "ref-A", orderId: "   "), Now);

        row.OrderId.Should().Be("ref-A");
    }

    [Fact]
    public void BuildRow_Trims_Body_And_Drops_Blank_Attachments()
    {
        var row = JeebSupportProjection.BuildRow(
            "t-1", "u",
            Req(body: "  hello  ", attachments: new[] { "a.png", " ", "", "b.png" }),
            Now);

        row.Body.Should().Be("hello");
        row.Attachments.Should().BeEquivalentTo(new[] { "a.png", "b.png" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void BuildRow_Unknown_Category_Throws()
    {
        var act = () => JeebSupportProjection.BuildRow("t-1", "u", Req(category: "nope"), Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MintTicketNumber_Is_SUP_Last6_Of_Epoch_Ms()
    {
        var num = JeebSupportProjection.MintTicketNumber(Now);
        num.Should().MatchRegex("^SUP-[0-9]{6}$");
    }

    // ── ProjectTicket (row → mobile DTO) ──────────────────────────────────────────────

    [Fact]
    public void ProjectTicket_Maps_All_Fields_To_Canonical_Shape()
    {
        var row = JeebSupportProjection.BuildRow("t-1", "user-9", Req(category: "kycAppeal"), Now);
        var dto = JeebSupportProjection.ProjectTicket(row);

        dto.Id.Should().Be("t-1");
        dto.UserId.Should().Be("user-9");
        dto.Category.Should().Be("kyc");
        dto.OrderId.Should().Be("ord-42");
        dto.Status.Should().Be("open");
        dto.TicketNumber.Should().StartWith("SUP-");
        // mobile reads id + status; both populated.
        dto.Id.Should().NotBeEmpty();
        dto.Status.Should().NotBeEmpty();
    }

    [Fact]
    public void ProjectTicket_Blank_Status_Defaults_To_Open()
    {
        var row = new SupportTicketRow { Id = "t-1", UserId = "u", Category = "other", Status = "  " };
        JeebSupportProjection.ProjectTicket(row).Status.Should().Be("open");
    }

    // ── ProjectPage (rows → page) ─────────────────────────────────────────────────────

    [Fact]
    public void ProjectPage_Null_Rows_Is_ColdStart_Empty_Page()
    {
        var page = JeebSupportProjection.ProjectPage(null, page: 1, pageSize: 20);

        page.Items.Should().BeEmpty();
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
        page.TotalCount.Should().Be(0);
        page.TotalPages.Should().Be(1);
        page.Cursor.Should().BeNull();
    }

    [Fact]
    public void ProjectPage_Orders_NewestFirst_And_Pages()
    {
        var rows = new List<SupportTicketRow>
        {
            new() { Id = "old", UserId = "u", Category = "other", CreatedAt = "2026-06-20T09:00:00Z" },
            new() { Id = "new", UserId = "u", Category = "other", CreatedAt = "2026-06-20T11:00:00Z" },
            new() { Id = "mid", UserId = "u", Category = "other", CreatedAt = "2026-06-20T10:00:00Z" },
        };

        var page = JeebSupportProjection.ProjectPage(rows, page: 1, pageSize: 2);

        page.TotalCount.Should().Be(3);
        page.TotalPages.Should().Be(2); // ceil(3/2)
        page.Items.Should().HaveCount(2);
        page.Items[0].Id.Should().Be("new");
        page.Items[1].Id.Should().Be("mid");

        var p2 = JeebSupportProjection.ProjectPage(rows, page: 2, pageSize: 2);
        p2.Items.Should().ContainSingle().Which.Id.Should().Be("old");
    }

    [Fact]
    public void ProjectPage_Clamps_Paging_To_Safe_Bounds()
    {
        var page = JeebSupportProjection.ProjectPage(new List<SupportTicketRow>(), page: 0, pageSize: 0);
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);

        var big = JeebSupportProjection.ProjectPage(new List<SupportTicketRow>(), page: 1, pageSize: 9999);
        big.PageSize.Should().Be(100);
    }

    // ── categories catalog (static, gateway-owned) ────────────────────────────────────

    [Fact]
    public void ProjectCategories_Returns_The_Canonical_Catalog()
    {
        var cats = JeebSupportProjection.ProjectCategories();

        cats.Items.Select(c => c.Id).Should().BeEquivalentTo(
            new[] { "order", "payment", "account", "kyc", "dispute", "other" });
        cats.Items.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Label));
    }
}
