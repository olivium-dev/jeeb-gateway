using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Notifications;
using JeebGateway.Observability;
using JeebGateway.Requests;
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
/// and END-TO-END tests through the accept route (<c>POST /v1/offers/{id}/accept</c> —
/// <c>JeebOffersController</c>) proving the fan-out fires once per recipient, that an
/// unresolvable losing bidder is skipped rather than guessed, and that a throwing push client
/// never breaks the committed 200. (Until 2026-08-01 this file ran the same E2E block twice,
/// against a second, duplicate accept route on the legacy <c>OffersController</c>; the owner
/// retired that route, so there is one surface and one block.)
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

    /// <summary>
    /// Was …WritesBeforePush. jeeb.offer_accepted has a centre route, so the write IS the push —
    /// a direct send alongside it is the 2026-08-14 duplicate in its accept-side form.
    /// </summary>
    [Fact]
    public async Task OfferAccepted_WithAuthoritativeFee_WritesTheRecord_AndTheCentreOwnsThePush()
    {
        var timeline = new List<string>();
        var writer = new RecordingNotificationRecordWriter(timeline);
        var push = new RecordingUserPushClient { BeforeSend = () => timeline.Add("push") };
        var request = AcceptedRequest(acceptedFee: 12.5m);
        var notifier = new OfferPushNotifier(
            push,
            writer,
            (_, _) => Task.FromResult<DeliveryRequest?>(request),
            NullLogger<OfferPushNotifier>.Instance);

        await notifier.NotifyOfferAcceptedAsync(
            "jeeber-winner",
            request.Id,
            "offer-win",
            CancellationToken.None);

        timeline.Should().Equal("write");
        push.Sends.Should().BeEmpty(
            "notification-service produces this push off the same POST — a direct send here is "
            + "the second card measured on hardware 2026-08-14");
        var record = writer.Accepted.Should().ContainSingle().Subject;
        record.Payload.AcceptedAmount.Should().Be(12.5m);
        record.Payload.JeeberId.Should().Be("jeeber-winner");
        record.Payload.PickupLocation.Should().Be("Hamra");
        record.Payload.DeliveryLocation.Should().Be("Achrafieh");

        // Copy + correlation parity is only observable where the centre declines to produce and
        // the direct client is the sole producer left.
        var fallback = new RecordingUserPushClient();
        await new OfferPushNotifier(
            fallback,
            new RecordingNotificationRecordWriter(
                classification: NotificationRecordWriteClassification.Disabled),
            (_, _) => Task.FromResult<DeliveryRequest?>(request),
            NullLogger<OfferPushNotifier>.Instance)
            .NotifyOfferAcceptedAsync("jeeber-winner", request.Id, "offer-win", CancellationToken.None);

        var payload = PayloadOf(fallback.Sends.Single());
        payload["title"].Should().Be(record.Title);
        payload["body"].Should().Be(record.Description);
        payload["notificationId"].Should().Be(record.NotificationCorrelationId);
        payload["notification_id"].Should().Be(record.NotificationCorrelationId);
    }

    [Fact]
    public async Task AC8e_AC8f_AC9d_NullAcceptedFee_PostsZero_IncrementsReasonCounter_AndPushes()
    {
        const string offerId = "offer-no-fee";
        long matchingSkipCount = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == BusinessOutcomeTelemetry.MeterName &&
                instrument.Name == "notif.durable_write.skipped")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, state) =>
            {
                var isExpectedEntity = false;
                var isAcceptedAmountAbsent = false;
                foreach (var tag in tags)
                {
                    isExpectedEntity |= tag.Key == "entityId" &&
                        Equals(tag.Value, offerId);
                    isAcceptedAmountAbsent |= tag.Key == "reason" &&
                        Equals(tag.Value, "accepted_amount_absent");
                }
                if (isExpectedEntity && isAcceptedAmountAbsent)
                {
                    Interlocked.Add(ref matchingSkipCount, measurement);
                }
            });
        meterListener.Start();

        var handler = new CountingNotificationHandler();
        var writer = NewConcreteWriter(handler);
        var push = new RecordingUserPushClient();
        var request = AcceptedRequest(acceptedFee: null);
        var notifier = new OfferPushNotifier(
            push,
            writer,
            (_, _) => Task.FromResult<DeliveryRequest?>(request),
            NullLogger<OfferPushNotifier>.Instance);

        await notifier.NotifyOfferAcceptedAsync(
            "jeeber-winner",
            request.Id,
            offerId,
            CancellationToken.None);

        handler.Posts.Should().Be(0);
        push.Sends.Should().ContainSingle();
        TypeOf(push.Sends.Single()).Should().Be("offer_accepted");
        matchingSkipCount.Should().Be(1);
    }

    [Fact]
    public async Task OfferAccepted_ThrowingRequestStore_DoesNotWrite_StillPushesAndNeverThrows()
    {
        var writer = new RecordingNotificationRecordWriter();
        var push = new RecordingUserPushClient();
        var notifier = new OfferPushNotifier(
            push,
            writer,
            (_, _) => Task.FromException<DeliveryRequest?>(
                new InvalidOperationException("request store unavailable")),
            NullLogger<OfferPushNotifier>.Instance);

        var act = async () => await notifier.NotifyOfferAcceptedAsync(
            "jeeber-winner",
            "req-1",
            "offer-win",
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        writer.Accepted.Should().BeEmpty();
        push.Sends.Should().ContainSingle();
    }

    // ---------------------------------------------------------------------
    // E2E — legacy OffersController: (Removed 2026-08-01)
    //
    // Three tests lived here, driving POST /offers/{offerId}/accept, which the owner
    // retired on 2026-08-01 as a duplicate of POST /v1/offers/{id}/accept:
    //   LegacyAccept_SendsWinnerPushOnce_AndOneLoserPushPerRejectedId
    //   LegacyAccept_UnknownLoserBidder_IsSkipped_NotGuessed
    //   LegacyAccept_WhenPushClientThrows_StillReturns200
    //
    // The first already had an exact V1 twin. The other two did NOT — so rather than
    // delete their coverage with the route, they were PORTED onto the surviving V1
    // surface below (V1Accept_UnknownLoserBidder_IsSkipped_NotGuessed and
    // V1Accept_WhenPushClientThrows_StillReturns200). Both exercise the same invariants
    // against JeebOffersController's own DispatchAcceptLifecyclePushes /
    // ResolveLosingBidders, which is a separate copy of the fan-out from the deleted
    // one — so these are real assertions on live code, not ceremony.
    // ---------------------------------------------------------------------

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
                    JeeberId = "e3b8471d-0c26-4a95-b8f3-72d0a951c6e4",
                    RejectedOfferIds = new[] { "v1-loser-a" }
                }
            }
        };
        using var factory = NewFactory(fake, push);
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record("v1-offer-win", "v1-req", "e3b8471d-0c26-4a95-b8f3-72d0a951c6e4");
        index.Record("v1-loser-a", "v1-req", "v1-jeeber-loser-a");

        var resp = await ClientActor(factory, "v1-client").PostAsync("/v1/offers/v1-offer-win/accept", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await DrainDetachedPushesAsync(factory);

        push.Sends.Should().HaveCount(2);
        var winner = push.Sends.Single(s => TypeOf(s) == "offer_accepted");
        winner.UserId.Should().Be("e3b8471d-0c26-4a95-b8f3-72d0a951c6e4");
        var loser = push.Sends.Single(s => TypeOf(s) == "offer_lost");
        loser.UserId.Should().Be("v1-jeeber-loser-a");
        PayloadOf(loser)["offerId"].Should().Be("v1-loser-a");
    }

    /// <summary>
    /// PORTED 2026-08-01 from LegacyAccept_UnknownLoserBidder_IsSkipped_NotGuessed when the
    /// legacy accept route was retired. A rejected offer id the routing index cannot resolve
    /// to a bidder must be SKIPPED, never guessed — pushing "you lost" to the wrong jeeber is
    /// worse than staying silent.
    /// </summary>
    [Fact]
    public async Task V1Accept_UnknownLoserBidder_IsSkipped_NotGuessed()
    {
        var push = new RecordingUserPushClient();
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "v1-offer-win2",
                    JeeberId = "a1f0d63c-9b45-4e28-8d71-06c2f5b3a947",
                    // One resolvable loser, one NOT recorded in the index.
                    RejectedOfferIds = new[] { "v1-offer-known", "v1-offer-unknown" }
                }
            }
        };
        using var factory = NewFactory(fake, push);
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record("v1-offer-win2", "v1-req-2", "a1f0d63c-9b45-4e28-8d71-06c2f5b3a947");
        index.Record("v1-offer-known", "v1-req-2", "v1-jeeber-known");
        // v1-offer-unknown intentionally NOT recorded.

        var resp = await ClientActor(factory, "v1-client-owner2")
            .PostAsync("/v1/offers/v1-offer-win2/accept", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await DrainDetachedPushesAsync(factory);

        // Winner + only the resolvable loser (the unknown bidder is skipped, never guessed).
        push.Sends.Should().HaveCount(2);
        push.Sends.Count(s => TypeOf(s) == "offer_lost").Should().Be(1);
        push.Sends.Single(s => TypeOf(s) == "offer_lost").UserId.Should().Be("v1-jeeber-known");
    }

    /// <summary>
    /// PORTED 2026-08-01 from LegacyAccept_WhenPushClientThrows_StillReturns200 when the
    /// legacy accept route was retired. Degrade-don't-fail at the ROUTE level: the accept
    /// saga has already committed upstream, so a throwing push client must never flip the
    /// committed 200. (LifecyclePush_PushServiceFault_IsSwallowed_NeverThrows asserts the
    /// same invariant one layer down, on OfferPushNotifier; this one asserts it end-to-end
    /// through the controller, which is what the retired test covered.)
    /// </summary>
    [Fact]
    public async Task V1Accept_WhenPushClientThrows_StillReturns200()
    {
        var push = new RecordingUserPushClient { Throw = true };
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "v1-offer-boom",
                    JeeberId = "7f2c65b8-4a19-4d03-9e6c-b81f30d75a26",
                    RejectedOfferIds = new[] { "v1-offer-loser-boom" }
                }
            }
        };
        using var factory = NewFactory(fake, push);
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record("v1-offer-boom", "v1-req-boom", "7f2c65b8-4a19-4d03-9e6c-b81f30d75a26");
        index.Record("v1-offer-loser-boom", "v1-req-boom", "v1-jeeber-loser-boom");

        var resp = await ClientActor(factory, "v1-client-boom")
            .PostAsync("/v1/offers/v1-offer-boom/accept", null);

        // Degrade-don't-fail: a throwing push client must never flip the committed 200.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await DrainDetachedPushesAsync(factory);
        push.Attempts.Should().BeGreaterThanOrEqualTo(1);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static string TypeOf(SendRecord s) => (string)((IDictionary<string, object?>)s.Payload)["type"]!;
    private static IDictionary<string, object?> PayloadOf(SendRecord s) => (IDictionary<string, object?>)s.Payload;

    private static DeliveryRequest AcceptedRequest(decimal? acceptedFee) => new()
    {
        Id = "req-accepted",
        ClientId = "client-owner",
        Status = RequestStatus.Accepted,
        Description = "Parcel",
        PickupAddress = "Hamra",
        DropoffAddress = "Achrafieh",
        AcceptedFee = acceptedFee,
        AcceptedAt = DateTimeOffset.Parse("2026-07-26T11:12:13Z"),
        CreatedAt = DateTimeOffset.Parse("2026-07-26T10:11:12Z"),
    };

    private static NotificationRecordWriter NewConcreteWriter(
        CountingNotificationHandler handler)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1/"),
        };
        var client = new JeebNotificationRecordClient(http);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NotificationRecordWriter.EnabledConfigurationKey] = "true",
            })
            .Build();
        return new NotificationRecordWriter(
            client,
            configuration,
            NullLogger<NotificationRecordWriter>.Instance);
    }

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
                    // The accept fan-out is detached by design; keep the handle so tests can
                    // await the sends instead of racing the pool (EXEC-LEDGER S3.56 §2.4).
                    Fakes.AwaitableDetachedPushDispatcher.Use(services);
                });
            });

    /// <summary>Completes when every detached push dispatched so far has finished.</summary>
    private static Task DrainDetachedPushesAsync(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<Fakes.AwaitableDetachedPushDispatcher>().DispatchedWork;

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
        public Action? BeforeSend { get; init; }

        public override Task<SentPayloadResponse> Send_notification_to_userAsync(
            string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
        {
            Attempts++;
            BeforeSend?.Invoke();
            if (Throw)
            {
                throw new InvalidOperationException("push service unavailable");
            }
            Sends.Enqueue(new SendRecord(user_id, body.Payload));
            return Task.FromResult(new SentPayloadResponse { Message = "ok", Timestamp = DateTimeOffset.UtcNow });
        }
    }

    private sealed class RecordingNotificationRecordWriter : FakeNotificationRecordWriterBase
    {
        private readonly List<string>? _timeline;
        private readonly NotificationRecordWriteClassification _classification;

        public RecordingNotificationRecordWriter(
            List<string>? timeline = null,
            NotificationRecordWriteClassification classification =
                NotificationRecordWriteClassification.Committed)
        {
            _timeline = timeline;
            _classification = classification;
        }

        public List<OfferAcceptedNotificationRecord> Accepted { get; } = new();

        // WriteOfferReceivedAsync and the six step-6a writers are left to the base, which throws
        // if reached: this fake exists to count offer-ACCEPTED writes, so any other write arriving
        // here is the test lying about what the notifier did.
        public override Task<NotificationRecordWriteOutcome> WriteOfferAcceptedAsync(
            OfferAcceptedNotificationRecord record,
            CancellationToken requestToken)
        {
            _timeline?.Add("write");
            Accepted.Add(record);
            return Task.FromResult(new NotificationRecordWriteOutcome(_classification, 201));
        }
    }

    private sealed class CountingNotificationHandler : HttpMessageHandler
    {
        public int Posts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
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
    // OA-21 (51a2677) added the provider-audience reads to IDeliveryServiceClient. This double's
    // subject is elsewhere; an empty audience is the neutral answer, not a simulated fault.
    public Task<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>> ListAvailableProvidersAsync(
        double? lat, double? lng, double? radiusKm,
        IReadOnlyCollection<string>? roles, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.AvailableProviderUpstream>());

    public Task<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>> ListKnownProvidersAsync(
        System.DateTimeOffset since, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>());

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
