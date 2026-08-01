using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// The serialisation boundary for every test class that can move
/// <see cref="JeebGateway.Observability.BusinessOutcomeTelemetry.DurableReadFailures"/>.
///
/// <para><b>Why this exists.</b> The counter is <c>static</c> and its only tag is a
/// bounded <c>store</c> literal, so a <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// cannot tell one caller's increment from another's. xUnit runs different collections in
/// parallel by default, and this was not hypothetical: run alone,
/// <c>DurableReadFailureCounterTests</c> is 7/7 green; run in the same pass as
/// <c>StateServiceOfferRequestIndexTests</c> — whose
/// <c>ListOfferIdsForJeeber_When_DurableStore_Faults_DegradesTo_LocalCache_NeverThrows</c>
/// drives the SAME faulting read twice — the prefix-scan assertion sees three
/// measurements on <c>state-service-offer-routing-reverse</c> instead of one.</para>
///
/// <para>The direction of that error is the dangerous part: a neighbour's extra increments
/// make a MISSING increment look present. Naming one collection on every class that can
/// touch the counter is the xUnit-guaranteed fix — tests in one collection never run
/// concurrently. Same mechanism, same reason as <c>Gw5Pack/Gw5ChatSettleCollection</c>.</para>
///
/// <para>Membership is therefore <b>not</b> cosmetic. A new test class that drives any
/// durable-read degrade path — <see cref="JeebGateway.StateService.Durable.StateServiceOfferRequestIndex"/>
/// or <see cref="JeebGateway.Requests.DurableRequestsStore"/> with a faulting durable
/// dependency — must join this collection, or the counter assertions stop being
/// measurements.</para>
///
/// <para>Current members: <c>DurableReadFailureCounterTests</c>,
/// <c>StateServiceOfferRequestIndexTests</c>.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class DurableReadFailureCollection
{
    public const string Name = "durable-read-failure-counter";
}
