using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Dispatch;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class DispatchingRequestExpiryNotifierTests
{
    private const string ClientId = "b52eb018-3ece-44e9-856f-87f27ec32b7f";
    private const string RequestId = "request-123";

    [Fact]
    public async Task Expiry_Uses_External_Push_Service_Route_And_Static_Template()
    {
        var handler = new RecordingPushHandler(HttpStatusCode.Created);
        using var services = BuildServices(handler);
        var notifier = CreateNotifier(services);

        await notifier.NotifyExpiredAsync(
            ClientId,
            RequestId,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Scheme.Should().Be(Uri.UriSchemeHttp);
        request.Uri.Port.Should().Be(10040);
        request.Uri.AbsolutePath.Should().Be($"/api/v1/sent-payload/user/{ClientId}");

        var payload = JObject.Parse(request.Body)["payload"]!.Value<JObject>()!;
        payload.Value<string>("title").Should().Be("Request Expired");
        payload.Value<string>("body").Should().Be(
            $"Your request {RequestId} expired before a Jeeber accepted it. Tap to re-request.");
        payload.Value<string>("type").Should().Be("request_expired");
        payload.Value<string>("requestId").Should().Be(RequestId);
        payload.Value<string>("request_id").Should().Be(RequestId);
        payload.Value<string>("language").Should().Be("en");

        // The IPushNotificationService leg is gone: the type itself no longer exists.
        // InGatewayPushStackDeletedTests holds that, and it runs in the project that compiles.
        services.GetService<IJeebNotificationDispatcher>().Should().BeNull(
            "the old dispatcher terminates in the in-gateway NoDevices path");
    }

    [Fact]
    public async Task Nudge_Uses_External_Push_Service_And_Static_Template()
    {
        var handler = new RecordingPushHandler(HttpStatusCode.Created);
        using var services = BuildServices(handler);
        var notifier = CreateNotifier(services);

        await notifier.NotifyTryExpandTierAsync(
            ClientId,
            RequestId,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await notifier.NotifyTryExpandTierAsync(
            ClientId,
            RequestId,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Uri.AbsolutePath.Should().Be($"/api/v1/sent-payload/user/{ClientId}");

        var payload = JObject.Parse(request.Body)["payload"]!.Value<JObject>()!;
        payload.Value<string>("title").Should().Be("Still looking");
        payload.Value<string>("body").Should().Be(
            $"No Jeeber has accepted {RequestId} yet. Try a faster tier.");
        payload.Value<string>("type").Should().Be("try_expand_tier");
    }

    [Fact]
    public async Task Push_Service_Failure_Does_Not_Fail_Expiry_Flow()
    {
        var handler = new RecordingPushHandler(HttpStatusCode.ServiceUnavailable);
        using var services = BuildServices(handler);
        var notifier = CreateNotifier(services);

        var act = () => notifier.NotifyExpiredAsync(
            ClientId,
            RequestId,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        handler.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// PUSH-LOOP regression (Bug A). The dedupe row used to be written only AFTER
    /// a successful push, so a push that kept failing never recorded
    /// <c>request-nudge:{requestId}</c> — <c>ExistsAsync</c> never deduplicated and
    /// <see cref="RequestNudgeSweeper"/> re-sent the identical nudge on EVERY 30s
    /// sweep, forever (four stuck requests × 56 re-sends in one observed window).
    /// The entry is now reserved BEFORE the push, so a FAILED push still fires once.
    /// </summary>
    [Fact]
    public async Task Failed_Push_Still_Records_Dedupe_Entry_So_Later_Sweeps_Do_Not_Resend()
    {
        // 500 = the push service's "every device token for this user is dead" shape.
        var handler = new RecordingPushHandler(HttpStatusCode.InternalServerError);
        using var services = BuildServices(handler);
        var notifier = CreateNotifier(services);

        // Three sweeps over the same still-pending request.
        for (var sweep = 0; sweep < 3; sweep++)
        {
            await notifier.NotifyTryExpandTierAsync(
                ClientId,
                RequestId,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        handler.Requests.Should().ContainSingle(
            "a nudge is fire-once: a failed push must not be re-sent on every subsequent sweep");

        var outbox = services.GetRequiredService<INotificationDispatchOutbox>();
        (await outbox.ExistsAsync($"request-nudge:{RequestId}"))
            .Should().BeTrue("the dedupe entry must be recorded even though the push failed");

        var dlq = await outbox.GetDlqAsync();
        var entry = dlq.Should().ContainSingle(
            "the failed attempt is booked to the DLQ, not silently dropped").Subject;
        entry.IdempotencyKey.Should().Be($"request-nudge:{RequestId}");
        entry.AttemptCount.Should().Be(1);
        entry.LastError.Should().NotBeNullOrWhiteSpace();
        outbox.PendingCount.Should().Be(
            0,
            "nothing re-drives this outbox path, so a failed entry must not linger as Pending");
    }

    [Fact]
    public async Task Successful_Push_Marks_The_Entry_Delivered()
    {
        var handler = new RecordingPushHandler(HttpStatusCode.Created);
        using var services = BuildServices(handler);
        var notifier = CreateNotifier(services);

        await notifier.NotifyExpiredAsync(
            ClientId,
            RequestId,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var outbox = services.GetRequiredService<INotificationDispatchOutbox>();
        (await outbox.ExistsAsync($"request-expired:{RequestId}")).Should().BeTrue();
        (await outbox.GetDlqAsync()).Should().BeEmpty("a successful push is not a failure");
        outbox.PendingCount.Should().Be(0, "a delivered entry leaves the Pending state");
    }

    private static DispatchingRequestExpiryNotifier CreateNotifier(IServiceProvider services) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DispatchingRequestExpiryNotifier>.Instance);

    private static ServiceProvider BuildServices(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton<INotificationTemplateRenderer, StaticNotificationTemplateRenderer>();
        services.AddSingleton<INotificationDispatchOutbox, InMemoryNotificationDispatchOutbox>();
        services.AddScoped(_ => new ServicePushNotificationClient(
            "http://push-service:10040/",
            new HttpClient(handler, disposeHandler: false)));
        return services.BuildServiceProvider();
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

    private sealed class RecordingPushHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public RecordingPushHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(
                    "{\"message\":\"ok\",\"timestamp\":\"2026-07-21T12:00:00Z\"}",
                    Encoding.UTF8,
                    "application/json"),
                RequestMessage = request,
            };
        }
    }
}
