using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Notifications;
using JeebGateway.Observability;
using JeebGateway.Requests;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// BUILD-OFFER-PUSH — the offer-submitted → push-notification trigger. Two layers:
///   • unit tests on <see cref="OfferPushNotifier"/> itself against a recording
///     <see cref="ServicePushNotificationClient"/> subclass — recipient resolution
///     (the request's customer/clientId), the FLAT offer payload shape (type=offer,
///     category=delivery, requestId+request_id, offerId), and degrade-don't-fail; and
///   • an END-TO-END wiring test through the REAL submit pipeline
///     (<c>POST /requests/{id}/offers</c>) with the push client replaced by a recorder,
///     proving the controller fires exactly one push to the request's clientId with
///     type=offer + requestId, and that a throwing push client never breaks the 201.
/// </summary>
[Collection("FM1 notification durability telemetry")]
public class OfferPushNotifierTests
{
    private const string Client = "client-sami";
    private const string RequestId = "req-42";
    private const string OfferId = "offer-7";

    [Fact]
    public async Task NewOffer_NotifiesCustomer_WithFlatOfferPayload()
    {
        var push = new RecordingUserPushClient();
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        await notifier.NotifyNewOfferAsync(Client, RequestId, OfferId, fee: 12.5m, CancellationToken.None);

        push.Sends.Should().ContainSingle();
        var send = push.Sends.Single();
        send.UserId.Should().Be(Client, "the push goes to the request's customer (clientId)");

        var payload = (IDictionary<string, object?>)send.Payload;
        payload["title"].Should().Be("New offer on your request");
        payload["type"].Should().Be("offer");
        payload["category"].Should().Be("delivery");
        // Both id variants are carried flat so the mobile deep-link (routes /orders/:id from
        // delivery_id/order_id/requestId fallback) resolves regardless of which key it reads.
        payload["requestId"].Should().Be(RequestId);
        payload["request_id"].Should().Be(RequestId);
        payload["offerId"].Should().Be(OfferId);
        // Routing fields are flat top-level entries — no nested "data" object.
        payload.Should().NotContainKey("data");
        ((string)payload["body"]!).Should().Contain("12.5");
    }

