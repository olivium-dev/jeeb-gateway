using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Push;
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

        services.GetService<IPushNotificationService>().Should().BeNull(
            "expiry delivery must not reach the in-gateway device-token registry");
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
