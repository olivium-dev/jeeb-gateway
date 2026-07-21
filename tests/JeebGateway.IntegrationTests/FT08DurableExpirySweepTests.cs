using FluentAssertions;
using JeebGateway.Conversations;
using JeebGateway.Requests;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Durable;
using JeebGateway.Tiers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// FT-08: verify that <see cref="DurableRequestsStore.ListPendingCreatedAtOrBeforeAsync"/>
/// merges rows from delivery-service when the in-memory mirror is empty (post-restart
/// scenario).
///
/// <para>MOTIVATION RE-POINTED: this merge originally existed so the gateway's own
/// TTL sweeper could still expire post-restart rows. The gateway no longer OWNS
/// request TTL — delivery-service authors expiry and
/// <see cref="RequestExpiryObserver"/> only projects it — so that justification is
/// gone. The merge itself is KEPT and still load-bearing, because
/// <c>ListPendingCreatedAtOrBeforeAsync</c> has other live callers that must survive a
/// bounce: the jeeber feed (<c>Controllers/V1/JeebFeedController.cs</c>) and the GDPR
/// data-export packager (<c>Users/DataExport/IDataExportPackager.cs</c>). Read the
/// assertions below as POST-RESTART FEED/EXPORT RECOVERY, not as expiry durability.</para>
///
/// Key assertions:
///   A1. When DurableRequests:Enabled=true and the in-memory store is empty, rows
///       found in delivery-service (stage=Ordered, createdAt &lt;= cutoff) are returned.
///   A2. A row present in both the in-memory mirror and delivery-service is NOT
///       duplicated in the merged result.
///   A3. When delivery-service is unreachable the method does not throw; it
///       returns whatever the in-memory mirror holds.
/// </summary>
public class FT08DurableExpirySweepTests
{
    // -------------------------------------------------------------------
    // A1: post-restart — in-memory empty, delivery-service has candidates
    // -------------------------------------------------------------------
    [Fact]
    public async Task ListPending_WhenInMemoryEmpty_ReturnsRowsFromDeliveryService()
    {
        var cutoff     = DateTimeOffset.UtcNow;
        var shipmentId = Guid.NewGuid().ToString();

        var deliveryStub = new StubDeliveryClient(
            new ShipmentDetailDto { Id = shipmentId, CreatedAt = cutoff.AddMinutes(-5), CurrentStage = "Ordered" });

        var store = Build(new StubRequestsStore(Array.Empty<DeliveryRequest>()), deliveryStub);

        var result = await store.ListPendingCreatedAtOrBeforeAsync(cutoff, CancellationToken.None);

        result.Should().ContainSingle(r => r.Id == shipmentId,
            "the in-memory mirror is empty post-restart; delivery-service is the durable source");
    }

    // -------------------------------------------------------------------
    // A2: no duplication when row is in both mirror and delivery-service
    // -------------------------------------------------------------------
    [Fact]
    public async Task ListPending_NoDuplication_WhenRowInBothMirrorAndDeliveryService()
    {
        var cutoff   = DateTimeOffset.UtcNow;
        var sharedId = Guid.NewGuid().ToString();

        var mirrorRow = new DeliveryRequest
        {
            Id          = sharedId,
            ClientId    = "c1",
            Description = "desc",
            Status      = RequestStatus.Pending,
            CreatedAt   = cutoff.AddMinutes(-3),
        };

        var deliveryStub = new StubDeliveryClient(
            new ShipmentDetailDto { Id = sharedId, CreatedAt = cutoff.AddMinutes(-3), CurrentStage = "Ordered" });

        var store = Build(new StubRequestsStore(new[] { mirrorRow }), deliveryStub);

        var result = await store.ListPendingCreatedAtOrBeforeAsync(cutoff, CancellationToken.None);

        result.Where(r => r.Id == sharedId).Should().HaveCount(1,
            "a row in both mirror and delivery-service must not be counted twice");
    }

    // -------------------------------------------------------------------
    // A3: delivery-service unreachable → fallback to in-memory, no throw
    // -------------------------------------------------------------------
    [Fact]
    public async Task ListPending_WhenDeliveryServiceUnreachable_FallsBackGracefully()
    {
        var cutoff  = DateTimeOffset.UtcNow;
        var localId = Guid.NewGuid().ToString();

        var localRow = new DeliveryRequest
        {
            Id          = localId,
            ClientId    = "c2",
            Description = "local",
            Status      = RequestStatus.Pending,
            CreatedAt   = cutoff.AddMinutes(-1),
        };

        var store = Build(
            new StubRequestsStore(new[] { localRow }),
            new ThrowingDeliveryClient());

        var act = async () => await store.ListPendingCreatedAtOrBeforeAsync(cutoff, CancellationToken.None);

        var result = await act.Should().NotThrowAsync(
            "a delivery-service outage must not crash the expiry sweep");
        result.Subject.Should().Contain(r => r.Id == localId,
            "the in-memory mirror is the fallback when delivery-service is unreachable");
    }

    // -------------------------------------------------------------------
    // Test doubles
    // -------------------------------------------------------------------

    private sealed class StubRequestsStore : IRequestsStore
    {
        private readonly IReadOnlyList<DeliveryRequest> _rows;

        public StubRequestsStore(IReadOnlyList<DeliveryRequest> rows) => _rows = rows;

        public Task<IReadOnlyList<DeliveryRequest>> ListPendingCreatedAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken ct)
            => Task.FromResult(_rows);

