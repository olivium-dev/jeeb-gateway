using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.service.ServiceNotification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// NOT-02 (Domain 12) — unread bell-badge surface. The gateway exposes
/// <c>GET /api/notification/messages/unread-count</c>, deriving the count from the
/// notification-service unread-filtered receiver query. These tests substitute the
/// NSwag <see cref="ServiceNotificationClient"/> with a controllable stub so the badge
/// math (cap + capped flag) and the edge-identity path are exercised without a live upstream.
/// </summary>
public class NotificationUnreadCountEndpointTests
{
    private const string Route = "/api/notification/messages/unread-count";

    [Fact]
    public async Task UnreadCount_Returns_Exact_Total_From_Upstream()
    {
        using var factory = NewFactory(total: 7, itemCount: 7);
        var client = ClientFor(factory, "user-badge-1");

        var resp = await client.GetAsync(Route);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UnreadCountResponse>();
        body.Should().NotBeNull();
        body!.UnreadCount.Should().Be(7);
        body.Capped.Should().BeFalse();
    }

    [Fact]
    public async Task UnreadCount_Caps_At_Ceiling_And_Flags_Capped()
    {
        // Upstream reports 250 unread; the badge must cap at 99 and report capped=true.
        using var factory = NewFactory(total: 250, itemCount: 100);
        var client = ClientFor(factory, "user-badge-2");

        var resp = await client.GetAsync(Route);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UnreadCountResponse>();
        body!.UnreadCount.Should().Be(99);
        body.Capped.Should().BeTrue();
    }

    [Fact]
    public async Task UnreadCount_Falls_Back_To_Item_Count_When_No_Total()
    {
        // No `total` field present -> count returned items instead of throwing.
        using var factory = NewFactory(total: null, itemCount: 3);
        var client = ClientFor(factory, "user-badge-3");

        var resp = await client.GetAsync(Route);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UnreadCountResponse>();
        body!.UnreadCount.Should().Be(3);
        body.Capped.Should().BeFalse();
    }

    [Fact]
    public async Task UnreadCount_Without_Identity_Returns_401()
    {
        // Negative path: no bearer claim and no X-User-Id edge header -> 401.
        using var factory = NewFactory(total: 5, itemCount: 5);
        var client = factory.CreateClient();

        var resp = await client.GetAsync(Route);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnreadCount_Is_Reachable_On_Edge_X_User_Id_Path()
    {
        // NOT-02 regression guard: the inbox/badge must be reachable on the trusted edge
        // header path (no bearer), which the pre-fix claim-only lookup rejected with 401.
        using var factory = NewFactory(total: 2, itemCount: 2);
        var client = ClientFor(factory, "user-edge-badge");

        var resp = await client.GetAsync(Route);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UnreadCountResponse>();
        body!.UnreadCount.Should().Be(2);
    }

    // ---- harness ----

    private static WebApplicationFactory<Program> NewFactory(int? total, int itemCount) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceNotificationClient>();
                services.AddScoped<ServiceNotificationClient>(
                    _ => new StubNotificationClient(total, itemCount));
            }));

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");
        return client;
    }

    private sealed record UnreadCountResponse(int UnreadCount, bool Capped);

    /// <summary>
    /// Hand-rolled test double (no Moq/NSubstitute in this project). Overrides only the
    /// receiver-messages call used by the unread-count endpoint and returns a shaped payload
    /// matching the dynamic projection the controller performs.
    /// </summary>
    private sealed class StubNotificationClient : ServiceNotificationClient
    {
        private readonly int? _total;
        private readonly int _itemCount;

        public StubNotificationClient(int? total, int itemCount)
            : base("http://stub-notification.test", new HttpClient())
        {
            _total = total;
            _itemCount = itemCount;
        }

        public override Task<object> Get_messages_by_receiver_messages_receiver__receiver_id__getAsync(
            string receiver_id,
            int? page,
            int? page_size,
            string read_status,
            Notification_type2 notification_type,
            Anonymous sender,
            Created_after2 created_after,
            Created_before2 created_before,
            CancellationToken cancellationToken)
        {
            var items = new List<object>();
            for (var i = 0; i < _itemCount; i++)
            {
                items.Add(new { notification_id = $"n{i}", status = "unread" });
            }

            object payload = _total.HasValue
                ? new { total = _total.Value, items }
                : new { items };

            return Task.FromResult(payload);
        }
    }
}
