using JeebGateway.Disputes.V2;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.UnitTests;

// DisputeCaseService was one of the two consumers still on the deleted in-gateway stack:
// every escalate/resolve push resolved NoDevices and the catch block hid it.
[Collection(PushMeterCollection.Name)]
public class DisputeCasePushHandoverTests
{
    private const string Client = "11111111-1111-4111-8111-111111111111";
    private const string Jeeber = "22222222-2222-4222-8222-222222222222";

    private sealed record Escalated(
        DisputeCase Case, RecordingLogger<DisputeCaseService> Log, long Unproduced);

    private static async Task<Escalated> EscalateAsync(ScriptedEventDispatcher dispatcher)
    {
        var requests = new InMemoryRequestsStore(TimeProvider.System);
        var delivery = await requests.CreateAsync(
            new CreateRequestInput { ClientId = Client, Description = "box" }, CancellationToken.None);
        await requests.SetJeeberIdAsync(delivery.Id, Jeeber, CancellationToken.None);

        var log = new RecordingLogger<DisputeCaseService>();
        var service = new DisputeCaseService(
            new InMemoryDisputeCaseStore(),
            requests,
            new NoEvidence(),
            new NoRefund(),
            dispatcher,
            TimeProvider.System,
            log);

        using var meter = new MeterCapture();
        var result = await service.EscalateAsync(
            new EscalateInput
            {
                DeliveryId = delivery.Id,
                OpenedByUserId = Client,
                Reason = "never arrived",
                IdempotencyKey = "dispute-" + delivery.Id,
            },
            CancellationToken.None);

        Assert.Equal(EscalateOutcome.Created, result.Outcome);
        return new Escalated(result.Case!, log, meter.UnproducedTotal());
    }

    [Fact]
    public async Task Escalate_hands_both_parties_over_to_notification_service()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Accepted);

        var run = await EscalateAsync(events);

        Assert.Equal(2, events.Calls.Count);
        Assert.Equal(new[] { Client, Jeeber }, events.Calls.Select(c => c.Receiver).ToArray());
        Assert.All(events.Calls, c =>
        {
            Assert.Equal(JeebGenericEventTypes.DisputeUpdateEventType, c.EventType);
            Assert.Equal(PushSilencePolicy.CategoryDispute, c.Category);
            Assert.Equal("dispute", c.Data["type"]);
            Assert.Equal(run.Case.Id, c.Data["case_id"]);
        });
        Assert.Equal(0L, run.Unproduced);
        Assert.Empty(run.Log.Errors);
    }

    [Fact]
    public async Task The_entity_id_is_the_services_own_pre_existing_idempotency_key()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Accepted);

        var run = await EscalateAsync(events);

        Assert.Equal($"dispute:{run.Case.Id}:opened", events.Calls[0].EntityId);
        Assert.Equal($"dispute:{run.Case.Id}:opened_counterparty", events.Calls[1].EntityId);
    }

    // THE REGRESSION THIS FILE EXISTS FOR: a hand-over failure must not be swallowed.
    // Re-wrapping the dispatch in a catch-and-drop turns both assertions red.
    [Fact]
    public async Task A_failed_hand_over_still_commits_the_case_but_raises_the_alarm()
    {
        var events = new ScriptedEventDispatcher(new InvalidOperationException("centre down"));

        var run = await EscalateAsync(events);

        Assert.NotNull(run.Case);
        Assert.Equal(2L, run.Unproduced);
        Assert.Contains(run.Log.Errors, e => e.Message.Contains(PushHandover.NoProducerEvent));
    }

    [Fact]
    public async Task An_unproven_hand_over_raises_the_alarm_too()
    {
        var events = new ScriptedEventDispatcher(GenericEventDispatchClassification.Unproven);

        var run = await EscalateAsync(events);

        Assert.Equal(2L, run.Unproduced);
        Assert.Contains(run.Log.Errors, e => e.Message.Contains(PushHandover.NoProducerEvent));
    }

    private sealed class NoEvidence : IDisputeEvidenceOrchestrator
    {
        public Task<DisputeEvidence> CaptureAsync(DisputeEvidenceRequest request, CancellationToken ct)
            => Task.FromResult(DisputeEvidence.Empty);
    }

    private sealed class NoRefund : IPaymentRefundClient
    {
        public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct)
            => throw new InvalidOperationException("not used by the escalate path");
    }
}