        // All other methods are unused in these tests — throw to catch accidental calls.
        public Task<DeliveryRequest> CreateAsync(CreateRequestInput input, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryRequest> TryCreateWithLimitAsync(CreateRequestInput input, int clientLimit, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveForClientAsync(string clientId, CancellationToken ct) => Task.FromResult(0);
        public Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> SetJeeberIdAsync(string requestId, string jeeberId, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> TrySetAcceptedFeeAsync(string requestId, decimal fee, CancellationToken ct) => Task.FromResult(true);
        public Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct) => Task.FromResult<DeliveryRequest?>(null);
        public Task<bool> TryExpireAsync(string requestId, DateTimeOffset at, CancellationToken ct) => Task.FromResult(false);
        public Task<int> AnonymizeForClientAsync(string userId, string anonymizedHash, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<DeliveryRequest>> ListScheduledDueAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());
        public Task<bool> TryActivateScheduledAsync(string requestId, DateTimeOffset at, CancellationToken ct) => Task.FromResult(false);
        public Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct) => Task.FromResult<DeliveryRequest?>(null);
        public Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());
        public Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());
        public Task<int> CountActiveForJeeberAsync(string jeeberId, CancellationToken ct) => Task.FromResult(0);
        public Task<DeliveryRequest?> TryAcceptByJeeberAsync(string requestId, string jeeberId, int limit, DateTimeOffset at, CancellationToken ct) => Task.FromResult<DeliveryRequest?>(null);
        // JEB-1479: TryTransitionAsync (the legacy linear delivery-transition method)
        // was retired from IRequestsStore by #151. The stub no longer implements it.
        public Task<CancellationStoreResult?> TryCancelAsync(string requestId, IReadOnlySet<string> allowedFromStates, string targetStatus, string cancelledBy, string? reason, DateTimeOffset at, CancellationToken ct) => throw new NotImplementedException();
        public Task<CancellationStoreResult?> TryDecideCancellationAsync(string requestId, bool approve, DateTimeOffset at, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingCancellationsAsync(int page, int pageSize, CancellationToken ct) => Task.FromResult<(IReadOnlyList<DeliveryRequest>, int)>((Array.Empty<DeliveryRequest>(), 0));
        public Task<IReadOnlyList<DeliveryRequest>> ListJeeberCancelledAsync(string jeeberId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());
        public Task<OtpVerificationResult> TryVerifyOtpAsync(string requestId, string otpCode, int maxAttempts, DateTimeOffset at, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryRequest?> MarkClientUnreachableAsync(string requestId, DateTimeOffset at, CancellationToken ct) => Task.FromResult<DeliveryRequest?>(null);
        public Task<IReadOnlyList<DeliveryRequest>> ListUnreachableAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());
        public Task<bool> TrySetEscalationIdAsync(string requestId, string escalationId, CancellationToken ct) => Task.FromResult(false);
    }

    private abstract class BaseStubDeliveryClient : IDeliveryServiceClient
    {
        public abstract Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct);
        public Task<IReadOnlyList<JeebGateway.Tiers.DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<JeebGateway.Tiers.DeliveryTierDto>>(Array.Empty<JeebGateway.Tiers.DeliveryTierDto>());
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct) => Task.FromResult<DeliveryReadUpstream?>(null);
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotImplementedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotImplementedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => Task.FromResult<JeeberAvailabilityUpstream?>(null);
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class StubDeliveryClient : BaseStubDeliveryClient
    {
        private readonly ShipmentDetailDto[] _shipments;

        public StubDeliveryClient(params ShipmentDetailDto[] shipments) => _shipments = shipments;

        public override Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct)
            => Task.FromResult(new ShipmentsListDto { Shipments = _shipments.ToList() });
    }

    private sealed class ThrowingDeliveryClient : BaseStubDeliveryClient
    {
        public override Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static DurableRequestsStore Build(IRequestsStore inner, IDeliveryServiceClient delivery)
    {
        var bundles    = new NoOpSagaBundleRecorder();
        var convs      = new NoOpConversationProvisioner();
        var broadcasts = new NoOpBroadcastEventRecorder();
        var opts       = Options.Create(new DurableRequestsOptions { Enabled = true });
        var logger     = NullLogger<DurableRequestsStore>.Instance;

        return new DurableRequestsStore(inner, delivery, bundles, convs, broadcasts, opts, logger);
    }

    // Minimal no-op implementations to satisfy DurableRequestsStore constructor.

    private sealed class NoOpSagaBundleRecorder : ISagaBundleRecorder
    {
        public Task<SagaBundleRecordOutcome> RecordCreatedAsync(
            string sourceId, string tag, object state, CancellationToken ct)
            => Task.FromResult(SagaBundleRecordOutcome.Recorded);
    }

    private sealed class NoOpConversationProvisioner : IConversationProvisioner
    {
        public Task<string?> CreateBroadcastingConversationAsync(
            string requestId, string clientId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<string?> AdvanceToAcceptedAsync(
            string? conversationId, string winningJeeberId,
            IReadOnlyList<string> losingMemberIds, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class NoOpBroadcastEventRecorder : IBroadcastEventRecorder
    {
        public Task<BroadcastEventRecordOutcome> RecordBroadcastingAsync(
            string contextId, string phase, CancellationToken ct)
            => Task.FromResult(BroadcastEventRecordOutcome.Recorded);
    }
}
