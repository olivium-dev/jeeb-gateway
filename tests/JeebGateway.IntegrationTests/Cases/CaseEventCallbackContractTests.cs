using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.Controllers;
using JeebGateway.Notifications;
using JeebGateway.Services.Clients;
using JeebGateway.service.ServiceNotification;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

/// <summary>
/// D12 (2026-08-16): the push leg of this controller moved from the DELETED in-gateway direct
/// dispatcher onto the notification-service hand-over rail, so every "push" expectation below
/// is now an <see cref="IGenericEventDispatcher"/> hand-over. The recipient derivation,
/// notification-centre envelope, loopback admission and deterministic-id contracts are
/// unchanged and are asserted exactly as before.
/// </summary>
public sealed class CaseEventCallbackContractTests
{
    [Fact]
    public async Task Canonical_State_Outbox_Envelope_Derives_Recipients_Then_Notifies_Before_Push()
    {
        const string payload = """
        {
          "eventId": "b86d460d-7b8e-4f2b-b46e-f4fbb595890f",
          "eventType": "case.message_added",
          "occurredAt": "2026-08-05T10:15:30Z",
          "case": {
            "caseId": "489660be-7844-42bc-a48f-f5c707b85b25",
            "kind": "dispute",
            "category": "damaged",
            "subject": { "type": "delivery", "ref": "delivery-1" },
            "requesterRef": "client-1",
            "participantRefs": ["client-1", "courier-1"],
            "status": "pending",
            "priority": "normal",
            "assigneeRef": null,
            "dueAt": null,
            "version": 4,
            "closedAt": null,
            "createdAt": "2026-08-05T09:00:00Z",
            "updatedAt": "2026-08-05T10:15:30Z"
          },
          "actor": { "ref": "admin-ops-1", "role": "admin" },
          "data": {
            "messageId": "f2f92a8d-5ad0-48ef-8a8e-72e510f497ec",
            "messageType": "reply"
          }
        }
        """;

        using var document = JsonDocument.Parse(payload);
        document.RootElement.EnumerateObject().Select(item => item.Name).Should().BeEquivalentTo(
            "eventId", "eventType", "occurredAt", "case", "actor", "data");
        document.RootElement.TryGetProperty("recipientUserIds", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("type", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("caseId", out _).Should().BeFalse();
        document.RootElement.GetProperty("case").GetProperty("participantRefs")
            .EnumerateArray().Select(item => item.GetString())
            .Should().Equal("client-1", "courier-1");

        var callback = JsonSerializer.Deserialize<GenericCaseCallbackV1>(payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        callback.Should().NotBeNull();

        var calls = new List<CapturedCall>();
        var controller = Controller(calls);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        controller.Request.Headers["X-Event-Id"] = callback!.EventId.ToString("D");

        var result = await controller.Dispatch(callback, default);

        result.Should().BeOfType<AcceptedResult>();
        calls.Select(item => item.Service).Should().Equal(
            "notification", "push", "notification", "push");
        calls.Select(item => item.Recipient).Should().Equal(
            "client-1", "client-1", "courier-1", "courier-1");
        calls[0].Body.Should().Contain("\"notification_type\":\"jeeb.dispute.reply\"");
        using (var notificationJson = JsonDocument.Parse(calls[0].Body))
        {
            var notificationId = Guid.Parse(
                notificationJson.RootElement.GetProperty("notification_id").GetString()!);
            notificationId.ToString("D")[14].Should().Be('4');
            notificationJson.RootElement.GetProperty("metadata").GetProperty("event_type")
                .GetString().Should().Be("jeeb.dispute.reply");
            notificationJson.RootElement.GetProperty("metadata").GetProperty("deep_link")
                .GetString().Should().Be("jeeb://disputes/489660be-7844-42bc-a48f-f5c707b85b25");
            notificationJson.RootElement.GetProperty("payload").GetProperty("caseId").GetString()
                .Should().Be("489660be-7844-42bc-a48f-f5c707b85b25");
            notificationJson.RootElement.GetProperty("payload").GetProperty("message_type")
                .GetString().Should().Be("jeeb.dispute.reply");
        }
        calls[3].Body.Should().Contain("\"recipientRole\":\"jeeber\"");
        calls[1].Body.Should().Contain("\"caseId\":\"489660be-7844-42bc-a48f-f5c707b85b25\"");
        using (var pushJson = JsonDocument.Parse(calls[1].Body))
        {
            // The hand-over entity id IS the notification id, so a producer that later
            // re-emits the same logical event dedupes on notification-service.
            pushJson.RootElement.GetProperty("idempotency_key").GetString()
                .Should().Be(JsonDocument.Parse(calls[0].Body).RootElement
                    .GetProperty("notification_id").GetString());
            pushJson.RootElement.GetProperty("payload").GetProperty("deepLink").GetString()
                .Should().Be("jeeb://disputes/489660be-7844-42bc-a48f-f5c707b85b25");
        }
    }

    [Fact]
    public async Task Dispute_Client_Actor_Is_Excluded_Courier_Still_Notified()
    {
        var calls = new List<CapturedCall>();
        var controller = Controller(calls);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(Callback(actorRef: "client-1", actorRole: "client"), default);

        result.Should().BeOfType<AcceptedResult>();
        calls.Select(call => call.Service).Should().Equal("notification", "push");
        calls.Select(call => call.Recipient).Should().Equal("courier-1", "courier-1");
    }

    [Fact]
    public async Task Dispute_Courier_Actor_Is_Excluded_Client_Still_Notified()
    {
        var calls = new List<CapturedCall>();
        var controller = Controller(calls);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(Callback(actorRef: "courier-1", actorRole: "jeeber"), default);

        result.Should().BeOfType<AcceptedResult>();
        calls.Select(call => call.Service).Should().Equal("notification", "push");
        calls.Select(call => call.Recipient).Should().Equal("client-1", "client-1");
    }

    [Fact]
    public async Task Dispute_Actor_Guid_Case_Skew_Is_Still_Excluded()
    {
        const string clientGuid = "0f8fad5b-d9cb-469f-a165-70867728950e";
        const string courierGuid = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
        var calls = new List<CapturedCall>();
        var controller = Controller(calls,
            deliveryHandler: new GuidPartyDeliveryHandler(clientGuid, courierGuid));
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(
            Callback(actorRef: courierGuid.ToUpperInvariant(), actorRole: "jeeber"), default);

        result.Should().BeOfType<AcceptedResult>();
        calls.Select(call => call.Service).Should().Equal("notification", "push");
        calls.Select(call => call.Recipient).Should().OnlyContain(r => r == clientGuid,
            "an uppercase D-format actor ref is the SAME user as the lowercase courier party id");
    }

    [Fact]
    public async Task NonDispute_Requester_Actor_Dispatches_Nothing_And_Does_Not_Throw()
    {
        var calls = new List<CapturedCall>();
        var controller = Controller(calls);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(
            SupportCallback(actorRef: "client-1", actorRole: "client"), default);

        result.Should().BeOfType<AcceptedResult>("zero recipients must degrade to a clean no-op accept");
        calls.Should().BeEmpty("the requester acting on their own case has no counterparty to notify");
    }

    [Fact]
    public async Task NonDispute_Admin_Reply_Still_Notifies_The_Requester()
    {
        var calls = new List<CapturedCall>();
        var controller = Controller(calls);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(
            SupportCallback(actorRef: "admin-ops-1", actorRole: "admin"), default);

        result.Should().BeOfType<AcceptedResult>();
        calls.Select(call => call.Service).Should().Equal("notification", "push");
        calls.Select(call => call.Recipient).Should().Equal("client-1", "client-1");
    }

    [Theory]
    [InlineData("/svc-callbacks/cases/events")]
    [InlineData("/v1/case-events")]
    public async Task Callback_Aliases_Reject_NonLoopback_Peers(string path)
    {
        var calls = new List<CapturedCall>();
        var controller = Controller(calls);
        controller.Request.Path = path;
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.2.44");

        var result = await controller.Dispatch(Callback(), default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/svc-callbacks/cases/events", "127.0.0.1")]
    [InlineData("/v1/case-events", "::1")]
    public async Task Callback_Aliases_Accept_Loopback_Peers_Without_Authentication(
        string path, string remoteIp)
    {
        var calls = new List<CapturedCall>();
        var controller = Controller(calls);
        controller.Request.Path = path;
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        (controller.User.Identity?.IsAuthenticated ?? false).Should().BeFalse();

        var result = await controller.Dispatch(Callback(), default);

        result.Should().BeOfType<AcceptedResult>();
        calls.Select(call => call.Service).Should().Equal(
            "notification", "push", "notification", "push");
    }

    [Fact]
    public async Task Callback_Retry_Propagates_Identical_Downstream_Dedupe_Identifiers()
    {
        var calls = new List<CapturedCall>();
        var controller = Controller(calls);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        var callback = Callback();

        await controller.Dispatch(callback, default);
        await controller.Dispatch(callback, default);

        var notifications = calls.Where(call => call.Service == "notification")
            .Select(call => JsonDocument.Parse(call.Body).RootElement
                .GetProperty("notification_id").GetString()).ToArray();
        notifications[0].Should().Be(notifications[2]);
        notifications[1].Should().Be(notifications[3]);
        var pushes = calls.Where(call => call.Service == "push")
            .Select(call => JsonDocument.Parse(call.Body).RootElement
                .GetProperty("idempotency_key").GetString()).ToArray();
        pushes[0].Should().Be(pushes[2]);
        pushes[1].Should().Be(pushes[3]);
        pushes.Should().Equal(notifications);
    }

    // D12: this used to assert 502 "remains retryable" on an undeterminable push outcome, and
    // that expectation IS the defect — an undeliverable notification retried a callback the
    // gateway had already processed, forever (MSI 15:15:45 / :49 / :57).
    [Fact]
    public async Task Unproven_Hand_Over_Is_Acknowledged_As_Degraded_Not_Retried()
    {
        var calls = new List<CapturedCall>();
        var controller = Controller(calls, classification: GenericEventDispatchClassification.Unproven);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(Callback(), default);

        result.Should().BeOfType<AcceptedResult>();
        calls.Select(call => call.Service).Should().Equal(
            "notification", "push", "notification", "push");
    }

    [Fact]
    public async Task Deduplicated_Hand_Over_Counts_As_Produced()
    {
        var calls = new List<CapturedCall>();
        var controller = Controller(calls, classification: GenericEventDispatchClassification.Deduplicated);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(Callback(), default);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        JsonSerializer.Serialize(accepted.Value).Should().Contain("\"degradedPushes\":0",
            "another producer already owns this notification id — that is the point of the key");
    }

    [Fact]
    public async Task Upstream_Timeout_Is_Retryable_When_Request_Was_Not_Cancelled()
    {
        var controller = Controller(new List<CapturedCall>(), notificationHandler: new TimeoutHandler());
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(Callback(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
    }

    [Fact]
    public void Controller_Has_One_Public_Constructor_For_Runtime_Activation()
    {
        typeof(CaseEventCallbacksController).GetConstructors().Should().ContainSingle();
    }

    [Fact]
    public void Deterministic_Notification_Id_Is_Rfc4122_Uuid4()
    {
        var eventId = Guid.Parse("b86d460d-7b8e-4f2b-b46e-f4fbb595890f");

        var first = CaseEventCallbacksController.DeterministicNotificationId(eventId, "client-1");
        var replay = CaseEventCallbacksController.DeterministicNotificationId(eventId, "client-1");

        first.Should().Be(replay);
        first.ToString("D")[14].Should().Be('4');
        first.ToString("D")[19].Should().BeOneOf('8', '9', 'a', 'b');
    }

    private static CaseEventCallbacksController Controller(
        List<CapturedCall> calls,
        HttpMessageHandler? deliveryHandler = null,
        HttpMessageHandler? notificationHandler = null,
        GenericEventDispatchClassification classification = GenericEventDispatchClassification.Accepted)
    {
        var delivery = new CaseDeliveryClient(new HttpClient(deliveryHandler ?? new DeliveryHandler())
            { BaseAddress = new Uri("https://delivery/") });
        return new CaseEventCallbacksController(delivery,
            new ServiceNotificationClient("https://notification/",
                new HttpClient(notificationHandler ?? new NotificationHandler(calls))),
            new RecordingHandoverDispatcher(calls, classification),
            NullLogger<CaseEventCallbacksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static GenericCaseCallbackV1 Callback(
        string actorRef = "admin-ops-1", string actorRole = "admin")
        => ParseCallback(kind: "dispute", actorRef, actorRole);

    private static GenericCaseCallbackV1 SupportCallback(string actorRef, string actorRole)
        => ParseCallback(kind: "support", actorRef, actorRole);

    private static GenericCaseCallbackV1 ParseCallback(string kind, string actorRef, string actorRole)
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
          "actor":{"ref":"{{actorRef}}","role":"{{actorRole}}"},
          "data":{"messageType":"reply"}
        }
        """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>
    /// Records each hand-over in the SAME ordered list as the notification-centre calls, so
    /// the "notify before push, per recipient" ordering stays assertable after the migration.
    /// </summary>
    private sealed class RecordingHandoverDispatcher(
        List<CapturedCall> calls, GenericEventDispatchClassification classification)
        : IGenericEventDispatcher
    {
        public Task<GenericEventDispatchOutcome> DispatchAsync(
            string eventType, string receiver, string entityId, string title, string body,
            IReadOnlyDictionary<string, string> data, string refreshCategory, CancellationToken ct)
        {
            calls.Add(new CapturedCall("push", receiver, JsonSerializer.Serialize(new
            {
                event_type = eventType,
                idempotency_key = entityId,
                category = refreshCategory,
                title,
                body,
                payload = data,
            })));
            return Task.FromResult(new GenericEventDispatchOutcome(classification, 201));
        }
    }

    private sealed class DeliveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri!.AbsolutePath.Should().Be("/deliveries/delivery-1/status-history");
            return Task.FromResult(Json(HttpStatusCode.OK, """
                {
                  "delivery_id":"delivery-1",
                  "party_ids":{"client_id":"client-1","courier_id":"courier-1"},
                  "current_status":"InTransit",
                  "status_history":[]
                }
                """));
        }
    }

    /// <summary>Delivery context whose party ids are lowercase D-format Guids (skew coverage).</summary>
    private sealed class GuidPartyDeliveryHandler(string clientId, string courierId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Json(HttpStatusCode.OK, $$"""
                {
                  "delivery_id":"delivery-1",
                  "party_ids":{"client_id":"{{clientId}}","courier_id":"{{courierId}}"},
                  "current_status":"InTransit",
                  "status_history":[]
                }
                """));
    }

    private sealed class NotificationHandler(List<CapturedCall> calls) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            var notificationId = Guid.Parse(
                json.RootElement.GetProperty("notification_id").GetString()!);
            notificationId.ToString("D")[14].Should().Be('4',
                "notification-service validates notification_id as UUID4");
            calls.Add(new CapturedCall("notification",
                json.RootElement.GetProperty("receiver").GetString()!, body));
            return Json(HttpStatusCode.OK, body);
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("upstream timeout"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed record CapturedCall(string Service, string Recipient, string Body);
}
