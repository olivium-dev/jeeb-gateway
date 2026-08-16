using System.Net;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.Controllers;
using JeebGateway.Notifications;
using JeebGateway.Services.Clients;
using JeebGateway.service.ServiceNotification;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JeebGateway.UnitTests;

/// <summary>
/// D12 (Phase V run 2, 2026-08-16): a dispute raised from the real client UI was created
/// (POST /cases → 201) and then produced NO notification, in a hot retry loop.
///
/// <para>Gateway journal, verbatim:</para>
/// <code>
/// fail: JeebGateway.Controllers.CaseEventCallbacksController[0]
///   event=case.callback_failed case_id=7ecd9aa9-6e71-4bc1-a0f9-d44cff6ce2ee ...
///   System.InvalidOperationException: Push outcome for case event 0da6beb3-... and
///     recipient fedb6e3b-... is undeterminable.
///    ---> System.AggregateException: (The HTTP status code of the response was not
///         expected (503). {"code":"gateway_direct_push_dispatch_disabled","detail":
///         "Gateway direct push dispatch is disabled; notification-service is the sole
///         push producer."}) (push-notification recovery call failed with 404.)
/// </code>
///
/// <para>The case callback path was the last consumer still calling the in-gateway direct
/// push dispatcher, which PR #374's single-producer cut-over deliberately turned into a
/// synthetic 503. Its recovery read-back then 404s, so the controller could not classify the
/// outcome, threw, answered 502, and the state-service outbox re-delivered the same event —
/// observed repeating at 15:15:45, 15:15:49 and 15:15:57.</para>
///
/// <para>FALSIFICATION from the same run: the same process minutes earlier dispatched four
/// delivery-status notifications through notif.generic_event.dispatched with
/// upstreamStatus=201 and they arrived on the device. The hand-over rail works; only this
/// controller was left on the removed one.</para>
///
/// <para>The controller is built through <see cref="ActivatorUtilities"/> on purpose, so this
/// file is byte-identical before and after the fix even though the constructor changes — the
/// RED run and the GREEN run assert exactly the same things.</para>
/// </summary>
public sealed class D12CaseCallbackPushHandoverTests
{
    private const string Client = "client-1";
    private const string Courier = "courier-1";

