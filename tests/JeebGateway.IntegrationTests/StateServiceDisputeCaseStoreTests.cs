using System.Collections.Concurrent;
using FluentAssertions;
using JeebGateway.Disputes.V2;
using JeebGateway.StateService.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-BE-028 / JEB-64 durability follow-up (ADR-0001 remediation for the v2 dispute-CASE
/// store). Pins the bounce/replica-survivable behaviour of
/// <see cref="StateServiceDisputeCaseStore"/>: every row/mutation is written to a durable KV,
/// so a fresh ("cold") store instance backed by the SAME <see cref="IIdempotencyStore"/>
/// observes exactly the same state a live instance would — the same contract
/// <c>StateServiceOfferRequestIndexTests</c> pins for the offer-routing index.
/// </summary>
public sealed class StateServiceDisputeCaseStoreTests
{
    [Fact]
    public async Task AddAsync_Then_ColdInstance_GetById_Returns_Same_Row()
    {
        var kv = new FakeIdempotencyStore();
        var writer = NewStore(kv);
        var @case = NewCase("case-1", "delivery-1", "user-opener");

        await writer.AddAsync(@case, CancellationToken.None);

        // Cold instance — fresh store object, same durable KV (simulates a bounce/replica).
        var reader = NewStore(kv);
        var fetched = await reader.GetByIdAsync("case-1", CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be("case-1");
        fetched.DeliveryId.Should().Be("delivery-1");
        fetched.OpenedByUserId.Should().Be("user-opener");
        fetched.State.Should().Be(DisputeCaseState.Open);
    }

    [Fact]
    public async Task GetByIdAsync_Unknown_Id_Returns_Null()
    {
        var store = NewStore(new FakeIdempotencyStore());
        (await store.GetByIdAsync("never-seen", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetByIdempotencyKeyAsync_Replays_The_Same_Case_PO_Blocker6()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        var @case = NewCase("case-2", "delivery-2", "user-2", idempotencyKey: "idem-abc");
        await store.AddAsync(@case, CancellationToken.None);

        // Cold instance — the replay lookup must resolve through the durable KV, not a
        // local cache.
        var replayStore = NewStore(kv);
        var replay = await replayStore.GetByIdempotencyKeyAsync("idem-abc", CancellationToken.None);

        replay.Should().NotBeNull();
        replay!.Id.Should().Be("case-2");
    }

    [Fact]
    public async Task GetByIdempotencyKeyAsync_Unseen_Key_Returns_Null()
    {
        var store = NewStore(new FakeIdempotencyStore());
        (await store.GetByIdempotencyKeyAsync("never-seen-key", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetActiveForDeliveryAsync_Returns_Newest_NonResolved_Case_And_Excludes_Resolved()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);

        var older = NewCase("case-old", "delivery-3", "user-a", openedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var newer = NewCase("case-new", "delivery-3", "user-b", openedAt: DateTimeOffset.UtcNow.AddHours(-1));
        await store.AddAsync(older, CancellationToken.None);
        await store.AddAsync(newer, CancellationToken.None);

        var active = await store.GetActiveForDeliveryAsync("delivery-3", CancellationToken.None);
        active.Should().NotBeNull();
        active!.Id.Should().Be("case-new");

        // Resolve the newer one — it must drop out of "active" (terminal-state filter).
        await store.ApplyResolutionAsync("case-new", new DisputeCaseResolutionPatch
        {
            State = DisputeCaseState.ResolvedNoAction,
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolverAdminId = "admin-1"
        }, CancellationToken.None);

        var activeAfterResolve = await store.GetActiveForDeliveryAsync("delivery-3", CancellationToken.None);
        activeAfterResolve.Should().NotBeNull();
        activeAfterResolve!.Id.Should().Be("case-old");
    }

    [Fact]
    public async Task GetActiveForDeliveryAsync_No_Cases_Returns_Null()
    {
        var store = NewStore(new FakeIdempotencyStore());
        (await store.GetActiveForDeliveryAsync("delivery-never", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ListForUserAsync_Returns_Cases_For_Opener_And_Counterparty_NewestFirst()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);

        var filedByUser = NewCase(
            "case-filed", "delivery-4", "user-x", counterpartyUserId: "user-y",
            openedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var filedAgainstUser = NewCase(
            "case-against", "delivery-5", "user-z", counterpartyUserId: "user-x",
            openedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await store.AddAsync(filedByUser, CancellationToken.None);
        await store.AddAsync(filedAgainstUser, CancellationToken.None);

        var list = await store.ListForUserAsync("user-x", CancellationToken.None);

        list.Select(c => c.Id).Should().Equal("case-against", "case-filed");
    }

    [Fact]
    public async Task ListForUserAsync_Unknown_User_Returns_Empty()
    {
        var store = NewStore(new FakeIdempotencyStore());
        (await store.ListForUserAsync("nobody", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyUnderReviewAsync_Transitions_Open_To_UnderReview_And_Persists_Across_Bounce()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        var @case = NewCase("case-6", "delivery-6", "user-6");
        await store.AddAsync(@case, CancellationToken.None);

        var updated = await store.ApplyUnderReviewAsync("case-6", CancellationToken.None);
        updated.Should().NotBeNull();
        updated!.State.Should().Be(DisputeCaseState.UnderReview);

        // Cold read must see the transition.
        var cold = NewStore(kv);
        var fetched = await cold.GetByIdAsync("case-6", CancellationToken.None);
        fetched!.State.Should().Be(DisputeCaseState.UnderReview);
    }

    [Fact]
    public async Task ApplyUnderReviewAsync_On_Resolved_Case_Returns_Null()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        var @case = NewCase("case-7", "delivery-7", "user-7");
        await store.AddAsync(@case, CancellationToken.None);
        await store.ApplyResolutionAsync("case-7", new DisputeCaseResolutionPatch
        {
            State = DisputeCaseState.ResolvedNoAction,
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolverAdminId = "admin-7"
        }, CancellationToken.None);

        var result = await store.ApplyUnderReviewAsync("case-7", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ApplyUnderReviewAsync_Unknown_Id_Returns_Null()
    {
        var store = NewStore(new FakeIdempotencyStore());
        (await store.ApplyUnderReviewAsync("never-seen", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ReplaceEvidenceAsync_Overwrites_Evidence_And_Persists_Across_Bounce()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        var @case = NewCase("case-8", "delivery-8", "user-8");
        await store.AddAsync(@case, CancellationToken.None);

        var replacement = new DisputeEvidence
        {
            ChatTranscriptJson = "[]",
            ChatTranscriptMessageCount = 3,
            GpsPolyline = new[] { new[] { 33.1, 35.2 } },
            Degraded = false
        };
        var updated = await store.ReplaceEvidenceAsync("case-8", replacement, CancellationToken.None);
        updated.Should().NotBeNull();
        updated!.Evidence.ChatTranscriptMessageCount.Should().Be(3);

        var cold = NewStore(kv);
        var fetched = await cold.GetByIdAsync("case-8", CancellationToken.None);
        fetched!.Evidence.ChatTranscriptMessageCount.Should().Be(3);
        fetched.Evidence.GpsPolyline.Should().HaveCount(1);
    }

    [Fact]
    public async Task ReplaceEvidenceAsync_Unknown_Id_Returns_Null()
    {
        var store = NewStore(new FakeIdempotencyStore());
        var evidence = new DisputeEvidence();
        (await store.ReplaceEvidenceAsync("never-seen", evidence, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ApplyResolutionAsync_Sets_Resolution_Fields_And_Persists_Across_Bounce()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        var @case = NewCase("case-9", "delivery-9", "user-9");
        await store.AddAsync(@case, CancellationToken.None);

        var resolvedAt = DateTimeOffset.UtcNow;
        var updated = await store.ApplyResolutionAsync("case-9", new DisputeCaseResolutionPatch
        {
            State = DisputeCaseState.ResolvedRefund,
            ResolvedAt = resolvedAt,
            ResolverAdminId = "admin-9",
            ResolutionNotes = "refunded per policy",
            RefundUsd = 12.50m,
            RefundLedgerEntryId = "ledger-1",
            ResolveIdempotencyKey = "resolve-idem-1"
        }, CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.State.Should().Be(DisputeCaseState.ResolvedRefund);
        updated.RefundUsd.Should().Be(12.50m);

        var cold = NewStore(kv);
        var fetched = await cold.GetByIdAsync("case-9", CancellationToken.None);
        fetched!.State.Should().Be(DisputeCaseState.ResolvedRefund);
        fetched.ResolverAdminId.Should().Be("admin-9");
        fetched.ResolutionNotes.Should().Be("refunded per policy");
        fetched.RefundLedgerEntryId.Should().Be("ledger-1");
        fetched.ResolveIdempotencyKey.Should().Be("resolve-idem-1");

        // The resolve idempotency-key index is maintained for parity with the escalate side
        // (PO blocker #6) even though no interface method reads it back today. Asserted via
        // the literal key format (not the store's internal *KeyPrefix consts) — the same
        // black-box style StateServiceOfferRequestIndexTests uses for its durable-key checks.
        kv.Get("dispute-case-resolve-idem:resolve-idem-1").Should().Be("case-9");
    }

    [Fact]
    public async Task ApplyResolutionAsync_Unknown_Id_Returns_Null()
    {
        var store = NewStore(new FakeIdempotencyStore());
        (await store.ApplyResolutionAsync("never-seen", new DisputeCaseResolutionPatch
        {
            State = DisputeCaseState.ResolvedNoAction,
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolverAdminId = "admin-x"
        }, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task UnderReview_Then_Resolve_Chain_Survives_A_Bounce()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        var @case = NewCase("case-10", "delivery-10", "user-10");
        await store.AddAsync(@case, CancellationToken.None);

        await store.ApplyUnderReviewAsync("case-10", CancellationToken.None);
        await store.ApplyResolutionAsync("case-10", new DisputeCaseResolutionPatch
        {
            State = DisputeCaseState.ResolvedNoAction,
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolverAdminId = "admin-10"
        }, CancellationToken.None);

        var cold = NewStore(kv);
        var fetched = await cold.GetByIdAsync("case-10", CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.State.Should().Be(DisputeCaseState.ResolvedNoAction);
        fetched.ResolverAdminId.Should().Be("admin-10");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static StateServiceDisputeCaseStore NewStore(IIdempotencyStore kv)
        => new(kv, NullLogger<StateServiceDisputeCaseStore>.Instance);

    private static DisputeCase NewCase(
        string id,
        string deliveryId,
        string openedByUserId,
        string? counterpartyUserId = null,
        string? idempotencyKey = null,
        DateTimeOffset? openedAt = null)
        => new()
        {
            Id = id,
            DeliveryId = deliveryId,
            OpenedByUserId = openedByUserId,
            CounterpartyUserId = counterpartyUserId,
            Reason = "test reason",
            State = DisputeCaseState.Open,
            OpenedAt = openedAt ?? DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey
        };

    // -----------------------------------------------------------------
    // fake
    // -----------------------------------------------------------------

    private sealed class FakeIdempotencyStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, string> _kv = new(StringComparer.Ordinal);

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
        {
            var stored = _kv.GetOrAdd(key, responseBodyJson);
            var inserted = ReferenceEquals(stored, responseBodyJson);
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = inserted,
                StatusCode = statusCode,
                ResponseBodyJson = stored,
            });
        }

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(_kv.TryGetValue(key, out var body)
                ? new IdempotencyOutcome { Inserted = false, StatusCode = 200, ResponseBodyJson = body }
                : null);

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(string prefix, CancellationToken ct)
        {
            IReadOnlyList<IdempotencyOutcome> rows = _kv
                .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kvp => new IdempotencyOutcome { Inserted = false, StatusCode = 200, ResponseBodyJson = kvp.Value })
                .ToList();
            return Task.FromResult(rows);
        }

        public string? Get(string key) => _kv.TryGetValue(key, out var v) ? v : null;
    }
}