    [Fact]
    public async Task PushServiceFault_IsSwallowed_NeverThrows()
    {
        var push = new RecordingUserPushClient { Throw = true };
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        // Degrade-don't-fail: a push blip must never surface to the offer-submit path.
        var act = async () => await notifier.NotifyNewOfferAsync(Client, RequestId, OfferId, 5m, CancellationToken.None);
        await act.Should().NotThrowAsync();
        push.Attempts.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GuardArmed503_IsLoggedAtDebug_NotWarnWithStack()
    {
        // GW-OFFER-503: since the D1 single-producer cut-over the direct-dispatch guard
        // synthesizes a 503 for EVERY offer push, and the generic catch logged it as
        // WARN + full stack once per offer. notification-service produces these pushes off
        // the durable record write, so the 503 is the expected steady state, not a fault.
        var log = new CapturingLogger<OfferPushNotifier>();
        var notifier = new OfferPushNotifier(GuardedPushClient(), log);

        await notifier.NotifyNewOfferAsync(Client, RequestId, OfferId, 5m, CancellationToken.None);

        log.Entries.Should().NotContain(e => e.Level == LogLevel.Warning,
            "the guard's expected 503 must not be journal noise");
        log.Has(LogLevel.Debug, "guard armed").Should().BeTrue();
    }

    [Fact]
    public async Task RealPushFailure_StillWarns_WhenItIsNotTheArmedGuard()
    {
        // The downgrade filter is narrow on purpose: a genuine push-service 503 (no guard
        // problem code) keeps the WARN + stack that operators alert on.
        var log = new CapturingLogger<OfferPushNotifier>();
        var push = new ServicePushNotificationClient(
            "http://push.test/",
            new HttpClient(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"code\":\"upstream_unavailable\"}"),
            }))
            {
                BaseAddress = new Uri("http://push.test/"),
            });

        await new OfferPushNotifier(push, log)
            .NotifyNewOfferAsync(Client, RequestId, OfferId, 5m, CancellationToken.None);

        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning,
            "a real push failure is still a warning");
    }

    /// <summary>
    /// A push client behind the REAL <see cref="JeebGateway.Services.Clients.GatewayDirectPushDispatchGuardHandler"/>
    /// in its committed (disabled) state, so the test sees the exact 503 + problem code the live
    /// gateway sees rather than a hand-forged exception.
    /// </summary>
    private static ServicePushNotificationClient GuardedPushClient()
    {
        var guard = new JeebGateway.Services.Clients.GatewayDirectPushDispatchGuardHandler
        {
            InnerHandler = new StaticResponseHandler(
                () => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { message = "ok" }),
                }),
        };

        return new ServicePushNotificationClient(
            "http://push.test/",
            new HttpClient(guard) { BaseAddress = new Uri("http://push.test/") });
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _response;

        public StaticResponseHandler(Func<HttpResponseMessage> response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = _response();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task MissingClientId_PushesNothing()
    {
        var push = new RecordingUserPushClient();
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        await notifier.NotifyNewOfferAsync(clientId: " ", RequestId, OfferId, 5m, CancellationToken.None);

        push.Sends.Should().BeEmpty();
        push.Attempts.Should().Be(0);
    }

    /// <summary>
    /// Was …BeforePush. The durable write no longer PRECEDES a push, it REPLACES it: the same
    /// POST notification-service answers is what produces the card (2026-08-14 duplicate).
    /// </summary>
    [Fact]
    public async Task AC3_AC8b_AC8c_AC8d_DurableWriteUsesEtaAndAbsentClientName_AndOwnsThePush()
    {
        long fieldAbsentCount = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == BusinessOutcomeTelemetry.MeterName &&
                instrument.Name == "notif.durable_write.field_absent")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, state) =>
            {
                var isClientName = false;
                var isOfferReceived = false;
                foreach (var tag in tags)
                {
                    isClientName |= tag.Key == "field" &&
                        Equals(tag.Value, "client_name");
                    isOfferReceived |= tag.Key == "templateKey" &&
                        Equals(tag.Value, OfferReceivedNotificationRecord.TemplateKey);
                }
                if (isClientName && isOfferReceived)
                {
                    Interlocked.Add(ref fieldAbsentCount, measurement);
                }
            });
        meterListener.Start();

        var timeline = new List<string>();
        var writer = new RecordingNotificationRecordWriter(timeline);
        var push = new RecordingUserPushClient { BeforeSend = () => timeline.Add("push") };
        var notifier = new OfferPushNotifier(
            push,
            writer,
            (_, _) => Task.FromResult<DeliveryRequest?>(null),
            NullLogger<OfferPushNotifier>.Instance);
        var context = new OfferReceivedNotificationContext(
            "Hamra, Beirut",
            "Achrafieh, Beirut",
            30,
            DateTimeOffset.Parse("2026-07-26T10:11:12Z"));

        await notifier.NotifyNewOfferAsync(
            context,
            Client,
            RequestId,
            OfferId,
            fee: 12.5m,
            CancellationToken.None);

        timeline.Should().Equal("write");
        push.Sends.Should().BeEmpty(
            "notification-service produces this push off the same POST — a direct send here is "
            + "the second card measured on hardware 2026-08-14");
        var record = writer.Received.Should().ContainSingle().Subject;
        record.Payload.OfferAmount.Should().Be(12.5m);
        record.Payload.DeliveryFee.Should().Be(12.5m);
        record.Payload.EstimatedDuration.Should().Be("30");
        record.Payload.ClientName.Should().BeEmpty();
        record.Payload.PickupLocation.Should().Be("Hamra, Beirut");
        record.Payload.DeliveryLocation.Should().Be("Achrafieh, Beirut");
        record.Payload.RequestId.Should().Be(RequestId,
            "the only offer-review screen is keyed by request id, so a typed offer push "
            + "without it cannot deep-link (measured on device, G9/T3b)");
        fieldAbsentCount.Should().Be(1);

        // Copy + correlation parity is only observable where the centre declines to produce and
        // the direct client is the sole producer left.
        var fallback = new RecordingUserPushClient();
        await new OfferPushNotifier(
            fallback,
            new RecordingNotificationRecordWriter(
                classification: NotificationRecordWriteClassification.Disabled),
            (_, _) => Task.FromResult<DeliveryRequest?>(null),
            NullLogger<OfferPushNotifier>.Instance)
            .NotifyNewOfferAsync(context, Client, RequestId, OfferId, 12.5m, CancellationToken.None);

        var payload = (IDictionary<string, object?>)fallback.Sends.Single().Payload;
        payload["notificationId"].Should().Be(record.NotificationCorrelationId);
        payload["notification_id"].Should().Be(record.NotificationCorrelationId);
        payload["title"].Should().Be(record.Title);
        payload["body"].Should().Be(record.Description);
    }

    [Fact]
    public async Task AC2a_AC2b_NewOffer_ThrowingDurableWriter_StillPushesAndLogsOnceWithNcid()
    {
        var writer = new RecordingNotificationRecordWriter(throwOnWrite: true);
        var push = new RecordingUserPushClient();
        var logger = new RecordingLogger<OfferPushNotifier>();
        var notifier = new OfferPushNotifier(
            push,
            writer,
            (_, _) => Task.FromResult<DeliveryRequest?>(null),
            logger);
        var context = new OfferReceivedNotificationContext(
            "A",
            "B",
            10,
            DateTimeOffset.Parse("2026-07-26T10:11:12Z"));

        var act = async () => await notifier.NotifyNewOfferAsync(
            context,
            Client,
            RequestId,
            OfferId,
            5m,
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        writer.Attempts.Should().Be(1);
        push.Sends.Should().ContainSingle();
        var pushPayload = (IDictionary<string, object?>)push.Sends.Single().Payload;
        var error = logger.Entries.Should()
            .ContainSingle(entry => entry.Level == LogLevel.Error)
            .Subject;
        error.Properties["event"].Should().Be("notif.durable_write.failed");
        error.Properties["ncid"].Should().Be(pushPayload["notificationId"]);
    }

    // ---------------------------------------------------------------------
    // E2E wiring — the REAL POST /requests/{id}/offers pipeline calls the notifier.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Submit_TriggersExactlyOnePush_ToCustomer_WithOfferType_AndRequestId()
    {
        var push = new RecordingUserPushClient();
        using var factory = NewFactory(push);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeber = JeeberClient(factory, $"jeeber-{Guid.NewGuid()}");

        var resp = await jeeber.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 9m, etaMinutes = 20, note = "On my way" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<OfferDto>())!;

        push.Sends.Should().ContainSingle("exactly one offer push per accepted submission");
        var send = push.Sends.Single();
        send.UserId.Should().Be(clientId, "the customer (request owner) is notified, not the offering jeeber");

        var payload = (IDictionary<string, object?>)send.Payload;
        payload["type"].Should().Be("offer");
        payload["category"].Should().Be("delivery");
        payload["requestId"].Should().Be(requestId);
        payload["request_id"].Should().Be(requestId);
        payload["offerId"].Should().Be(dto.Id);
    }

    [Fact]
    public async Task Submit_WhenPushClientThrows_StillReturns201()
    {
        var push = new RecordingUserPushClient { Throw = true };
        using var factory = NewFactory(push);

        var (_, requestId) = await SeedRequestAsync(factory);
        var jeeber = JeeberClient(factory, $"jeeber-{Guid.NewGuid()}");

        var resp = await jeeber.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 6m, etaMinutes = 15 });

        // Degrade-don't-fail end-to-end: a throwing push service does not flip the 201.
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        push.Attempts.Should().BeGreaterThanOrEqualTo(1);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(RecordingUserPushClient push)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Replace the deployed :10040 push client with the recorder so no real
                    // network call happens and the emitted payload/recipient are asserted.
                    services.RemoveAll<ServicePushNotificationClient>();
                    services.AddSingleton<ServicePushNotificationClient>(push);

                    // GW3 / W3.5(c): the gateway ships no in-memory offer store any more, so a test that
                    // needs a working offer ledger registers the test-owned double itself.
                    Fakes.FakeOfferStoreWebApplicationFactory.UseFakeOfferStore(services);
                });
            });

    private static async Task<(string clientId, string requestId)> SeedRequestAsync(
        WebApplicationFactory<Program> factory)
    {
        var clientId = $"client-{Guid.NewGuid()}";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up a package",
            // D2: the offer range guard needs a resolvable tier + pickup point.
            TierId = Fakes.InRangeGeoFixture.TierId,
            PickupLocation = new GeoPoint
            {
                Lat = Fakes.InRangeGeoFixture.Lat,
                Lng = Fakes.InRangeGeoFixture.Lng,
            },
        }, default);
        return (clientId, created.Id);
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver"); // → contract jeeber
        return c;
    }

    private sealed record SendRecord(string UserId, object Payload);

    /// <summary>Recording stand-in for the deployed push client; overrides the single
    /// send-to-user seam both notifiers use. The base ctor needs a base URL + HttpClient.</summary>
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

    [Fact]
    public void TypedOfferPayloadsSerializeTheOwningRequestIdForTheDeepLink()
    {
        var received = JsonSerializer.SerializeToNode(new OfferReceivedNotificationPayload
        {
            UserId = Client,
            OfferId = OfferId,
            RequestId = RequestId,
            OfferAmount = 7m,
            DeliveryFee = 7m,
            EstimatedDuration = "30",
            CreatedAt = DateTimeOffset.UnixEpoch,
        })!.AsObject();
        var accepted = JsonSerializer.SerializeToNode(new OfferAcceptedNotificationPayload
        {
            UserId = Client,
            OfferId = OfferId,
            RequestId = RequestId,
            AcceptedAmount = 7m,
            JeeberId = "jeeber-1",
            CreatedAt = DateTimeOffset.UnixEpoch,
        })!.AsObject();

        foreach (var payload in new[] { received, accepted })
        {
            payload.Should().ContainKey("request_id");
            payload["request_id"]!.GetValue<string>().Should().Be(RequestId);
            payload.Should().ContainKey("offer_id");
        }
    }

    private sealed class RecordingNotificationRecordWriter : FakeNotificationRecordWriterBase
    {
        private readonly List<string>? _timeline;
        private readonly bool _throwOnWrite;
        private readonly NotificationRecordWriteClassification _classification;

        public RecordingNotificationRecordWriter(
            List<string>? timeline = null,
            bool throwOnWrite = false,
            NotificationRecordWriteClassification classification =
                NotificationRecordWriteClassification.Committed)
        {
            _timeline = timeline;
            _throwOnWrite = throwOnWrite;
            _classification = classification;
        }

        public int Attempts { get; private set; }
        public List<OfferReceivedNotificationRecord> Received { get; } = new();

        public override Task<NotificationRecordWriteOutcome> WriteOfferReceivedAsync(
            OfferReceivedNotificationRecord record,
            CancellationToken requestToken)
        {
            Attempts++;
            _timeline?.Add("write");
            if (_throwOnWrite)
            {
                throw new InvalidOperationException("writer fault");
            }
            Received.Add(record);
            return Task.FromResult(new NotificationRecordWriteOutcome(_classification, 201));
        }

        // WriteOfferAcceptedAsync and the six step-6a writers stay on the base, which throws if
        // reached — this fake counts offer-RECEIVED writes only.
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>>
                ?? Array.Empty<KeyValuePair<string, object?>>();
            Entries.Add(new LogEntry(
                logLevel,
                properties.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        IReadOnlyDictionary<string, object?> Properties);
}
