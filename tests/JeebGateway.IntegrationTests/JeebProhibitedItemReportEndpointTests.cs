using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// End-to-end tests for the user-facing prohibited-item report route
/// (<c>POST /v1/jeeb/prohibited-items/report</c>) and its reuse of the existing
/// admin moderation queue (a filed report shows up in
/// <c>GET /admin/prohibited-items/flagged</c> and is decidable).
/// </summary>
public class JeebProhibitedItemReportEndpointTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public JeebProhibitedItemReportEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Report_Without_Identity_Returns_401()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/jeeb/prohibited-items/report",
            new { requestId = "req-1", reason = "Contains a knife." });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Report_With_Blank_Reason_Returns_400()
    {
        var client = ParticipantClient("user-report-blank");

        var resp = await client.PostAsJsonAsync("/v1/jeeb/prohibited-items/report",
            new { requestId = "req-1", reason = "   " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Report_Without_Anchor_Returns_400()
    {
        var client = ParticipantClient("user-report-noanchor");

        var resp = await client.PostAsJsonAsync("/v1/jeeb/prohibited-items/report",
            new { reason = "There is a prohibited item." });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Report_Creates_Pending_Flag_For_RequestId()
    {
        var client = ParticipantClient("user-report-ok");

        var resp = await client.PostAsJsonAsync("/v1/jeeb/prohibited-items/report",
            new { requestId = "req-report-ok", reason = "Box clearly contains fireworks." });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<ReportResponseDto>();
        body!.Id.Should().NotBeNullOrWhiteSpace();
        body.RequestId.Should().Be("req-report-ok");
        body.ReporterUserId.Should().Be("user-report-ok");
        body.Reason.Should().Be("Box clearly contains fireworks.");
        body.Status.Should().Be("pending");
    }

    [Fact]
    public async Task Report_Accepts_DeliveryId_As_Anchor()
    {
        var client = ParticipantClient("user-report-delivery");

        var resp = await client.PostAsJsonAsync("/v1/jeeb/prohibited-items/report",
            new { deliveryId = "del-report-1", reason = "Driver is carrying a weapon." });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<ReportResponseDto>();
        body!.RequestId.Should().Be("del-report-1");
        body.Status.Should().Be("pending");
    }

    [Fact]
    public async Task Reported_Item_Lands_In_Admin_Queue_And_Is_Decidable()
    {
        var user = ParticipantClient("user-report-flow");
        var reportResp = await user.PostAsJsonAsync("/v1/jeeb/prohibited-items/report",
            new { requestId = "req-report-flow", reason = "Package smells of solvent / flammable." });
        var report = await reportResp.Content.ReadFromJsonAsync<ReportResponseDto>();
        report!.Id.Should().NotBeNullOrWhiteSpace();

        var admin = AdminClient("admin-report-flow");

        var listResp = await admin.GetAsync("/admin/prohibited-items/flagged?status=pending&page=1&pageSize=100");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listResp.Content.ReadFromJsonAsync<FlaggedListDto>();
        list!.Items.Should().Contain(f =>
            f.Id == report.Id
            && f.RequestId == "req-report-flow"
            && f.UserId == "user-report-flow"
            && f.Matches.Length == 0);

        var decisionResp = await admin.PostAsJsonAsync(
            $"/admin/prohibited-items/flagged/{report.Id}/decision",
            new { decision = "upheld", note = "Confirmed flammable goods." });
        decisionResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var decided = await decisionResp.Content.ReadFromJsonAsync<FlaggedDto>();
        decided!.Status.Should().Be("upheld");
    }

    private HttpClient ParticipantClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");
        return client;
    }

    private HttpClient AdminClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private sealed record ReportResponseDto(
        string Id,
        string? RequestId,
        string ReporterUserId,
        string Reason,
        string Status,
        DateTimeOffset CreatedAt);

    private sealed record FlaggedDto(
        string Id,
        string? RequestId,
        string UserId,
        string Description,
        object[] Matches,
        string Status,
        DateTimeOffset CreatedAt,
        string? DecidedBy,
        DateTimeOffset? DecidedAt,
        string? DecisionNote);

    private sealed record FlaggedListDto(
        FlaggedDto[] Items,
        int Page,
        int PageSize,
        int Total);
}
