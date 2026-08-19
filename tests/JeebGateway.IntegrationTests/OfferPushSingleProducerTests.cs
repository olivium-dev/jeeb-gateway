using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
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

/// <summary>
/// The 2026-08-14 hardware regression, pinned: ONE server-side offer produced TWO FCM cards
/// 26 ms apart because the gateway ran BOTH producers for jeeb.offer_received.
///
/// <para><b>The two producers.</b> <see cref="OfferPushNotifier.NotifyNewOfferAsync"/> writes the
/// durable record (<c>POST notifications/jeeb.offer_received</c>) — notification-service produces
/// the push off that same POST — and then ALSO calls
/// <c>POST /api/v1/sent-payload/user/{id}</c> itself. Nothing made the two exclusive: the only
/// thing suppressing the second one was <see cref="GatewayDirectPushDispatchGuardHandler"/>
/// synthesizing a 503. The guard is now unconditional, so migration rungs cannot re-arm the
/// producer the D1 cut-over removed.</para>
///
/// <para><b>Owner: notification-service, in every rung.</b> jeeb.offer_received and
/// jeeb.offer_accepted are the only offer types with a centre route, and the row and the push
/// come from ONE upstream POST — so "the gateway produces" necessarily means "no inbox row".
/// The direct client survives only where the hand-over produced nothing at all.</para>
///
/// <para>Host-free, both legs observable at once: the REAL <see cref="NotificationRecordWriter"/>
/// over a counting handler, and the REAL push client behind the REAL guard, with a counter on
/// each side of the guard so an ATTEMPT is distinguishable from a delivered send.</para>
/// </summary>
public class OfferPushSingleProducerTests
{
    private const string Client = "client-sami";
    private const string Jeeber = "jeeber-winner";
    private const string RequestId = "req-f264e3c7";
    private const string OfferId = "offer-f264e3c7";

    private static readonly OfferReceivedNotificationContext Context = new(
        "Hamra, Beirut", "Achrafieh, Beirut", 30, DateTimeOffset.Parse("2026-08-14T04:12:55Z"));

    /// <summary>
    /// Both PushDispatchMode rungs keep the same permanently-disabled direct-dispatch state.
    /// </summary>
    public static TheoryData<string, bool> Rungs => new()
    {
        { "local", false },
        { "upstream-authority", true },
    };

    [Theory]
    [MemberData(nameof(Rungs))]
    public async Task One_offer_event_delivers_exactly_one_push(string rung, bool directDispatchArmed)
    {
        var centre = new CountingHandler();
        var wire = new CountingHandler();
        var notifier = Notifier(centre, wire, directDispatchArmed);

        await notifier.NotifyNewOfferAsync(Context, Client, RequestId, OfferId, 1m, CancellationToken.None);

        (centre.Posts + wire.Posts).Should().Be(
            1, $"rung {rung}: one offer event must reach exactly one push producer");
        centre.Posts.Should().Be(1, "notification-service owns the offer category");
        wire.Posts.Should().Be(0, "the gateway is not a second producer for offers");
    }

    [Theory]
    [MemberData(nameof(Rungs))]
    public async Task One_accepted_offer_delivers_exactly_one_push(string rung, bool directDispatchArmed)
    {
        var centre = new CountingHandler();
        var wire = new CountingHandler();
        var notifier = Notifier(centre, wire, directDispatchArmed, AcceptedRequest());

        await notifier.NotifyOfferAcceptedAsync(Jeeber, RequestId, OfferId, CancellationToken.None);

        (centre.Posts + wire.Posts).Should().Be(
            1, $"rung {rung}: one accept event must reach exactly one push producer");
        centre.Posts.Should().Be(1, "notification-service owns jeeb.offer_accepted too");
        wire.Posts.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Rungs))]
    public async Task The_gateway_does_not_even_attempt_a_second_send(string rung, bool directDispatchArmed)
    {
        // The guard is a net, not the design. Counting ABOVE it shows whether the gateway still
        // believes it is a producer — at "local" the 503 hid that belief rather than removing it.
        var attempts = new AttemptCountingHandler();
        var notifier = Notifier(new CountingHandler(), new CountingHandler(), directDispatchArmed, outer: attempts);

        await notifier.NotifyNewOfferAsync(Context, Client, RequestId, OfferId, 1m, CancellationToken.None);

        attempts.Posts.Should().Be(
            0, $"rung {rung}: the direct send must not be issued once the centre owns the event");
    }

    [Fact]
    public async Task With_the_centre_switched_off_direct_dispatch_stays_blocked()
    {
        // A notification-centre outage must be fixed forward; it cannot re-arm gateway sends.
        var wire = new CountingHandler();
        var notifier = Notifier(new CountingHandler(), wire, directDispatchArmed: true, durableWrite: false);

        await notifier.NotifyNewOfferAsync(Context, Client, RequestId, OfferId, 1m, CancellationToken.None);

        wire.Posts.Should().Be(0, "notification-service remains the sole push producer");
    }

    [Fact]
    public async Task An_offer_with_no_durable_context_does_not_bypass_the_owner()
    {
        // No context means there is no durable hand-over; the gateway still cannot become a producer.
        var centre = new CountingHandler();
        var wire = new CountingHandler();
        var notifier = Notifier(centre, wire, directDispatchArmed: true);

        await notifier.NotifyNewOfferAsync(Client, RequestId, OfferId, 1m, CancellationToken.None);

        centre.Posts.Should().Be(0);
        wire.Posts.Should().Be(0);
    }

    // -------- helpers ----------------------------------------------------------------

    private static OfferPushNotifier Notifier(
        CountingHandler centre,
        CountingHandler wire,
        bool directDispatchArmed,
        DeliveryRequest? request = null,
        bool durableWrite = true,
        AttemptCountingHandler? outer = null)
    {
        var guard = new GatewayDirectPushDispatchGuardHandler
        {
            InnerHandler = wire,
        };
        HttpMessageHandler pipeline = guard;
        if (outer is not null)
        {
            outer.InnerHandler = guard;
            pipeline = outer;
        }

        var push = new ServicePushNotificationClient(
            "http://push.test/",
            new HttpClient(pipeline) { BaseAddress = new Uri("http://push.test/") });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NotificationRecordWriter.EnabledConfigurationKey] = durableWrite ? "true" : "false",
            })
            .Build();
        var writer = new NotificationRecordWriter(
            new JeebNotificationRecordClient(
                new HttpClient(centre) { BaseAddress = new Uri("http://centre.test/") }),
            configuration,
            NullLogger<NotificationRecordWriter>.Instance);

        return new OfferPushNotifier(
            push, writer, (_, _) => Task.FromResult(request), NullLogger<OfferPushNotifier>.Instance);
    }

    private static DeliveryRequest AcceptedRequest() => new()
    {
        Id = RequestId,
        ClientId = Client,
        Status = RequestStatus.Accepted,
        Description = "Parcel",
        PickupAddress = "Hamra",
        DropoffAddress = "Achrafieh",
        AcceptedFee = 1m,
        AcceptedAt = DateTimeOffset.Parse("2026-08-14T04:12:55Z"),
        CreatedAt = DateTimeOffset.Parse("2026-08-14T04:10:00Z"),
    };

    /// <summary>Terminal: counts POSTs that reach it and answers the shape each client expects.</summary>
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
                    "{\"message\":\"ok\",\"timestamp\":\"2026-08-14T04:12:55+00:00\"}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    /// <summary>Sits ABOVE the guard, so it counts sends the gateway ISSUED, not sends allowed.</summary>
    private sealed class AttemptCountingHandler : DelegatingHandler
    {
        public int Posts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
