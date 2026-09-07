using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class ServiceCallbackRouteAliasHttpTests
{
    [Theory]
    [InlineData("requestId", false)]
    [InlineData("request_id", false)]
    [InlineData("requestId", true)]
    public async Task Actual_Http_Callback_Emits_Request_Route_With_Different_Offer_Aliases(
        string requestKey, bool includeEqualAlias)
    {
        await using var factory = new AliasHttpFactory();
        using var client = factory.CreateClient();
        var data = new Dictionary<string, string>
        {
            [requestKey] = "req7",
            ["entityId"] = "offer42",
            ["offerId"] = "offer42",
            ["deliveryId"] = "delivery9",
        };
        if (includeEqualAlias)
            data["request_id"] = "req7";

        // Callback auth remains the existing anonymous service contract, as in
        // ServiceCallbackNotifyTests: no production authentication seam is changed.
        using var response = await client.PostAsJsonAsync("/svc-callbacks/notify", new
        {
            notificationType = "jeeb.offer_updated",
            recipientUserId = "11111111-2222-3333-4444-555555555555",
            locale = "en",
            silent = false,
            idempotencyKey = $"alias-http:{Guid.NewGuid()}",
            data,
        });
        var responseText = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, responseText);
        using var body = JsonDocument.Parse(responseText);
        body.RootElement.GetProperty("status").GetString().Should().Be("Queued");
        body.RootElement.GetProperty("wasDeduplicated").GetBoolean().Should().BeFalse();

        var emission = factory.Sink.Emissions.Should().ContainSingle().Subject;
        emission.EventType.Should().Be("jeeb.offer_updated");
        emission.Receiver.Should().Be("11111111-2222-3333-4444-555555555555");
        emission.Data["deepLink"].Should().Be("jeeb://requests/req7/offers");
        emission.Data[requestKey].Should().Be("req7");
        emission.Data["entityId"].Should().Be("offer42");
        emission.Data["offerId"].Should().Be("offer42");
        emission.Data["deliveryId"].Should().Be("delivery9");
        factory.Outbound.Attempts.Should().Be(0);
    }

    private sealed class AliasHttpFactory : WebApplicationFactory<Program>
    {
        internal RecordingSink Sink { get; } = new();
        internal ForbidOutboundHttp Outbound { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                // TestWebApplicationFactory supplies explicit in-memory owners.
                // No unrelated workers or service calls may run in this packet.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IGenericEventDispatcher>();
                services.AddSingleton<IGenericEventDispatcher>(Sink);
                services.RemoveAll<INotificationRecordWriter>();
                services.AddSingleton<INotificationRecordWriter, UnexpectedRecordWriter>();
                services.AddSingleton<IHttpMessageHandlerBuilderFilter>(Outbound);
            });
        }
    }

    private sealed class UnexpectedRecordWriter : FakeNotificationRecordWriterBase;

    private sealed record Emission(
        string EventType, string Receiver, IReadOnlyDictionary<string, string> Data);

    private sealed class RecordingSink : IGenericEventDispatcher
    {
        internal List<Emission> Emissions { get; } = new();

        public Task<GenericEventDispatchOutcome> DispatchAsync(
            string eventType, string receiver, string entityId, string title,
            string body, IReadOnlyDictionary<string, string> data,
            string refreshCategory, CancellationToken ct)
        {
            Emissions.Add(new Emission(eventType, receiver,
                new Dictionary<string, string>(data)));
            return Task.FromResult(new GenericEventDispatchOutcome(
                GenericEventDispatchClassification.Accepted, 202));
        }
    }

    private sealed class ForbidOutboundHttp : IHttpMessageHandlerBuilderFilter
    {
        internal int Attempts;

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
            => builder =>
            {
                next(builder);
                builder.PrimaryHandler = new ForbiddenHandler(this);
                builder.AdditionalHandlers.Clear();
            };

        private sealed class ForbiddenHandler(ForbidOutboundHttp owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.Attempts);
                throw new InvalidOperationException("This callback test forbids outbound HTTP.");
            }
        }
    }
}
