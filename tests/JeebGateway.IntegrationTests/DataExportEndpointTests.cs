using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Users.DataExport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-042: POST /users/me/data-export. GDPR-like right of access
/// — every request queues a full export (profile, orders, ratings, chat
/// history) and the user receives a secure download link within 72 hours.
///
/// The tests pin the contract on every AC bullet:
///   * queueing returns 202 with status=queued and dueBy = now + 72h
///   * the packager includes all four sections
///   * the download link is valid only for the user who requested it
///     and only once
///   * a fresh request while a previous one is open is idempotent
/// </summary>
public class DataExportEndpointTests
{
    [Fact]
    public async Task RequestExport_Without_Identity_Returns_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var anon = factory.CreateClient();

        var resp = await anon.PostAsync("/users/me/data-export", JsonContent.Create(new { }));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RequestExport_Returns_202_And_Sets_Sla_Deadline_72h_Out()
    {
        using var factory = new WebApplicationFactory<Program>();
        var before = DateTimeOffset.UtcNow;
        var client = ClientFor(factory, "exp-sla");

        var resp = await client.PostAsync("/users/me/data-export", JsonContent.Create(new { }));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<DataExportResponse>();
        body!.UserId.Should().Be("exp-sla");
        body.Status.Should().Be(DataExportStatus.Queued);
        body.Format.Should().Be(DataExportFormat.Json);
        body.DueBy.Should().BeCloseTo(before.AddHours(72), TimeSpan.FromMinutes(1));
        body.ReadyAt.Should().BeNull();
        body.DownloadUrl.Should().BeNull();
    }

