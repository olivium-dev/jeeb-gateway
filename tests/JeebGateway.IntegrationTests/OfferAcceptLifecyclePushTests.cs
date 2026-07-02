using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Notifications;
using JeebGateway.Services.Clients;
using JeebGateway.service.ServicePushNotification;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// sprint-009 Lane E — the accept-lifecycle push fan-out. When a client accepts one
/// jeeber's offer, the gateway (the sole composer; the offer-service accept saga owns no
/// notification) must push:
///   • exactly ONE <c>jeeb.offer_accepted</c> (type=offer_accepted) to the WINNING jeeber, and
///   • one <c>jeeb.offer_rejected</c> (type=offer_lost) per id in the envelope's
///     <c>RejectedOfferIds</c>, addressed to each losing bidder resolved from the offer
///     routing index.
/// Two layers: unit tests on <see cref="OfferPushNotifier"/> (payload + deep link + degrade),
/// and END-TO-END tests through BOTH accept routes (<c>POST /offers/{id}/accept</c> — legacy
/// <c>OffersController</c>, and <c>POST /v1/offers/{id}/accept</c> — <c>JeebOffersController</c>)
/// proving the fan-out fires once per recipient and that a throwing push client never breaks
/// the committed 200.
/// </summary>
public class OfferAcceptLifecyclePushTests
{
    // ---------------------------------------------------------------------
    // Notifier unit tests
    // ---------------------------------------------------------------------

    [Fact]
    public async Task OfferAccepted_NotifiesWinner_WithAcceptedTemplate_AndOffersDeepLink()
    {
        var push = new RecordingUserPushClient();
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        await notifier.NotifyOfferAcceptedAsync("jeeber-winner", "req-1", "offer-win", CancellationToken.None);

        push.Sends.Should().ContainSingle();
        var send = push.Sends.Single();
        send.UserId.Should().Be("jeeber-winner", "the accepted push goes to the winning jeeber");

        var payload = (IDictionary<string, object?>)send.Payload;
        payload["type"].Should().Be("offer_accepted");
        payload["category"].Should().Be("delivery");
        payload["title"].Should().Be("Offer Accepted", "rendered from the jeeb.offer_accepted catalog template");
        payload["requestId"].Should().Be("req-1");
        payload["request_id"].Should().Be("req-1");
        payload["offerId"].Should().Be("offer-win");
        payload["deepLink"].Should().Be("jeeb://offers/offer-win");
        payload.Should().NotContainKey("data", "routing fields are flat top-level entries");
    }

    [Fact]
    public async Task OfferLost_NotifiesLoser_WithRejectedTemplate_AndOffersDeepLink()
    {
        var push = new RecordingUserPushClient();
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        await notifier.NotifyOfferLostAsync("jeeber-loser", "req-1", "offer-lost", CancellationToken.None);

        var send = push.Sends.Single();
        send.UserId.Should().Be("jeeber-loser");

        var payload = (IDictionary<string, object?>)send.Payload;
        payload["type"].Should().Be("offer_lost");
        payload["category"].Should().Be("delivery");
        payload["title"].Should().Be("Offer Not Selected");
        ((string)payload["body"]!).Should().Contain("wasn't selected");
        payload["offerId"].Should().Be("offer-lost");
        payload["deepLink"].Should().Be("jeeb://offers/offer-lost");
    }

    [Fact]
    public async Task LifecyclePush_PushServiceFault_IsSwallowed_NeverThrows()
    {
        var push = new RecordingUserPushClient { Throw = true };
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        var acceptAct = async () => await notifier.NotifyOfferAcceptedAsync("w", "r", "o", CancellationToken.None);
        var lostAct = async () => await notifier.NotifyOfferLostAsync("l", "r", "o", CancellationToken.None);

        await acceptAct.Should().NotThrowAsync();
        await lostAct.Should().NotThrowAsync();
        push.Attempts.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task LifecyclePush_BlankRecipient_PushesNothing()
    {
        var push = new RecordingUserPushClient();
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        await notifier.NotifyOfferAcceptedAsync(" ", "r", "o", CancellationToken.None);
        await notifier.NotifyOfferLostAsync("", "r", "o", CancellationToken.None);

        push.Sends.Should().BeEmpty();
        push.Attempts.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // E2E — legacy OffersController: POST /offers/{offerId}/accept
    // ---------------------------------------------------------------------

    [Fact]
    public async Task LegacyAccept_SendsWinnerPushOnce_AndOneLoserPushPerRejectedId()
    {
        var push = new RecordingUserPushClient();
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-win",
                    JeeberId = "jeeber-winner",
                    RejectedOfferIds = new[] { "offer-loser-a", "offer-loser-b" }
                }
            }
        };
        using var factory = NewFactory(fake, push);

        // Winner + both losers recorded so the loser bidders resolve from the index.
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record("offer-win", "req-1", "jeeber-winner");
        index.Record("offer-loser-a", "req-1", "jeeber-loser-a");
        index.Record("offer-loser-b", "req-1", "jeeber-loser-b");

