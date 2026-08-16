using System.Security.Claims;
using JeebGateway.Admin;
using JeebGateway.Controllers;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace JeebGateway.UnitTests;

// AdminCancellationsController was the other consumer still on the deleted in-gateway stack.
// Its approve/reject push has never reached a device.
[Collection(PushMeterCollection.Name)]
public class AdminCancellationDecisionPushTests
{
    private const string Client = "33333333-3333-4333-8333-333333333333";
    private const string Jeeber = "44444444-4444-4444-8444-444444444444";
    private const string Admin = "55555555-5555-4555-8555-555555555555";

    private sealed record Decided(
        string DeliveryId, IActionResult Result,
        RecordingLogger<AdminCancellationsController> Log, long Unproduced);

    private static async Task<Decided> DecideAsync(
        ScriptedEventDispatcher events, string action = "approve", string? note = "looks fine")
    {
        var requests = new InMemoryRequestsStore(TimeProvider.System);
        var delivery = await requests.CreateAsync(
            new CreateRequestInput { ClientId = Client, Description = "box" }, CancellationToken.None);
        await requests.SetJeeberIdAsync(delivery.Id, Jeeber, CancellationToken.None);
        var row = (await requests.GetAsync(delivery.Id, CancellationToken.None))!;

        var log = new RecordingLogger<AdminCancellationsController>();
#pragma warning disable CS0618 // the controller carries a BFF-migration [Obsolete]; still live
        var controller = new AdminCancellationsController(
            new ScriptedCancellationService(row), requests, events, new InMemoryAdminAuditLog(), log)
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContextFor(Admin) }
        };
#pragma warning restore CS0618

        using var meter = new MeterCapture();
        var result = await controller.Decide(
            delivery.Id, new AdminCancellationDecisionBody { Action = action, Note = note },
            CancellationToken.None);

        return new Decided(delivery.Id, result, log, meter.UnproducedTotal());
    }

    private static HttpContext HttpContextFor(string userId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", userId) }, "test"));
        return ctx;
    }

    [Fact]
    public async Task An_approved_cancellation_hands_both_parties_over_to_notification_service()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Accepted);

        var run = await DecideAsync(events);

        Assert.IsType<OkObjectResult>(run.Result);
        Assert.Equal(new[] { Client, Jeeber }, events.Calls.Select(c => c.Receiver).ToArray());
        Assert.All(events.Calls, c =>
        {
            Assert.Equal(JeebGenericEventTypes.CancellationDecisionEventType, c.EventType);
            Assert.Equal(PushSilencePolicy.CategoryDelivery, c.Category);
            // Without type/delivery_id the mobile handler resolves `other` and drops it.
            Assert.Equal("delivery", c.Data["type"]);
            Assert.Equal(run.DeliveryId, c.Data["delivery_id"]);
            Assert.Equal("approved", c.Data["decision"]);
            Assert.Equal("looks fine", c.Data["note"]);
        });
        Assert.Equal(0L, run.Unproduced);
        Assert.Empty(run.Log.Errors);
    }

    [Fact]
    public async Task The_entity_id_is_the_controllers_own_pre_existing_idempotency_key()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Accepted);

        var run = await DecideAsync(events);

        Assert.Equal(
            $"{run.DeliveryId}:{ScriptedCancellationService.DecidedStatus}:cancel-decision:{Client}",
            events.Calls[0].EntityId);
        Assert.Equal(
            $"{run.DeliveryId}:{ScriptedCancellationService.DecidedStatus}:cancel-decision:{Jeeber}",
            events.Calls[1].EntityId);
    }

    // THE REGRESSION THIS FILE EXISTS FOR: the decision still returns 200 (it is committed),
    // but a lost push is now counted and Error-logged instead of vanishing into a warning.
    [Fact]
    public async Task A_failed_hand_over_still_returns_200_but_raises_the_alarm()
    {
        var events = new ScriptedEventDispatcher(new InvalidOperationException("centre down"));

        var run = await DecideAsync(events);

        Assert.IsType<OkObjectResult>(run.Result);
        Assert.Equal(2L, run.Unproduced);
        Assert.Contains(run.Log.Errors, e => e.Message.Contains(PushHandover.NoProducerEvent));
    }

    [Fact]
    public async Task An_unproven_hand_over_raises_the_alarm_too()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Unproven);

        var run = await DecideAsync(events);

        Assert.Equal(2L, run.Unproduced);
        Assert.Contains(run.Log.Errors, e => e.Message.Contains(PushHandover.NoProducerEvent));
    }

    private sealed class ScriptedCancellationService : ICancellationService
    {
        public const string DecidedStatus = "cancelled";

        private readonly DeliveryRequest _row;

        public ScriptedCancellationService(DeliveryRequest row) => _row = row;

        public Task<AdminCancellationDecisionResult> DecideAsync(
            string deliveryId, string action, CancellationToken ct)
        {
            _row.Status = DecidedStatus;
            return Task.FromResult(new AdminCancellationDecisionResult(
                action == "approve"
                    ? AdminCancellationDecisionOutcome.Approved
                    : AdminCancellationDecisionOutcome.Rejected,
                _row,
                "cancellation_requested"));
        }

        public Task<CancellationResult> CancelAsync(
            string deliveryId, string callerUserId, bool callerIsClient, bool callerIsJeeber,
            string? reason, CancellationToken ct) => throw new NotSupportedException();

        public Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingApprovalsAsync(
            int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> GetJeeberCancellationCountAsync(string jeeberId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<int> GetJeeberCancellationCountLast7DaysAsync(
            string jeeberId, DateTimeOffset at, CancellationToken ct) => Task.FromResult(0);
    }
}
