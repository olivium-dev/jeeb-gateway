using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.Controllers;
using JeebGateway.Services.Clients;
using JeebGateway.service.ServiceNotification;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

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
          "actor": { "ref": "client-1", "role": "client" },
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
        var delivery = new CaseDeliveryClient(new HttpClient(new DeliveryHandler())
            { BaseAddress = new Uri("https://delivery/") });
        var notification = new ServiceNotificationClient("https://notification/",
            new HttpClient(new NotificationHandler(calls)));
        var push = new ServicePushNotificationClient("https://push/",
            new HttpClient(new PushHandler(calls)));
        var controller = new CaseEventCallbacksController(delivery, notification, push,
            NullLogger<CaseEventCallbacksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
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
            pushJson.RootElement.GetProperty("idempotency_key").GetString()
                .Should().Be(JsonDocument.Parse(calls[0].Body).RootElement
                    .GetProperty("notification_id").GetString());
            pushJson.RootElement.GetProperty("payload").GetProperty("deepLink").GetString()
                .Should().Be("jeeb://disputes/489660be-7844-42bc-a48f-f5c707b85b25");
        }
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

    [Fact]
    public async Task Ambiguous_Push_Failure_With_Claimed_Ledger_Remains_Retryable()
    {
        var calls = new List<CapturedCall>();
        var delivery = new CaseDeliveryClient(new HttpClient(new DeliveryHandler())
            { BaseAddress = new Uri("https://delivery/") });
        var controller = new CaseEventCallbacksController(delivery,
            new ServiceNotificationClient("https://notification/",
                new HttpClient(new NotificationHandler(calls))),
            new ServicePushNotificationClient("https://push/",
                new HttpClient(new PushConflictHandler(calls))),
            new FakePushRecovery("claimed"),
            NullLogger<CaseEventCallbacksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(Callback(), default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
        calls.Select(call => call.Service).Should().Equal("notification", "push");
    }

    [Fact]
    public async Task Terminal_Failed_NoDevice_Push_Is_Acknowledged_As_Degraded()
    {
        var calls = new List<CapturedCall>();
        var recovery = new FakePushRecovery("failed");
        var controller = Controller(calls, new PushFailureHandler(calls, HttpStatusCode.NotFound), recovery);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(Callback(), default);

        result.Should().BeOfType<AcceptedResult>();
        recovery.QueriedKeys.Should().HaveCount(2);
        calls.Select(call => call.Service).Should().Equal(
            "notification", "push", "notification", "push");
    }

    [Fact]
    public async Task Terminal_Succeeded_Push_Is_Acknowledged_After_Ambiguous_Response()
    {
        var calls = new List<CapturedCall>();
        var recovery = new FakePushRecovery("succeeded");
        var controller = Controller(calls, new PushConflictHandler(calls), recovery);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await controller.Dispatch(Callback(), default);

        result.Should().BeOfType<AcceptedResult>();
        recovery.QueriedKeys.Should().HaveCount(2);
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

    private static CaseEventCallbacksController Controller(List<CapturedCall> calls)
        => Controller(calls, new PushHandler(calls), new FakePushRecovery("succeeded"));

    private static CaseEventCallbacksController Controller(
        List<CapturedCall> calls, HttpMessageHandler pushHandler, IPushDispatchRecoveryClient recovery)
    {
        var delivery = new CaseDeliveryClient(new HttpClient(new DeliveryHandler())
            { BaseAddress = new Uri("https://delivery/") });
        return new CaseEventCallbacksController(delivery,
            new ServiceNotificationClient("https://notification/",
                new HttpClient(new NotificationHandler(calls))),
            new ServicePushNotificationClient("https://push/",
                new HttpClient(pushHandler)),
            recovery,
            NullLogger<CaseEventCallbacksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static GenericCaseCallbackV1 Callback() => JsonSerializer.Deserialize<GenericCaseCallbackV1>("""
        {
          "eventId":"b86d460d-7b8e-4f2b-b46e-f4fbb595890f",
          "eventType":"case.message_added",
          "occurredAt":"2026-08-05T10:15:30Z",
          "case":{"caseId":"489660be-7844-42bc-a48f-f5c707b85b25","kind":"dispute",
            "category":"damaged","subject":{"type":"delivery","ref":"delivery-1"},
            "requesterRef":"client-1","participantRefs":["client-1","courier-1"],
            "status":"pending","priority":"normal","version":4,
            "createdAt":"2026-08-05T09:00:00Z","updatedAt":"2026-08-05T10:15:30Z"},
          "actor":{"ref":"client-1","role":"client"},
          "data":{"messageType":"reply"}
        }
        """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed class DeliveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/v1/deliveries/delivery-1/status-history");
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

    private sealed class PushHandler(List<CapturedCall> calls) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            calls.Add(new CapturedCall("push", request.RequestUri!.Segments.Last(), body));
            return Json(HttpStatusCode.Created,
                "{\"message\":\"accepted\",\"timestamp\":\"2026-08-05T10:15:31Z\"}");
        }
    }

    private sealed class PushConflictHandler(List<CapturedCall> calls) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            calls.Add(new CapturedCall("push", request.RequestUri!.Segments.Last(), body));
            return Json(HttpStatusCode.Conflict,
                "{\"detail\":\"Dispatch outcome is unresolved; retry later\"}");
        }
    }

    private sealed class PushFailureHandler(List<CapturedCall> calls, HttpStatusCode status) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            calls.Add(new CapturedCall("push", request.RequestUri!.Segments.Last(), body));
            return Json(status, "{\"detail\":\"push was not delivered\"}");
        }
    }

    private sealed class FakePushRecovery(string state) : IPushDispatchRecoveryClient
    {
        public List<string> QueriedKeys { get; } = new();

        public Task<PushDispatchStatusV1> GetAsync(
            string idempotencyKey, int staleAfterSeconds, CancellationToken ct)
        {
            QueriedKeys.Add(idempotencyKey);
            return Task.FromResult(new PushDispatchStatusV1
            {
                IdempotencyKey = idempotencyKey,
                TargetUserId = "recipient",
                State = state,
                UpdatedAt = DateTimeOffset.Parse("2026-08-05T10:15:31Z"),
            });
        }

        public Task<PushDispatchListV1> ListStaleAsync(
            int olderThanSeconds, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<PushDispatchStatusV1> ResolveAsync(
            string idempotencyKey, PushDispatchResolutionV1 request, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed record CapturedCall(string Service, string Recipient, string Body);
}