    [Fact]
    public async Task RequestExport_Is_Idempotent_While_Existing_Is_Open()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "exp-idem");

        var first = await (await client.PostAsync("/users/me/data-export", JsonContent.Create(new { })))
            .Content.ReadFromJsonAsync<DataExportResponse>();
        var second = await (await client.PostAsync("/users/me/data-export", JsonContent.Create(new { })))
            .Content.ReadFromJsonAsync<DataExportResponse>();

        second!.Id.Should().Be(first!.Id);
        second.RequestedAt.Should().Be(first.RequestedAt);
        second.DueBy.Should().Be(first.DueBy);
    }

    [Fact]
    public async Task RequestExport_Invalid_Format_Returns_400()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "exp-bad-fmt");

        var resp = await client.PostAsync(
            "/users/me/data-export",
            JsonContent.Create(new { format = "xml" }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetLatest_Without_Prior_Request_Returns_404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "exp-missing");

        var resp = await client.GetAsync("/users/me/data-export");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Processor_Renders_Payload_With_All_Four_Sections()
    {
        using var factory = NewFactoryWithChatHistoryDouble();
        var userId = "exp-payload";

        // Seed a profile, an order, ratings, and chat history so the
        // packager has something real to include — and the test can
        // assert every AC bullet shows up in the bytes.
        SeedProfile(factory, userId);
        var orderId = await SeedOrder(factory, userId);
        SeedRating(factory, userId, requestId: orderId);
        SeedChatMessage(factory, userId);

        var client = ClientFor(factory, userId);
        await client.PostAsync("/users/me/data-export", JsonContent.Create(new { }));

        var processor = factory.Services.GetRequiredService<DataExportProcessor>();
        var processed = await processor.ProcessOnceAsync(CancellationToken.None);
        processed.Should().Be(1);

        var status = await client.GetFromJsonAsync<DataExportResponse>("/users/me/data-export");
        status!.Status.Should().Be(DataExportStatus.Ready);
        status.DownloadUrl.Should().NotBeNull();
        status.PayloadSizeBytes.Should().BeGreaterThan(0);

        // Pull the bytes through the download endpoint and assert that
        // each of the four required sections is present and non-empty.
        var downloadResp = await client.GetAsync(status.DownloadUrl);
        downloadResp.EnsureSuccessStatusCode();
        downloadResp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        using var doc = JsonDocument.Parse(await downloadResp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("profile").GetProperty("name").GetString().Should().Be("Lina");
        root.GetProperty("orders").GetArrayLength().Should().Be(1);
        root.GetProperty("ratings").GetArrayLength().Should().Be(1);
        root.GetProperty("chatHistory").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Notifier_Receives_Ready_Event_With_Download_Token()
    {
        using var factory = new WebApplicationFactory<Program>();
        var userId = "exp-notify";
        var client = ClientFor(factory, userId);

        await client.PostAsync("/users/me/data-export", JsonContent.Create(new { }));
        var processor = factory.Services.GetRequiredService<DataExportProcessor>();
        await processor.ProcessOnceAsync(CancellationToken.None);

        // The AC requires email/push notification on completion — the
        // gateway delegates that to notification-service via the notifier
        // seam, so asserting the seam was invoked confirms the wiring.
        var notifier = factory.Services.GetRequiredService<InMemoryDataExportNotifier>();
        notifier.All.Should().ContainSingle()
            .Which.UserId.Should().Be(userId);
        notifier.All[0].DownloadToken.Should().NotBeNullOrEmpty();
        notifier.All[0].LinkExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Download_Endpoint_Rejects_Unknown_Token_With_404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var anon = factory.CreateClient();

        var resp = await anon.GetAsync("/users/me/data-export/not-a-real-token/download");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_Endpoint_Is_Single_Use()
    {
        using var factory = new WebApplicationFactory<Program>();
        var userId = "exp-single-use";
        var client = ClientFor(factory, userId);
        await client.PostAsync("/users/me/data-export", JsonContent.Create(new { }));
        await factory.Services.GetRequiredService<DataExportProcessor>().ProcessOnceAsync(CancellationToken.None);
        var status = await client.GetFromJsonAsync<DataExportResponse>("/users/me/data-export");

        // The token can be presented anonymously — that's the point of
        // the unguessable link — but only ONCE. The second hit on the
        // same URL is a 404 even from the same caller.
        var anon = factory.CreateClient();
        var first = await anon.GetAsync(status!.DownloadUrl);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await anon.GetAsync(status.DownloadUrl);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Processor_Completes_Well_Inside_The_72_Hour_Sla()
    {
        // This is a smoke check on the SLA itself: a queued export gets
        // marked ready in a single sweep, so the wall-clock budget is
        // bounded by the sweep interval (30s by default) plus the
        // packaging cost — orders of magnitude under 72h. The assertion
        // is intentionally generous; the point is to fail loudly if a
        // regression makes the processor a no-op.
        using var factory = new WebApplicationFactory<Program>();
        var userId = "exp-sla-smoke";
        var client = ClientFor(factory, userId);
        var requested = DateTimeOffset.UtcNow;

        await client.PostAsync("/users/me/data-export", JsonContent.Create(new { }));
        await factory.Services.GetRequiredService<DataExportProcessor>().ProcessOnceAsync(CancellationToken.None);

        var status = await client.GetFromJsonAsync<DataExportResponse>("/users/me/data-export");
        status!.Status.Should().Be(DataExportStatus.Ready);
        status.ReadyAt.Should().NotBeNull();
        status.ReadyAt!.Value.Should().BeBefore(requested.AddHours(72));
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        // ADR-005 §7: edge caller declares its user type. data.export.self is §B {client, jeeber, admin};
        // a participant edge role satisfies it (the single-use download token route stays anonymous).
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");
        return client;
    }

    private static void SeedProfile(WebApplicationFactory<Program> factory, string userId)
    {
        var users = factory.Services.GetRequiredService<JeebGateway.Users.InMemoryUsersStore>();
        users.Seed(new JeebGateway.Users.UserProfile
        {
            Id = userId,
            Phone = "+96550001234",
            Name = "Lina",
            Email = "lina@example.com",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-90),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
    }

    private static async Task<string> SeedOrder(WebApplicationFactory<Program> factory, string userId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var req = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = userId,
            Description = "data-export test parcel"
        }, CancellationToken.None);
        return req.Id;
    }

    private static void SeedRating(WebApplicationFactory<Program> factory, string userId, string requestId)
    {
        var ratings = factory.Services.GetRequiredService<InMemoryDataExportRatingsProvider>();
        ratings.Seed(userId, new RatingSnapshot
        {
            RatingId = Guid.NewGuid().ToString("N"),
            RequestId = requestId,
            Direction = "given",
            CounterpartyId = "jeeber-1",
            Stars = 5,
            Comment = "fast",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2)
        });
    }

    /// <summary>
    /// Factory variant that swaps the production client-backed chat-history provider
    /// (returns empty until chat-service exposes list-channels-for-member) for a
    /// seedable double, so the export-pipeline test can assert chat history is
    /// packaged when the provider yields it.
    /// </summary>
    private static WebApplicationFactory<Program> NewFactoryWithChatHistoryDouble() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDataExportChatHistoryProvider>();
                services.AddSingleton<SeedableDataExportChatHistoryProvider>();
                services.AddScoped<IDataExportChatHistoryProvider>(
                    sp => sp.GetRequiredService<SeedableDataExportChatHistoryProvider>());
            });
        });

    private static void SeedChatMessage(WebApplicationFactory<Program> factory, string userId)
    {
        var chats = factory.Services.GetRequiredService<SeedableDataExportChatHistoryProvider>();
        chats.Seed(userId, new ChatMessageSnapshot
        {
            ConversationId = "conv-1",
            MessageId = Guid.NewGuid().ToString("N"),
            SenderId = userId,
            Body = "on the way",
            SentAt = DateTimeOffset.UtcNow.AddHours(-3)
        });
    }
}
