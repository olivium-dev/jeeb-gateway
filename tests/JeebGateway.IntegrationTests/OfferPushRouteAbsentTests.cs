using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

// G6 (2026-09-04): a typed centre route the deployment profile never declared answers 404/405,
// and the seat must fall back to the static events route instead of assuming upstream owns it.
public class OfferPushRouteAbsentTests
{
    // Starlette answers 405 (partial match on PATCH /notifications/{id}), 404 without one.
    public static TheoryData<HttpStatusCode> AbsentRoute =>
        new() { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed };

    private const string Client = "client-nour";
    private const string Jeeber = "jeeber-karim";
    private const string RequestId = "be14d19f-c210-45d7-8769-2e558418a708";
    private const string OfferId = "e8f9fb2f-1848-4b0a-bbdf-9f6d50e54693";

    private static readonly OfferReceivedNotificationContext Context = new(
        "Hamra, Beirut", "Achrafieh, Beirut", 80, DateTimeOffset.Parse("2026-09-04T01:16:26Z"));

    [Theory]
    [MemberData(nameof(AbsentRoute))]
    public async Task An_absent_typed_centre_route_is_route_absent_and_is_never_read_back(
        HttpStatusCode typedStatus)
    {
        var centre = new RoutingCentreHandler(typedStatus);
        var writer = Writer(centre);

        var outcome = await writer.WriteOfferReceivedAsync(ReceivedRecord(), CancellationToken.None);

        outcome.Classification.Should()
            .Be(NotificationRecordWriteClassification.RouteAbsent,
                "a route that does not exist cannot have committed a row");
        outcome.UpstreamStatus.Should().Be((int)typedStatus);
        centre.Gets.Should().Be(
            0, "the read-back can only turn a known no-producer into an ambiguous Unproven");
    }

    [Theory]
    [MemberData(nameof(AbsentRoute))]
    public async Task An_offer_still_reaches_the_client_when_the_typed_centre_route_is_absent(
        HttpStatusCode typedStatus)
    {
        var centre = new RoutingCentreHandler(typedStatus);
        var wire = new CountingHandler();
        var notifier = Notifier(centre, wire);

        await notifier.NotifyNewOfferAsync(
            Context, Client, RequestId, OfferId, 5m, CancellationToken.None);

        centre.EventPosts.Should().Be(
            1, "the static generic-event route is the only producer left when the typed one is gone");
        wire.Posts.Should().Be(0, "ADR-0013: the gateway is never a direct push producer");

        var body = JsonDocument.Parse(centre.EventBodies.Single()).RootElement;
        body.GetProperty("event_type").GetString().Should().Be("jeeb.offer_received");
        body.GetProperty("receiver").GetString().Should().Be(Client);
        body.GetProperty("title").GetString().Should().Be("New offer on your request");
        var data = body.GetProperty("data");
        data.GetProperty("category").GetString().Should().Be(PushSilencePolicy.CategoryNewOffer);
        data.GetProperty("requestId").GetString().Should().Be(RequestId);
        data.GetProperty("offerId").GetString().Should().Be(OfferId);
        body.GetProperty("notification_id").GetString().Should().Be(
            NotificationCorrelationId.Create(
                OfferReceivedNotificationRecord.TemplateKey, Client, OfferId),
            "the fallback must mint the SAME ncid as the typed write, or a replay duplicates");
    }

