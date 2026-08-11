using System;
using System.Collections.Concurrent;
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
using JeebGateway.Services.Clients;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// The 2026-08-11 live regression, pinned.
///
/// <para><b>What actually broke.</b> PR #374's D1 single-producer cut-over armed
/// <c>GatewayDirectPushDispatchGuardHandler</c>, which synthesizes a 503
/// <c>gateway_direct_push_dispatch_disabled</c> for every <c>POST /api/v1/sent-payload/*</c>
/// while <c>PushNotificationServiceApi:GatewayDirectDispatch:Enabled</c> is false — the
/// committed and live default. Chat, offer-lost, new-request and expiry were all migrated to
/// the <see cref="IGenericEventDispatcher"/> hand-over in the same change.
/// <see cref="DeliveryStatusPushNotifier"/> was not, so the delivery-status category had NO
/// producer at all: the live gateway journal shows all four transitions of delivery
/// <c>da210b2f</c> (accepted→Picked 00:32:21, Picked→InTransit 00:32:52,
/// InTransit→AtDoor 00:34:59, AtDoor→Done 00:35:24) logging
/// "Delivery-status push … failed; the transition stands" against that same 503, and ZERO
/// <c>notif.generic_event.dispatched</c> lines for <c>jeeb.delivery_status_updated</c>.</para>
///
/// <para><b>Why the flag is not the fix.</b> Re-arming direct dispatch would make the
/// gateway a second producer for every category that WAS migrated, recreating the D1
/// duplicate-push defect. The producer has to move, which is what these tests hold in place.</para>
///
/// <para>Host-free: the real <see cref="GenericEventDispatcher"/> over a recording HTTP
/// handler, plus a recording push client, so both producer legs are observable at once.</para>
/// </summary>
public class DeliveryStatusGenericEventHandoverTests
{
    private const string Client = "1df7a825-62e4-4f9a-ba47-938fb4849926";
    private const string Jeeber = "b4c26077-0985-40a1-b799-ec001bc9ad10";
    private const string DeliveryId = "da210b2f-cbde-41ba-afe2-fa3c17c9ffd4";

    [Fact]
    public async Task Direct_dispatch_disabled_hands_the_transition_over_and_sends_nothing_directly()
    {
        var (notifier, events, push) = Notifier(directDispatchEnabled: false);

        await notifier.NotifyAsync(Transition("InTransit", "AtDoor"), CancellationToken.None);

        push.Sends.Should().BeEmpty(
            "the guard 503s every /api/v1/sent-payload/* POST while direct dispatch is off — "
            + "this is exactly the leg that failed live on 2026-08-11");
        events.Posts.Should().HaveCount(2, "one hand-over per recipient (client + jeeber)");

        var toClient = events.Posts.Single(p => p.GetProperty("receiver").GetString() == Client);
        toClient.GetProperty("event_type").GetString()
            .Should().Be(DeliveryStatusUpdatedNotificationRecord.TemplateKey);

        var data = Data(toClient);
        data["type"].Should().Be("delivery",
            "without it NotificationCategory.fromData resolves `other` and mobile drops the message");
        data["category"].Should().Be("delivery");
        data["delivery_id"].Should().Be(DeliveryId,
            "the mobile id-guarded refresh branch reads delivery_id|order_id|requestId|request_id");
        data["request_id"].Should().Be(DeliveryId);
        data["status"].Should().Be("AtDoor");
        data["previous_status"].Should().Be("InTransit");
    }

    [Fact]
    public async Task Two_transitions_of_one_delivery_mint_two_distinct_notification_ids()
    {
        // THE DEDUP TRAP. The correlation id is a deterministic hash of
        // (eventType, receiver, entityId) and the centre upserts $setOnInsert on it, so an
        // entityId of DeliveryId alone would collapse Picked/InTransit/AtDoor/Done into ONE push.
        var (notifier, events, _) = Notifier(directDispatchEnabled: false);

        await notifier.NotifyAsync(Transition("accepted", "Picked"), CancellationToken.None);
        await notifier.NotifyAsync(Transition("Picked", "InTransit"), CancellationToken.None);

        var idsForClient = events.Posts
            .Where(p => p.GetProperty("receiver").GetString() == Client)
            .Select(p => p.GetProperty("notification_id").GetString())
            .ToArray();

        idsForClient.Should().HaveCount(2).And.OnlyHaveUniqueItems(
            "a second transition of the same delivery must not dedupe into the first");
    }

