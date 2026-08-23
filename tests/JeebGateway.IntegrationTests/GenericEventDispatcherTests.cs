using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Bug D1 — the gateway hands every push kind to notification-service under ONE shared
/// idempotency key, so two in-gateway producers of the same event collapse to one dispatch.
/// </summary>
public sealed class GenericEventDispatcherTests
{
    private static readonly IReadOnlyDictionary<string, string> Routing =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "chat",
            ["requestId"] = "req-1",
        };

    [Fact]
    public async Task A_created_event_posts_once_to_the_generic_route()
    {
        var recorder = new RecordingHandler(HttpStatusCode.Created);
        var dispatcher = Dispatcher(recorder);

        var outcome = await Dispatch(dispatcher);

        outcome.Classification.Should().Be(GenericEventDispatchClassification.Accepted);
        recorder.Requests.Should().ContainSingle()
            .Which.Path.Should().EndWith("/notifications/events");
    }

    [Fact]
    public async Task A_409_is_a_dedupe_not_a_failure()
    {
        // notification-service answers 409 when the notification_id already belongs to another
        // command. That is the second producer losing the race, i.e. exactly the D1 fix working.
        var recorder = new RecordingHandler(HttpStatusCode.Conflict);
        var dispatcher = Dispatcher(recorder);

        var outcome = await Dispatch(dispatcher);

        outcome.Classification.Should().Be(GenericEventDispatchClassification.Deduplicated);
        outcome.UpstreamStatus.Should().Be(409);
    }

    [Fact]
    public async Task An_ambiguous_response_is_classified_by_read_back_not_by_a_second_post()
    {
        var recorder = new RecordingHandler(HttpStatusCode.InternalServerError)
        {
            ReadBackCorrelationId = ExpectedCorrelationId(),
        };
        var dispatcher = Dispatcher(recorder);

        var outcome = await Dispatch(dispatcher);

        outcome.Classification.Should()
            .Be(GenericEventDispatchClassification.AcceptedAfterAmbiguousResponse);
        recorder.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1,
            "a retry POST could create a second dispatch, which is the bug being fixed");
    }

    [Fact]
    public async Task An_unrecoverable_upstream_is_reported_unproven()
    {
        var recorder = new RecordingHandler(HttpStatusCode.InternalServerError);
        var dispatcher = Dispatcher(recorder);

        (await Dispatch(dispatcher)).Classification
            .Should().Be(GenericEventDispatchClassification.Unproven);
    }

    [Fact]
    public void Two_producers_of_one_event_mint_the_same_idempotency_key()
    {
        var fromNotifier = GenericEventDispatcher.BuildRecord(
            JeebGenericEventTypes.OfferLostEventType, "user-1", "irrelevant-a",
            "T", "B", Routing, PushSilencePolicy.CategoryOfferLost);
        var fromCallback = GenericEventDispatcher.BuildRecord(
            JeebGenericEventTypes.OfferLostEventType, "user-1", "irrelevant-a",
            "Different title", "Different body",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "other" },
            PushSilencePolicy.CategoryOfferLost);

        fromNotifier.NotificationCorrelationId.Should().Be(fromCallback.NotificationCorrelationId,
            "the key is derived from (eventType, receiver, entityId) only — copy and routing "
            + "differences between two producers must NOT split it into two dispatches");
    }

    [Fact]
    public void A_different_entity_gets_a_different_key()
    {
        // Guards the chat case specifically: keying on the thread instead of the message would
        // dedupe every message after the first into silence.
        var first = GenericEventDispatcher.BuildRecord(
            JeebGenericEventTypes.ChatMessageEventType, "user-1", "msg-1",
            "T", "B", Routing, PushSilencePolicy.CategoryChat);
        var second = GenericEventDispatcher.BuildRecord(
            JeebGenericEventTypes.ChatMessageEventType, "user-1", "msg-2",
            "T", "B", Routing, PushSilencePolicy.CategoryChat);

        first.NotificationCorrelationId.Should().NotBe(second.NotificationCorrelationId);
    }

    [Fact]
    public void The_silent_stamp_is_sourced_from_the_policy_and_no_live_category_is_silent()
    {
        // 2026-08-23 reversal: a silent new_request never rendered on-device, so the
        // fan-out record must carry its notification block and NO silent stamp.
        var newRequest = GenericEventDispatcher.BuildRecord(
            JeebGenericEventTypes.NewRequestEventType, "user-1", "req-1",
            "New delivery request", "Groceries • Small", Routing,
            PushSilencePolicy.CategoryNewRequest);
        var chat = GenericEventDispatcher.BuildRecord(
            JeebGenericEventTypes.ChatMessageEventType, "user-1", "msg-1",
            "T", "B", Routing, PushSilencePolicy.CategoryChat);

        newRequest.Data.Should().NotContainKey("silent",
            "the new-request fan-out must reach the notification shade");
        newRequest.Title.Should().Be("New delivery request");
        newRequest.Body.Should().Be("Groceries • Small");
        chat.Data.Should().NotContainKey("silent",
            "a human-addressed push must keep its shade entry");
    }

    [Fact]
    public void The_routing_block_preserves_the_producer_payload_and_carries_the_correlation_id()
    {
        var record = GenericEventDispatcher.BuildRecord(
            JeebGenericEventTypes.ChatMessageEventType, "user-1", "msg-1",
            "New message", "hello", Routing, PushSilencePolicy.CategoryChat);

        record.Data["type"].Should().Be("chat");
        record.Data["requestId"].Should().Be("req-1");
        record.Data["notification_id"].Should().Be(record.NotificationCorrelationId);
        record.Data["notificationId"].Should().Be(record.NotificationCorrelationId);
        record.EventType.Should().Be("jeeb.chat_message");
    }

    [Fact]
    public void The_four_uncovered_push_kinds_all_have_an_event_type()
        => new[]
        {
            JeebGenericEventTypes.ChatMessageEventType,
            JeebGenericEventTypes.NewRequestEventType,
            JeebGenericEventTypes.RequestExpiringEventType,
            JeebGenericEventTypes.OfferLostEventType,
        }.Should().OnlyHaveUniqueItems().And.OnlyContain(t => t.StartsWith("jeeb."));

    [Fact]
    public void No_generic_event_type_is_a_notification_centre_catalog_key()
        // The shared service must stay generic: these types have NO route there, and adding
        // them to the catalog would re-introduce Jeeb literals into it via the seeder.
        => JeebNotificationCatalog.Keys.Should().NotContain(new[]
        {
            JeebGenericEventTypes.ChatMessageEventType,
            JeebGenericEventTypes.NewRequestEventType,
            JeebGenericEventTypes.RequestExpiringEventType,
            JeebGenericEventTypes.OfferLostEventType,
        });

    [Fact]
    public async Task The_dispatcher_never_throws_on_a_transport_fault()
    {
        var dispatcher = Dispatcher(new ThrowingHandler());

        var outcome = await Dispatch(dispatcher);

        outcome.Classification.Should().Be(GenericEventDispatchClassification.Unproven);
    }

    [Fact]
    public async Task A_blank_receiver_or_entity_is_rejected_before_any_call()
    {
        var recorder = new RecordingHandler(HttpStatusCode.Created);
        var dispatcher = Dispatcher(recorder);

        var outcome = await dispatcher.DispatchAsync(
            JeebGenericEventTypes.ChatMessageEventType, receiver: " ", entityId: "msg-1",
            "T", "B", Routing, PushSilencePolicy.CategoryChat, default);

        outcome.Classification.Should().Be(GenericEventDispatchClassification.Unproven);
        recorder.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_handed_over_when_the_durable_write_flag_is_off()
    {
        // Deploy footgun made explicit: direct dispatch off AND durable write off means the
        // event has no producer at all. The dispatcher logs that and does not pretend to send.
        var recorder = new RecordingHandler(HttpStatusCode.Created);
        var dispatcher = Dispatcher(recorder, durableWriteEnabled: false);

        var outcome = await Dispatch(dispatcher);

        outcome.Classification.Should()
            .Be(GenericEventDispatchClassification.SkippedDirectDispatchArmed);
        recorder.Requests.Should().BeEmpty();
    }

    // ── harness ──────────────────────────────────────────────────────────────────────

    private static string ExpectedCorrelationId() => NotificationCorrelationId.Create(
        JeebGenericEventTypes.ChatMessageEventType, "user-1", "msg-1");

    private static Task<GenericEventDispatchOutcome> Dispatch(IGenericEventDispatcher dispatcher)
        => dispatcher.DispatchAsync(
            JeebGenericEventTypes.ChatMessageEventType, "user-1", "msg-1",
            "New message", "hello", Routing, PushSilencePolicy.CategoryChat, default);

    private static GenericEventDispatcher Dispatcher(
        HttpMessageHandler handler, bool durableWriteEnabled = true)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://notifications.test/") };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NotificationRecordWriter.EnabledConfigurationKey] =
                    durableWriteEnabled ? "true" : "false",
            })
            .Build();

        return new GenericEventDispatcher(
            new JeebNotificationRecordClient(http),
            configuration,
            NullLogger<GenericEventDispatcher>.Instance);
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _postStatus;

        public RecordingHandler(HttpStatusCode postStatus) => _postStatus = postStatus;

        public List<RecordedRequest> Requests { get; } = new();

        public string? ReadBackCorrelationId { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath));

            if (request.Method == HttpMethod.Get)
            {
                var rows = ReadBackCorrelationId is null
                    ? "[]"
                    : $"[{{\"notification_id\":\"{ReadBackCorrelationId}\"}}]";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"messages\":{rows}}}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(_postStatus)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("upstream down");
    }
}