    [Fact]
    public async Task A_present_typed_centre_route_still_produces_exactly_one_push()
    {
        var centre = new RoutingCentreHandler(typedStatus: HttpStatusCode.Created);
        var wire = new CountingHandler();
        var notifier = Notifier(centre, wire);

        await notifier.NotifyNewOfferAsync(
            Context, Client, RequestId, OfferId, 5m, CancellationToken.None);

        centre.TypedPosts.Should().Be(1);
        centre.EventPosts.Should().Be(
            0, "the typed write already handed the event over; a second seam would duplicate it");
        wire.Posts.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(AbsentRoute))]
    public async Task The_winner_push_survives_an_absent_offer_accepted_route_too(
        HttpStatusCode typedStatus)
    {
        var centre = new RoutingCentreHandler(typedStatus);
        var wire = new CountingHandler();
        var notifier = Notifier(centre, wire, AcceptedRequest());

        await notifier.NotifyOfferAcceptedAsync(Jeeber, RequestId, OfferId, CancellationToken.None);

        centre.EventPosts.Should().Be(1);
        wire.Posts.Should().Be(0);
        var body = JsonDocument.Parse(centre.EventBodies.Single()).RootElement;
        body.GetProperty("event_type").GetString().Should().Be("jeeb.offer_accepted");
        body.GetProperty("receiver").GetString().Should().Be(Jeeber);
        body.GetProperty("data").GetProperty("category").GetString()
            .Should().Be(PushSilencePolicy.CategoryOfferAccepted);
    }

    // -------- helpers ----------------------------------------------------------------

    private static NotificationRecordWriter Writer(HttpMessageHandler centre)
        => new(
            new JeebNotificationRecordClient(
                new HttpClient(centre) { BaseAddress = new Uri("http://centre.test/") }),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [NotificationRecordWriter.EnabledConfigurationKey] = "true",
                })
                .Build(),
            NullLogger<NotificationRecordWriter>.Instance);

    private static OfferPushNotifier Notifier(
        RoutingCentreHandler centre,
        CountingHandler wire,
        DeliveryRequest? request = null)
    {
        var push = new ServicePushNotificationClient(
            "http://push.test/",
            new HttpClient(new GatewayDirectPushDispatchGuardHandler { InnerHandler = wire })
            {
                BaseAddress = new Uri("http://push.test/"),
            });

        var recordClient = new JeebNotificationRecordClient(
            new HttpClient(centre) { BaseAddress = new Uri("http://centre.test/") });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NotificationRecordWriter.EnabledConfigurationKey] = "true",
            })
            .Build();

        return new OfferPushNotifier(
            push,
            new NotificationRecordWriter(
                recordClient, configuration, NullLogger<NotificationRecordWriter>.Instance),
            new GenericEventDispatcher(
                recordClient, configuration, NullLogger<GenericEventDispatcher>.Instance),
            (_, _) => Task.FromResult(request),
            NullLogger<OfferPushNotifier>.Instance);
    }

    private static OfferReceivedNotificationRecord ReceivedRecord() => new()
    {
        Sender = "jeeb-gateway",
        Receiver = Client,
        NotificationCorrelationId = NotificationCorrelationId.Create(
            OfferReceivedNotificationRecord.TemplateKey, Client, OfferId),
        Title = "New offer on your request",
        Description = "You received a new offer for $5. Tap to review.",
        Payload = new OfferReceivedNotificationPayload
        {
            UserId = Client,
            OfferId = OfferId,
            RequestId = "req-1",
            OfferAmount = 5m,
            DeliveryFee = 5m,
            EstimatedDuration = "80",
            CreatedAt = Context.CreatedAt,
        },
    };

    private static DeliveryRequest AcceptedRequest() => new()
    {
        Id = RequestId,
        ClientId = Client,
        Status = RequestStatus.Accepted,
        Description = "t1 final 011118",
        PickupAddress = "Hamra",
        DropoffAddress = "Achrafieh",
        AcceptedFee = 5m,
        AcceptedAt = DateTimeOffset.Parse("2026-09-04T01:19:42Z"),
        CreatedAt = DateTimeOffset.Parse("2026-09-04T01:11:56Z"),
    };

    /// <summary>Staging's shape: typed jeeb.* routes answer as configured, events always 201.</summary>
    private sealed class RoutingCentreHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _typedStatus;

        public RoutingCentreHandler(HttpStatusCode typedStatus) => _typedStatus = typedStatus;

        public int TypedPosts { get; private set; }

        public int EventPosts { get; private set; }

        public int Gets { get; private set; }

        public List<string> EventBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get)
            {
                Gets++;
                return Json(HttpStatusCode.OK, "{\"messages\":[],\"total_messages\":0}", request);
            }

            if (path.EndsWith("/notifications/events", StringComparison.Ordinal))
            {
                EventPosts++;
                EventBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(HttpStatusCode.Created, "{\"id\":\"1\"}", request);
            }

            TypedPosts++;
            return Json(_typedStatus, "{\"detail\":\"no such route\"}", request);
        }

        private static HttpResponseMessage Json(
            HttpStatusCode status, string body, HttpRequestMessage request)
            => new(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    /// <summary>Terminal for the push wire; sits UNDER the guard, so it counts delivered sends.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Posts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{\"message\":\"ok\",\"timestamp\":\"2026-09-04T01:16:26+00:00\"}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
