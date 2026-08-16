using JeebGateway.Notifications;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.UnitTests;

// The deleted in-gateway push stack lost five categories in silence for the life of the
// product because every consumer wrapped the send in catch { LogWarning }. These pin the fix.
[Collection(PushMeterCollection.Name)]
public class PushHandoverAlarmTests
{
    private static async Task<(long Unproduced, RecordingLogger<PushHandoverAlarmTests> Log)> RunAsync(
        IGenericEventDispatcher dispatcher)
    {
        var log = new RecordingLogger<PushHandoverAlarmTests>();
        using var meter = new MeterCapture();

        await PushHandover.DispatchAsync(
            dispatcher, log, "jeeb.dispute_update", "user-1", "dispute:case-1:opened",
            "t", "b", new Dictionary<string, string>(), PushSilencePolicy.CategoryDispute,
            CancellationToken.None);

        return (meter.UnproducedTotal(), log);
    }

    [Theory]
    [InlineData(GenericEventDispatchClassification.Unproven)]
    [InlineData(GenericEventDispatchClassification.SkippedDirectDispatchArmed)]
    public async Task A_non_producing_outcome_is_counted_and_error_logged(
        GenericEventDispatchClassification classification)
    {
        var (unproduced, log) = await RunAsync(new ScriptedEventDispatcher(classification));

        Assert.Equal(1L, unproduced);
        Assert.Contains(log.Errors, e => e.Message.Contains(PushHandover.NoProducerEvent));
        Assert.Contains(log.Errors, e => e.Message.Contains(classification.ToString()));
    }

    [Fact]
    public async Task A_thrown_dispatcher_is_counted_and_error_logged_with_the_exception()
    {
        var boom = new InvalidOperationException("notification-service is down");

        var (unproduced, log) = await RunAsync(new ScriptedEventDispatcher(boom));

        Assert.Equal(1L, unproduced);
        Assert.Contains(log.Errors, e => ReferenceEquals(e.Exception, boom));
        Assert.Contains(log.Errors, e => e.Message.Contains(PushHandover.NoProducerEvent));
    }

    // NON-VACUITY CONTROL. The same harness must be able to return the other answer, or the
    // three assertions above prove nothing about the alarm.
    [Theory]
    [InlineData(GenericEventDispatchClassification.Accepted)]
    [InlineData(GenericEventDispatchClassification.Deduplicated)]
    [InlineData(GenericEventDispatchClassification.AcceptedAfterAmbiguousResponse)]
    public async Task A_producer_owned_outcome_raises_no_alarm(
        GenericEventDispatchClassification classification)
    {
        var (unproduced, log) = await RunAsync(new ScriptedEventDispatcher(classification));

        Assert.Equal(0L, unproduced);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_is_not_reported_as_a_lost_push()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var log = new RecordingLogger<PushHandoverAlarmTests>();
        using var meter = new MeterCapture();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PushHandover.DispatchAsync(
            new ScriptedEventDispatcher(new OperationCanceledException(cts.Token)),
            log, "jeeb.dispute_update", "user-1", "e-1", "t", "b",
            new Dictionary<string, string>(), PushSilencePolicy.CategoryDispute, cts.Token));

        Assert.Equal(0L, meter.UnproducedTotal());
    }

    [Fact]
    public void IsProducerOwned_partitions_the_classification_enum_exhaustively()
    {
        // A new classification must be decided deliberately, not inherit a default.
        var all = Enum.GetValues<GenericEventDispatchClassification>();
        var owned = all.Where(PushHandover.IsProducerOwned).ToArray();
        var lost = all.Where(c => !PushHandover.IsProducerOwned(c)).ToArray();

        Assert.Equal(3, owned.Length);
        Assert.Equal(2, lost.Length);
        Assert.Contains(GenericEventDispatchClassification.Unproven, lost);
        Assert.Contains(GenericEventDispatchClassification.SkippedDirectDispatchArmed, lost);
    }
}
