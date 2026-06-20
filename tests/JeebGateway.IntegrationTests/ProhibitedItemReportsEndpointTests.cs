using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class ProhibitedItemReportsEndpointTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ProhibitedItemReportsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Report_Creates_Pending_Flagged_Request_For_Admin_Review()
    {
        var jeeber = ParticipantClient("jeeber-report-1");

        var resp = await jeeber.PostAsJsonAsync("/prohibited-items/reports", new
        {
            requestId = "req-prohibited-report-1",
            reason = "Client asked me to deliver a prohibited item."
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<ReportResponseDto>();
        body!.ReportId.Should().NotBeNullOrWhiteSpace();
        body.RequestId.Should().Be("req-prohibited-report-1");
        body.Status.Should().Be("pending");

        var admin = AdminClient("admin-report-reader");
        var list = await admin.GetFromJsonAsync<FlaggedListDto>(
            "/admin/prohibited-items/flagged?status=pending&page=1&pageSize=50");

        list!.Items.Should().Contain(i =>
            i.Id == body.ReportId
            && i.RequestId == "req-prohibited-report-1"
            && i.UserId == "jeeber-report-1"
            && i.Description == "Client asked me to deliver a prohibited item."
            && i.Matches.Length == 0);
    }

    [Fact]
    public async Task Report_Requires_Identity()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/prohibited-items/reports", new
        {
            requestId = "req-report-unauth",
            reason = "prohibited item"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("", "reason")]
    [InlineData("request-id", "")]
    public async Task Report_Validates_Required_Fields(string requestId, string reason)
    {
        var client = ParticipantClient("jeeber-report-validation");

        var resp = await client.PostAsJsonAsync("/prohibited-items/reports", new
        {
            requestId,
            reason
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
        string ReportId,
        string RequestId,
        string Status,
        DateTimeOffset CreatedAt);

    private sealed record FlaggedListDto(
        FlaggedDto[] Items,
        int Page,
        int PageSize,
        int Total);

    private sealed record FlaggedDto(
        string Id,
        string? RequestId,
        string UserId,
        string Description,
        MatchDto[] Matches,
        string Status);

    private sealed record MatchDto(
        string ItemId,
        string ItemName,
        string Category,
        string MatchedTerm,
        string Evidence,
        string MatchType,
        double Confidence);
}