        var resp = await ClientActor(factory, "client-owner").PostAsync("/offers/offer-win/accept", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Exactly 3 pushes: 1 winner + 2 losers.
        push.Sends.Should().HaveCount(3);

        var winner = push.Sends.Single(s => TypeOf(s) == "offer_accepted");
        winner.UserId.Should().Be("jeeber-winner");
        PayloadOf(winner)["offerId"].Should().Be("offer-win");

        var losers = push.Sends.Where(s => TypeOf(s) == "offer_lost").ToList();
        losers.Should().HaveCount(2);
        losers.Select(s => s.UserId).Should().BeEquivalentTo(new[] { "jeeber-loser-a", "jeeber-loser-b" });
        losers.Select(s => (string)PayloadOf(s)["offerId"]!)
              .Should().BeEquivalentTo(new[] { "offer-loser-a", "offer-loser-b" });
    }

    [Fact]
    public async Task LegacyAccept_UnknownLoserBidder_IsSkipped_NotGuessed()
    {
        var push = new RecordingUserPushClient();
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-win2",
                    JeeberId = "jeeber-winner2",
                    // One resolvable loser, one NOT recorded in the index.
                    RejectedOfferIds = new[] { "offer-known", "offer-unknown" }
                }
            }
        };
        using var factory = NewFactory(fake, push);
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record("offer-win2", "req-2", "jeeber-winner2");
        index.Record("offer-known", "req-2", "jeeber-known");
        // offer-unknown intentionally NOT recorded.

        var resp = await ClientActor(factory, "client-owner2").PostAsync("/offers/offer-win2/accept", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Winner + only the resolvable loser (the unknown bidder is skipped, never guessed).
        push.Sends.Should().HaveCount(2);
        push.Sends.Count(s => TypeOf(s) == "offer_lost").Should().Be(1);
        push.Sends.Single(s => TypeOf(s) == "offer_lost").UserId.Should().Be("jeeber-known");
    }

    [Fact]
    public async Task LegacyAccept_WhenPushClientThrows_StillReturns200()
    {
        var push = new RecordingUserPushClient { Throw = true };
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-boom",
                    JeeberId = "jeeber-boom",
                    RejectedOfferIds = new[] { "offer-loser-boom" }
                }
            }
        };
        using var factory = NewFactory(fake, push);
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record("offer-boom", "req-boom", "jeeber-boom");
        index.Record("offer-loser-boom", "req-boom", "jeeber-loser-boom");

        var resp = await ClientActor(factory, "client-boom").PostAsync("/offers/offer-boom/accept", null);

        // Degrade-don't-fail: a throwing push client must never flip the committed 200.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        push.Attempts.Should().BeGreaterThanOrEqualTo(1);
    }

    // ---------------------------------------------------------------------
    // E2E — V1 JeebOffersController: POST /v1/offers/{id}/accept
    // ---------------------------------------------------------------------

    [Fact]
    public async Task V1Accept_SendsWinnerPushOnce_AndOneLoserPushPerRejectedId()
    {
        var push = new RecordingUserPushClient();
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "v1-offer-win",
                    JeeberId = "v1-jeeber-winner",
                    RejectedOfferIds = new[] { "v1-loser-a" }
                }
            }
        };
        using var factory = NewFactory(fake, push);
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record("v1-offer-win", "v1-req", "v1-jeeber-winner");
        index.Record("v1-loser-a", "v1-req", "v1-jeeber-loser-a");

        var resp = await ClientActor(factory, "v1-client").PostAsync("/v1/offers/v1-offer-win/accept", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        push.Sends.Should().HaveCount(2);
        var winner = push.Sends.Single(s => TypeOf(s) == "offer_accepted");
        winner.UserId.Should().Be("v1-jeeber-winner");
        var loser = push.Sends.Single(s => TypeOf(s) == "offer_lost");
        loser.UserId.Should().Be("v1-jeeber-loser-a");
        PayloadOf(loser)["offerId"].Should().Be("v1-loser-a");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static string TypeOf(SendRecord s) => (string)((IDictionary<string, object?>)s.Payload)["type"]!;
    private static IDictionary<string, object?> PayloadOf(SendRecord s) => (IDictionary<string, object?>)s.Payload;

    private static WebApplicationFactory<Program> NewFactory(IOfferServiceClient fake, RecordingUserPushClient push)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", "true" },
                        { "FeatureFlags:UseUpstream:Delivery", "false" }
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(fake);
                    services.RemoveAll<IDeliveryServiceClient>();
                    services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryServiceClient());
                    services.RemoveAll<ServicePushNotificationClient>();
                    services.AddSingleton<ServicePushNotificationClient>(push);
                });
            });

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return c;
    }

    private sealed record SendRecord(string UserId, object Payload);

    private sealed class RecordingUserPushClient : ServicePushNotificationClient
    {
        public RecordingUserPushClient() : base("http://localhost", new HttpClient()) { }

        public ConcurrentQueue<SendRecord> Sends { get; } = new();
        public int Attempts { get; private set; }
        public bool Throw { get; init; }

        public override Task<SentPayloadResponse> Send_notification_to_userAsync(
            string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Throw)
            {
                throw new InvalidOperationException("push service unavailable");
            }
            Sends.Enqueue(new SendRecord(user_id, body.Payload));
            return Task.FromResult(new SentPayloadResponse { Message = "ok", Timestamp = DateTimeOffset.UtcNow });
        }
    }

    private sealed class FakeOfferServiceClient : IOfferServiceClient
    {
        public required OfferAcceptResult Result { get; init; }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(Result);

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeDeliveryServiceClient : IDeliveryServiceClient
    {
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct)
            => Task.FromResult(0);
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
            => Task.FromResult(new DeliveryRowUpstream { DeliveryId = body.Id });

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
