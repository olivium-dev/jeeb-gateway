using Xunit;

namespace JeebGateway.IntegrationTests.Gw5Pack;

/// <summary>
/// GW5 / W1.6-gateway — the serialisation boundary for every test class that can move
/// <see cref="JeebGateway.Conversations.ChatSettleTelemetry"/>.
///
/// <para><b>Why this exists.</b> Those counters are <c>static</c>, they carry no tags, and
/// a <see cref="System.Diagnostics.Metrics.MeterListener"/> therefore cannot tell one
/// caller's increment from another's. xUnit runs different collections in parallel by
/// default, so a delta measured in G4 while G2 or S03 was mid-settle would be measuring
/// the wrong thing — and worse, it would measure it in the direction that makes a broken
/// counter look fine (extra increments from a neighbour mask a missing one here). Naming
/// the same collection on every class that can touch those counters is the xUnit-guaranteed
/// way to stop that: tests in one collection never run concurrently.</para>
///
/// <para>Membership is therefore <b>not</b> cosmetic. If a new test class starts driving
/// <see cref="JeebGateway.Conversations.IAcceptChatSettler"/>,
/// <see cref="JeebGateway.Conversations.AcceptChatSettleReconciler"/> or an accept with the
/// Chat upstream flag ON, it must join this collection or G4's telemetry deltas stop being
/// measurements.</para>
///
/// <para>Current members: <c>G2_AcceptSettleReconcileTests</c>,
/// <c>G3_ReconcileDurabilityTests</c>, <c>G4_CompositionScheduleAndTelemetryTests</c>,
/// <c>S03AcceptConversationSeatTests</c>.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class Gw5ChatSettleCollection
{
    public const string Name = "gw5-chat-settle";
}
