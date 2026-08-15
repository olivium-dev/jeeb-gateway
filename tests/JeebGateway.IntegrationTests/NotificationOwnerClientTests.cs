using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class NotificationOwnerClientTests
{
    [Fact]
    public async Task Publish_Forwards_Stable_Notification_Identity()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, "{}");
        var client = Owner(handler);
        var id = NotificationOwnerEventId.FromIdempotencyKey("delivery:request-42");

        await client.PublishAsync(new NotificationOwnerEvent(
            id, "user-1", "Ready", "Parcel ready", "gateway.request_ready",
            new Dictionary<string, object?> { ["request_id"] = "42" }),
            CancellationToken.None);

        handler.Method.Should().Be(HttpMethod.Post);
        handler.Path.Should().Be("/notifications/events");
        handler.IdempotencyKey.Should().Be(id.ToString("D"));
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("notification_id").GetGuid().Should().Be(id);
        body.RootElement.GetProperty("receiver").GetString().Should().Be("user-1");
    }

    [Fact]
    public async Task Publish_Conflict_Is_Typed_And_Never_Accepted()
    {
        var handler = new RecordingHandler(HttpStatusCode.Conflict, "{}");
        var client = Owner(handler);
        var id = Guid.NewGuid();

        var act = async () => await client.PublishAsync(new NotificationOwnerEvent(
            id, "user-1", "Title", "Body", "gateway.generic",
            new Dictionary<string, object?>()), CancellationToken.None);

        (await act.Should().ThrowAsync<NotificationOwnerConflictException>())
            .Which.NotificationId.Should().Be(id);
    }

    [Fact]
    public async Task Dlq_Uses_Dedicated_Admin_Bearer()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"items\":[]}");
        var client = Owner(handler, "dlq-admin-secret");

        await client.GetDeadLettersAsync(CancellationToken.None);

        handler.Path.Should().Be("/dlq");
        handler.Authorization.Should().Be("Bearer dlq-admin-secret");
    }

    [Fact]
    public void Arbitrary_Idempotency_Key_Maps_To_Stable_Uuid4()
    {
        var first = NotificationOwnerEventId.FromIdempotencyKey("same-command");
        var second = NotificationOwnerEventId.FromIdempotencyKey("same-command");

        first.Should().Be(second);
        first.ToString("D")[14].Should().Be('4');
        first.ToString("D")[19].Should().BeOneOf('8', '9', 'a', 'b');
    }

    private static NotificationOwnerClient Owner(
        RecordingHandler handler,
        string? dlqToken = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://notification.test/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ServiceNotificationClient:DlqAdminToken"] = dlqToken,
            }).Build();
        return new NotificationOwnerClient(new FixedHttpClientFactory(http), config);
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(HttpStatusCode status, string responseBody)
        : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values)
                ? values.Single()
                : null;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