    [Fact]
    public async Task A_dispute_callback_hands_every_recipient_to_notification_service()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Accepted);
        var controller = Build(events, out var directPush, out _);

        var result = await controller.Dispatch(Callback(kind: "dispute"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        directPush.Attempts.Should().Be(0,
            "the gateway is not a push producer any more; the direct dispatcher answers 503 "
            + "gateway_direct_push_dispatch_disabled by design");
        events.Calls.Select(call => call.Receiver).Should().Equal(Client, Courier);
        events.Calls.Should().OnlyContain(call =>
            call.EventType == JeebGenericEventTypes.DisputeUpdateEventType
            && call.Category == PushSilencePolicy.CategoryDispute);
        events.Calls[0].Data["case_id"].Should().Be("489660be-7844-42bc-a48f-f5c707b85b25");
        events.Calls[0].Data["type"].Should().Be("dispute");
        events.Calls[0].Data["deep_link"].Should()
            .Be("jeeb://disputes/489660be-7844-42bc-a48f-f5c707b85b25");
    }

    [Fact]
    public async Task A_support_callback_uses_the_support_event_type_and_category()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Accepted);
        var controller = Build(events, out _, out _);

        var result = await controller.Dispatch(
            Callback(kind: "support", actorRef: "admin-ops-1"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        events.Calls.Select(call => call.Receiver).Should().Equal(Client);
        events.Calls[0].EventType.Should().Be(JeebGenericEventTypes.SupportCaseUpdateEventType);
        events.Calls[0].Category.Should().Be(PushSilencePolicy.CategorySupport);
        events.Calls[0].Data["deep_link"].Should()
            .Be("jeeb://support/tickets/489660be-7844-42bc-a48f-f5c707b85b25");
    }

    // THE DEFECT ITSELF. A notification that cannot be delivered must not turn a callback
    // the gateway already processed into an infinite outbox retry.
    [Fact]
    public async Task An_undeliverable_notification_does_not_make_the_callback_retryable()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Unproven);
        var controller = Build(events, out _, out _);

        var result = await controller.Dispatch(Callback(kind: "dispute"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>(
            "the case row and the notification-centre record are already committed; a 502 here "
            + "is what produced the 15:15:45 / :49 / :57 retry storm");
        events.Calls.Should().HaveCount(2, "each recipient is still attempted");
    }

    [Fact]
    public async Task A_throwing_hand_over_does_not_make_the_callback_retryable_either()
    {
        var events = new ScriptedEventDispatcher(new InvalidOperationException("centre down"));
        var controller = Build(events, out _, out _);

        var result = await controller.Dispatch(Callback(kind: "dispute"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    // CONTROL, and one that must be capable of a different answer: the fix must not have
    // blanket-swallowed every failure. A notification-CENTRE fault is genuinely retryable
    // (nothing durable was written for it) and must still surface as 502.
    [Fact]
    public async Task A_notification_centre_fault_is_still_retryable()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Accepted);
        var controller = Build(events, out _, out _, notificationStatus: HttpStatusCode.InternalServerError);

        var result = await controller.Dispatch(Callback(kind: "dispute"), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
    }

    [Fact]
    public async Task A_replayed_callback_reuses_the_same_hand_over_identity()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Accepted);
        var controller = Build(events, out _, out _);
        var callback = Callback(kind: "dispute");

        await controller.Dispatch(callback, CancellationToken.None);
        await controller.Dispatch(callback, CancellationToken.None);

        events.Calls.Should().HaveCount(4);
        events.Calls[0].EntityId.Should().Be(events.Calls[2].EntityId);
        events.Calls[1].EntityId.Should().Be(events.Calls[3].EntityId);
        events.Calls[0].EntityId.Should().NotBe(events.Calls[1].EntityId);
    }

    // ---------------------------------------------------------------------
    // fixture
    // ---------------------------------------------------------------------

    private static CaseEventCallbacksController Build(
        IGenericEventDispatcher events,
        out DisabledDirectPushClient directPush,
        out NotFoundPushRecovery recovery,
        HttpStatusCode notificationStatus = HttpStatusCode.OK)
    {
        var delivery = Substitute.For<ICaseDeliveryClient>();
        delivery.GetDeliveryCaseContextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeliveryCaseContextUpstream?>(new DeliveryCaseContextUpstream
            {
                DeliveryId = "delivery-1",
                PartyIds = new DeliveryCasePartyIdsUpstream { ClientId = Client, CourierId = Courier },
                CurrentStatus = "InTransit",
            }));

        directPush = new DisabledDirectPushClient();
        recovery = new NotFoundPushRecovery();

        var services = new ServiceCollection();
        services.AddSingleton(delivery);
        services.AddSingleton(new ServiceNotificationClient(
            "https://notification/", new HttpClient(new StubHandler(notificationStatus))));
        services.AddSingleton<ServicePushNotificationClient>(directPush);
        services.AddSingleton<IPushDispatchRecoveryClient>(recovery);
        services.AddSingleton(events);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<CaseEventCallbacksController>>(
            NullLogger<CaseEventCallbacksController>.Instance);

        var controller = ActivatorUtilities.CreateInstance<CaseEventCallbacksController>(
            services.BuildServiceProvider());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        return controller;
    }

    private static GenericCaseCallbackV1 Callback(string kind, string actorRef = "admin-ops-1")
        => JsonSerializer.Deserialize<GenericCaseCallbackV1>($$"""
        {
          "eventId":"b86d460d-7b8e-4f2b-b46e-f4fbb595890f",
          "eventType":"case.message_added",
          "occurredAt":"2026-08-05T10:15:30Z",
          "case":{"caseId":"489660be-7844-42bc-a48f-f5c707b85b25","kind":"{{kind}}",
            "category":"damaged","subject":{"type":"delivery","ref":"delivery-1"},
            "requesterRef":"client-1","participantRefs":["client-1","courier-1"],
            "status":"pending","priority":"normal","version":4,
            "createdAt":"2026-08-05T09:00:00Z","updatedAt":"2026-08-05T10:15:30Z"},
          "actor":{"ref":"{{actorRef}}","role":"admin"},
          "data":{"messageType":"reply"}
        }
        """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>The live GatewayDirectPushDispatchGuardHandler behaviour, verbatim.</summary>
    internal sealed class DisabledDirectPushClient : ServicePushNotificationClient
    {
        public DisabledDirectPushClient() : base("https://push/", new HttpClient()) { }

        public int Attempts { get; private set; }

        public override Task<SentPayloadResponse> Send_notification_to_userAsync(
            string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new HttpRequestException(
                "The HTTP status code of the response was not expected (503). "
                + "{\"code\":\"gateway_direct_push_dispatch_disabled\",\"detail\":\"Gateway direct "
                + "push dispatch is disabled; notification-service is the sole push producer.\"}");
        }
    }

    /// <summary>The live recovery read-back: push-notification answers 404 for that key.</summary>
    internal sealed class NotFoundPushRecovery : IPushDispatchRecoveryClient
    {
        public List<string> QueriedKeys { get; } = new();

        public Task<PushDispatchStatusV1> GetAsync(
            string idempotencyKey, int staleAfterSeconds, CancellationToken ct)
        {
            QueriedKeys.Add(idempotencyKey);
            throw new PushDispatchRecoveryApiException(404);
        }

        public Task<PushDispatchListV1> ListStaleAsync(int olderThanSeconds, int limit, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<PushDispatchStatusV1> ResolveAsync(
            string idempotencyKey, PushDispatchResolutionV1 request, CancellationToken ct)
            => throw new NotSupportedException();
    }

    // Echoes the request body on 2xx, exactly as notification-service does.
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    status == HttpStatusCode.OK ? body : "{\"detail\":\"centre down\"}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
