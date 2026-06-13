using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-024 (JEEB-42) integration tests for POST /deliveries/{id}/cancel
/// and the /admin/cancellations queue.
///
/// Coverage maps to the ACs:
/// <list type="bullet">
///   <item>Client cancels before pickup → free + immediate.</item>
///   <item>Client cancels after pickup → admin queue.</item>
///   <item>Jeeber cancels → mandatory reason; 3+/7d triggers 24hr restriction.</item>
///   <item>Cancellation rate tracked per Jeeber.</item>
///   <item>Admin approve / reject works and is audited.</item>
/// </list>
///
/// Each test spins up a fresh WebApplicationFactory with a controllable
/// FakeClock so the 7-day rolling window can be exercised deterministically.
/// </summary>
public class CancellationEndpointTests
{
    // -------- Client pre-pickup: free + immediate ----------------------

    [Theory]
    [InlineData(RequestStatus.Pending)]
    [InlineData(RequestStatus.Matched)]
    [InlineData(RequestStatus.Accepted)]
    public async Task Client_Cancel_Before_Pickup_Is_Free_And_Immediate(string fromStatus)
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, fromStatus, bindJeeber: fromStatus != RequestStatus.Pending);

        var resp = await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CancelDeliveryResponse>();
        dto!.Status.Should().Be(RequestStatus.Cancelled);
        dto.PreviousStatus.Should().Be(fromStatus);
        dto.PendingApproval.Should().BeFalse();
        dto.JeeberRestricted.Should().BeFalse();

        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.Status.Should().Be(RequestStatus.Cancelled);
        row.CancelledBy.Should().Be("client");
    }

    // -------- Client post-pickup: admin approval required --------------

    [Theory]
    [InlineData(RequestStatus.PickedUp)]
    [InlineData(RequestStatus.HeadingOff)]
    public async Task Client_Cancel_After_Pickup_Goes_To_Admin_Queue(string fromStatus)
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, fromStatus);

        var resp = await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { reason = "changed my mind" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CancelDeliveryResponse>();
        dto!.Status.Should().Be(RequestStatus.CancellationRequested);
        dto.PendingApproval.Should().BeTrue();

        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.Status.Should().Be(RequestStatus.CancellationRequested);
        row.CancellationPreviousStatus.Should().Be(fromStatus);
    }

    [Fact]
    public async Task Pending_Cancellation_Is_Listed_On_Admin_Queue()
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, RequestStatus.PickedUp);

        (await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { reason = "queued" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await AdminClient(factory).GetFromJsonAsync<AdminCancellationsResponse>("/admin/cancellations");
        list!.Items.Should().Contain(i => i.DeliveryId == seed.Id && i.Reason == "queued");
        list.Total.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task Admin_Approve_Lands_Row_On_Cancelled()
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, RequestStatus.HeadingOff);
        await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { reason = "package wet" });

        var resp = await AdminClient(factory)
            .PatchAsync($"/admin/cancellations/{seed.Id}",
                JsonContent.Create(new { action = "approve" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.Status.Should().Be(RequestStatus.Cancelled);
        row.CancellationApprovedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Admin_Reject_Reverts_Row_To_Previous_Status()
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, RequestStatus.HeadingOff);
        await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { reason = "package wet" });

        var resp = await AdminClient(factory)
            .PatchAsync($"/admin/cancellations/{seed.Id}",
                JsonContent.Create(new { action = "reject", note = "no proof" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.Status.Should().Be(RequestStatus.HeadingOff);
        row.CancellationRejectedAt.Should().NotBeNull();
        row.CancelledBy.Should().BeNull("reject clears the cancellation audit fields");
    }

    [Fact]
    public async Task Admin_Decision_On_Non_Pending_Returns_409()
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, RequestStatus.Pending);

        // Cancel directly first (pre-pickup → immediate terminal).
        await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { });

        var resp = await AdminClient(factory)
            .PatchAsync($"/admin/cancellations/{seed.Id}",
                JsonContent.Create(new { action = "approve" }));
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // -------- Jeeber cancellation: reason mandatory + 3+/7d block ------

    [Fact]
    public async Task Jeeber_Cancel_Without_Reason_Returns_400()
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, RequestStatus.Accepted);

        var resp = await JeeberClient(factory, seed.JeeberId!)
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/cancellation-reason-required");
    }

    [Fact]
    public async Task Jeeber_Cancel_With_Reason_Succeeds()
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, RequestStatus.Accepted);

        var resp = await JeeberClient(factory, seed.JeeberId!)
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { reason = "bike broke down" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<CancelDeliveryResponse>();
        dto!.Status.Should().Be(RequestStatus.Cancelled);
        dto.JeeberCancellationsLast7Days.Should().Be(1);
        dto.JeeberRestricted.Should().BeFalse();
    }

    [Fact]
    public async Task Third_Jeeber_Cancel_In_Seven_Days_Triggers_24hr_Restriction()
    {
        using var factory = NewFactory(out var clock);
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        // Three independent deliveries spread across the 7-day window.
        for (var i = 0; i < 3; i++)
        {
            var seed = await SeedRow(factory, RequestStatus.Accepted, jeeberId);
            var resp = await JeeberClient(factory, jeeberId)
                .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { reason = $"cancel-{i}" });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var dto = await resp.Content.ReadFromJsonAsync<CancelDeliveryResponse>();
            dto!.JeeberCancellationsLast7Days.Should().Be(i + 1);

            // The threshold is "3+", so the 3rd cancel itself trips it.
            if (i < 2)
            {
                dto.JeeberRestricted.Should().BeFalse();
            }
            else
            {
                dto.JeeberRestricted.Should().BeTrue();
                dto.RestrictionExpiresAt.Should()
                    .BeCloseTo(clock.GetUtcNow() + TimeSpan.FromHours(24), TimeSpan.FromMinutes(1));
            }

            clock.Advance(TimeSpan.FromMinutes(5));
        }

        var restrictions = factory.Services.GetRequiredService<IJeeberRestrictionStore>();
        (await restrictions.IsRestrictedAsync(jeeberId, clock.GetUtcNow(), default)).Should().BeTrue();
    }

    [Fact]
    public async Task Cancellations_Older_Than_Seven_Days_Do_Not_Trip_The_Restriction()
    {
        using var factory = NewFactory(out var clock);
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        // Two cancellations more than 7 days ago.
        for (var i = 0; i < 2; i++)
        {
            var seed = await SeedRow(factory, RequestStatus.Accepted, jeeberId);
            await JeeberClient(factory, jeeberId)
                .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { reason = "old" });
        }
        clock.Advance(TimeSpan.FromDays(8));

        // Today's cancel — should be the ONLY one inside the rolling window.
        var fresh = await SeedRow(factory, RequestStatus.Accepted, jeeberId);
        var resp = await JeeberClient(factory, jeeberId)
            .PostAsJsonAsync($"/deliveries/{fresh.Id}/cancel", new { reason = "fresh" });

        var dto = await resp.Content.ReadFromJsonAsync<CancelDeliveryResponse>();
        dto!.JeeberCancellationsLast7Days.Should().Be(1);
        dto.JeeberRestricted.Should().BeFalse();
    }

    [Fact]
    public async Task Lifetime_Cancellation_Rate_Is_Tracked_Per_Jeeber()
    {
        using var factory = NewFactory(out var clock);
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        for (var i = 0; i < 4; i++)
        {
            var seed = await SeedRow(factory, RequestStatus.Accepted, jeeberId);
            await JeeberClient(factory, jeeberId)
                .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { reason = "x" });
            clock.Advance(TimeSpan.FromDays(3));
        }

        var cancellations = factory.Services.GetRequiredService<ICancellationService>();
        var lifetime = await cancellations.GetJeeberCancellationCountAsync(jeeberId, default);
        lifetime.Should().Be(4, "every Jeeber cancellation counts toward the lifetime rate");
    }

    // -------- Auth + edge cases ----------------------------------------

    [Fact]
    public async Task Cancel_Without_Identity_Returns_401()
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, RequestStatus.Pending);

        var resp = await factory.CreateClient()
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cancel_By_Stranger_Returns_403()
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, RequestStatus.Accepted);

        var stranger = ClientFor(factory, $"intruder-{Guid.NewGuid()}");
        var resp = await stranger.PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cancel_Of_Unknown_Delivery_Returns_404()
    {
        using var factory = NewFactory(out _);
        var resp = await ClientFor(factory, "anyone")
            .PostAsJsonAsync($"/deliveries/{Guid.NewGuid()}/cancel", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_Of_Already_Cancelled_Delivery_Returns_409()
    {
        using var factory = NewFactory(out _);
        var seed = await SeedRow(factory, RequestStatus.Pending);

        (await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/deliveries/{seed.Id}/cancel", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ----------------------- helpers -----------------------------------

    private static WebApplicationFactory<Program> NewFactory(out FakeClock clock)
    {
        var theClock = new FakeClock(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
        clock = theClock;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(theClock);
            });
        });
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", Roles.Client);
        return c;
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", Roles.Jeeber);
        return c;
    }

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", $"admin-{Guid.NewGuid()}");
        c.DefaultRequestHeaders.Add("X-User-Roles", Roles.Admin);
        return c;
    }

    private static async Task<Seed> SeedRow(
        WebApplicationFactory<Program> factory,
        string targetStatus,
        bool bindJeeber = true)
    {
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        return await SeedCore(factory, targetStatus, jeeberId, bindJeeber);
    }

    private static Task<Seed> SeedRow(
        WebApplicationFactory<Program> factory,
        string targetStatus,
        string jeeberId,
        bool bindJeeber = true)
    {
        return SeedCore(factory, targetStatus, jeeberId, bindJeeber);
    }

    private static async Task<Seed> SeedCore(
        WebApplicationFactory<Program> factory,
        string targetStatus,
        string jeeberId,
        bool bindJeeber)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"client-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "test parcel"
        }, default);

        if (bindJeeber)
        {
            var accepted = await store.TryAcceptByJeeberAsync(
                created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
            accepted.Should().NotBeNull();
        }

        if (created.Status != targetStatus)
        {
            (await store.SetStatusAsync(created.Id, targetStatus, default)).Should().BeTrue();
        }

        return new Seed(created.Id, clientId, bindJeeber ? jeeberId : null);
    }

    private sealed record Seed(string Id, string ClientId, string? JeeberId);

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
