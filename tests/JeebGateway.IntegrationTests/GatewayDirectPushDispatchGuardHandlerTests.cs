using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class GatewayDirectPushDispatchGuardHandlerTests
{
    [Theory]
    [InlineData("api/v1/sent-payload/device/device-1")]
    [InlineData("api/v1/sent-payload/user/user-1")]
    [InlineData("api/v1/sent-payload/broadcast")]
    [InlineData("api/v1/sent-payload/topic/jeeb_jeebers")]
    public async Task Disabled_BlocksDirectDispatchWithoutCallingPushService(string path)
    {
        var downstream = new RecordingHandler();
        using var client = Client(enabled: false, downstream);

        using var response = await client.PostAsJsonAsync(path, new { payload = new { title = "test" } });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        downstream.RequestCount.Should().Be(0);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(GatewayDirectPushDispatchGuardHandler.DisabledProblemCode);
    }

    [Theory]
    [InlineData("PUT", "api/v1/register")]
    [InlineData("DELETE", "api/v1/register/by-user")]
    [InlineData("GET", "health")]
    [InlineData("GET", "api/v1/sent-payload/idempotency/stale")]
    [InlineData("POST", "api/v1/sent-payload/idempotency/key/resolve")]
    public async Task Disabled_AllowsNonDispatchPushOperations(string method, string path)
    {
        var downstream = new RecordingHandler();
        using var client = Client(enabled: false, downstream);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Enabled_AllowsEmergencyRollbackToLegacyDirectDispatch()
    {
        var downstream = new RecordingHandler();
        using var client = Client(enabled: true, downstream);

        using var response = await client.PostAsJsonAsync(
            "api/v1/sent-payload/user/user-1",
            new { payload = new { title = "test" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Disabled_Routes_PerUser_Dispatch_To_Notification_Owner()
    {
        var downstream = new RecordingHandler();
        var owner = new RecordingNotificationOwner();
        using var client = Client(enabled: false, downstream, owner);

        using var response = await client.PostAsJsonAsync(
            "api/v1/sent-payload/user/user-42",
            new
            {
                payload = new
                {
                    title = "Ready",
                    body = "Parcel ready",
                    type = "request_ready",
                    notification_id = "request-ready:42",
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        downstream.RequestCount.Should().Be(0);
        owner.Events.Should().ContainSingle();
        var accepted = owner.Events[0];
        accepted.Receiver.Should().Be("user-42");
        accepted.EventType.Should().Be("gateway.request_ready");
        accepted.NotificationId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Disabled_Does_Not_Fabricate_Acceptance_When_Owner_Fails()
    {
        var downstream = new RecordingHandler();
        var owner = new RecordingNotificationOwner { Failure = new HttpRequestException("down") };
        using var client = Client(enabled: false, downstream, owner);

        using var response = await client.PostAsJsonAsync(
            "api/v1/sent-payload/user/user-42",
            new { payload = new { title = "Ready", body = "Parcel ready" } });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        downstream.RequestCount.Should().Be(0);
    }

    private static HttpClient Client(
        bool enabled,
        HttpMessageHandler downstream,
        INotificationOwnerClient? owner = null)
    {
        var guard = new GatewayDirectPushDispatchGuardHandler(
            Options.Create(new GatewayDirectPushDispatchOptions { Enabled = enabled }),
            owner)
        {
            InnerHandler = downstream,
        };
        return new HttpClient(guard) { BaseAddress = new Uri("http://push.test/") };
    }

    private sealed class RecordingNotificationOwner : INotificationOwnerClient
    {
        public List<NotificationOwnerEvent> Events { get; } = [];
        public Exception? Failure { get; init; }

        public Task<NotificationOwnerAcceptance> PublishAsync(
            NotificationOwnerEvent notification,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }
            Events.Add(notification);
            return Task.FromResult(new NotificationOwnerAcceptance(notification.NotificationId));
        }

        public Task<JsonElement> GetDeadLettersAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(new { message = "ok" }),
            });
        }
    }
}