    [Fact]
    public async Task Each_recipient_of_one_transition_gets_its_own_notification_id()
    {
        var (notifier, events, _) = Notifier(directDispatchEnabled: false);

        await notifier.NotifyAsync(Transition("InTransit", "AtDoor"), CancellationToken.None);

        events.Posts
            .Select(p => p.GetProperty("notification_id").GetString())
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Direct_dispatch_rearmed_falls_back_to_the_push_client_and_hands_nothing_over()
    {
        var (notifier, events, push) = Notifier(directDispatchEnabled: true);

        await notifier.NotifyAsync(Transition("InTransit", "AtDoor"), CancellationToken.None);

        events.Posts.Should().BeEmpty();
        push.Sends.Select(s => s.UserId).Should().Equal(Client, Jeeber);
        var payload = (IDictionary<string, object?>)push.Sends.First().Payload;
        payload["delivery_id"].Should().Be(DeliveryId, "BuildPayload stays the direct wire contract");
    }

    [Fact]
    public async Task The_handed_over_event_carries_no_silent_key()
    {
        // delivery is ShadeAndStored (owner reversal 2026-07-27): the shade entry must post.
        var (notifier, events, _) = Notifier(directDispatchEnabled: false);

        await notifier.NotifyAsync(Transition("InTransit", "AtDoor"), CancellationToken.None);

        Data(events.Posts.First()).Should().NotContainKey("silent");
    }

    [Fact]
    public async Task A_dead_notification_centre_never_throws_and_never_double_sends()
    {
        var (notifier, events, push) = Notifier(directDispatchEnabled: false, postStatus: HttpStatusCode.InternalServerError);

        var notify = async () => await notifier.NotifyAsync(Transition("InTransit", "AtDoor"), CancellationToken.None);

        await notify.Should().NotThrowAsync("the transition already committed — degrade, don't fail");
        events.Posts.Should().HaveCount(2);
        push.Sends.Should().BeEmpty(
            "an Unproven hand-over must NOT fall through to the direct client: that is the "
            + "double-producer path, and the guard would 503 it anyway");
    }

    // ── harness ──────────────────────────────────────────────────────────────────────

    private static DeliveryStatusPushNotification Transition(string previous, string status)
        => new(
            DeliveryId: DeliveryId,
            RequestId: DeliveryId,
            PreviousStatus: previous,
            Status: status,
            Recipients: new[] { Client, Jeeber },
            Title: "Delivery status updated",
            Body: $"Status changed from {previous} to {status}.",
            GpsTrackingActive: true);

    private static Dictionary<string, string> Data(JsonElement post)
        => post.GetProperty("data").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);

    private static (DeliveryStatusPushNotifier, RecordingCentreHandler, RecordingPushClient) Notifier(
        bool directDispatchEnabled,
        HttpStatusCode postStatus = HttpStatusCode.Created)
    {
        var centre = new RecordingCentreHandler(postStatus);
        var http = new HttpClient(centre) { BaseAddress = new Uri("http://notifications.test/") };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NotificationRecordWriter.EnabledConfigurationKey] = "true",
            })
            .Build();

        var dispatcher = new GenericEventDispatcher(
            new JeebNotificationRecordClient(http),
            Options.Create(new GatewayDirectPushDispatchOptions { Enabled = directDispatchEnabled }),
            configuration,
            NullLogger<GenericEventDispatcher>.Instance);

        var push = new RecordingPushClient();
        return (
            new DeliveryStatusPushNotifier(push, dispatcher, NullLogger<DeliveryStatusPushNotifier>.Instance),
            centre,
            push);
    }

    private sealed class RecordingCentreHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _postStatus;

        public RecordingCentreHandler(HttpStatusCode postStatus) => _postStatus = postStatus;

        public List<JsonElement> Posts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"messages\":[]}", Encoding.UTF8, "application/json"),
                };
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Posts.Add(JsonDocument.Parse(body).RootElement.Clone());

            return new HttpResponseMessage(_postStatus)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record SentPush(string UserId, object Payload);

    private sealed class RecordingPushClient : ServicePushNotificationClient
    {
        public RecordingPushClient() : base("http://localhost", new HttpClient()) { }

        public ConcurrentQueue<SentPush> Sends { get; } = new();

        public override Task<SentPayloadResponse> Send_notification_to_userAsync(
            string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
        {
            Sends.Enqueue(new SentPush(user_id, body.Payload));
            return Task.FromResult(new SentPayloadResponse
            {
                Message = "ok",
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }
}
