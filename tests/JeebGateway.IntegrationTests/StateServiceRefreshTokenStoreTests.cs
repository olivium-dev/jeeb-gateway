using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.StateService.Idempotency;
using JeebGateway.Tokens;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// M3 — the DURABLE refresh-token store (<see cref="StateServiceRefreshTokenStore"/>),
/// the state-service-backed replacement for <see cref="InMemoryRefreshTokenStore"/>.
/// These tests pin the bounce/replica-survivable behaviour the in-memory MVP could not
/// provide: a token, its hash/user indexes, and the revoke/rotate status chain all live
/// in the shared idempotency KV, so a COLD instance (empty process, simulating a bounce
/// or a different replica) re-resolves the authoritative state — including the
/// reuse-detection tombstone — from the durable store.
///
/// The KV is faked with an insert-once <see cref="FakeIdempotencyStore"/> (the same
/// primitive shape as <c>StateServiceOfferRequestIndexTests</c>), so the append-only
/// status-chain single-winner semantics are exercised exactly as in production.
/// </summary>
public sealed class StateServiceRefreshTokenStoreTests
{
    private static StateServiceRefreshTokenStore NewStore(IIdempotencyStore kv)
        => new(kv, NullLogger<StateServiceRefreshTokenStore>.Instance);

    private static RefreshToken Token(string id, string user, string hash, DateTimeOffset? expiresAt = null)
        => new()
        {
            TokenId = id,
            UserId = user,
            TokenHash = hash,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(30),
        };

    [Fact]
    public async Task Add_Then_FindByHash_Returns_The_Token()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);

        await store.AddAsync(Token("t1", "u1", "hash-1"), CancellationToken.None);

        var found = await store.FindByHashAsync("hash-1", CancellationToken.None);
        found.Should().NotBeNull();
        found!.TokenId.Should().Be("t1");
        found.UserId.Should().Be("u1");
        found.RevokedAt.Should().BeNull("a freshly-added token is active");
    }

    [Fact]
    public async Task FindByHash_ReHydrates_On_A_Cold_Instance()
    {
        // Instance A persists into the shared KV.
        var kv = new FakeIdempotencyStore();
        var instanceA = NewStore(kv);
        await instanceA.AddAsync(Token("t2", "u2", "hash-2"), CancellationToken.None);

        // Instance B is COLD (a bounce / different replica) but shares the SAME KV.
        var instanceB = NewStore(kv);
        var found = await instanceB.FindByHashAsync("hash-2", CancellationToken.None);

        found.Should().NotBeNull("the token survives a gateway bounce");
        found!.TokenId.Should().Be("t2");
    }

    [Fact]
    public async Task FindByHash_Unknown_Returns_Null()
    {
        var store = NewStore(new FakeIdempotencyStore());
        (await store.FindByHashAsync("never-seen", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Rotate_Revokes_Old_As_Rotated_And_Persists_New()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        await store.AddAsync(Token("old", "u3", "hash-old"), CancellationToken.None);

        var ok = await store.RotateAsync("old", Token("new", "u3", "hash-new"), CancellationToken.None);
        ok.Should().BeTrue();

        // Old is now revoked with reason Rotated + a forward link to the replacement.
        var old = await store.FindByHashAsync("hash-old", CancellationToken.None);
        old.Should().NotBeNull();
        old!.RevokedAt.Should().NotBeNull();
        old.RevokedReason.Should().Be(RevocationReason.Rotated.ToString());
        old.ReplacedByTokenId.Should().Be("new");

        // The replacement is active and independently findable.
        var replacement = await store.FindByHashAsync("hash-new", CancellationToken.None);
        replacement.Should().NotBeNull();
        replacement!.TokenId.Should().Be("new");
        replacement.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Rotate_Persists_Replacement_Before_Revoking_Old_FailOpen_Ordering()
    {
        // PP-11 (JEBV4-39): the durable persist of the NEW token must precede the
        // revoke/rotate write of the OLD token, so a mid-operation failure leaves the
        // old token valid (retryable) instead of stranding the user with neither token.
        var kv = new OrderRecordingIdempotencyStore();
        var store = NewStore(kv);
        await store.AddAsync(Token("old", "u-order", "hash-old"), CancellationToken.None);

        (await store.RotateAsync("old", Token("new", "u-order", "hash-new"), CancellationToken.None))
            .Should().BeTrue();

        // The replacement's durable base row is written BEFORE the old token's
        // revoke/rotate status revision (refresh-token-status:old:1).
        var replacementPersistedAt = kv.Writes.IndexOf("refresh-token:new");
        var oldRevokedAt = kv.Writes.IndexOf("refresh-token-status:old:1");
        (replacementPersistedAt >= 0).Should().BeTrue("the replacement base row is persisted");
        (oldRevokedAt >= 0).Should().BeTrue("the old token is rotated on seq 1");
        (replacementPersistedAt < oldRevokedAt).Should().BeTrue(
            "fail-open: the new token is durably persisted before the old one is revoked");
    }

    [Fact]
    public async Task Rotate_Is_FailOpen_When_Revoke_Old_Write_Fails()
    {
        // If the revoke-old write fails mid-rotate (transient state-service write error),
        // the OLD token must stay active — the refresh is safely retryable — and the
        // replacement must already be durable. This is the inverse of the previous
        // fail-CLOSED ordering, which revoked the old token first and could strand the
        // user if the replacement persist then failed.
        var kv = new FaultOnStatusWriteIdempotencyStore();
        var store = NewStore(kv);
        await store.AddAsync(Token("old", "u-failopen", "hash-old"), CancellationToken.None);

        await store
            .Invoking(s => s.RotateAsync("old", Token("new", "u-failopen", "hash-new"), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>("the revoke-old status write fails mid-rotate");

        // Old token survived the failed rotate → still usable for a retry.
        var old = await store.FindByHashAsync("hash-old", CancellationToken.None);
        old.Should().NotBeNull();
        old!.RevokedAt.Should().BeNull(
            "fail-open: a failed revoke-old write must leave the presented token usable for a retry");

        // Replacement was durably persisted BEFORE the (failed) revoke-old write.
        var replacement = await store.FindByHashAsync("hash-new", CancellationToken.None);
        replacement.Should().NotBeNull("the replacement is persisted before the old token is revoked");
        replacement!.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Rotate_Reuse_Tombstone_Is_Visible_To_A_Cold_Instance()
    {
        var kv = new FakeIdempotencyStore();
        await NewStore(kv).AddAsync(Token("old", "u4", "hash-old"), CancellationToken.None);
        (await NewStore(kv).RotateAsync("old", Token("new", "u4", "hash-new"), CancellationToken.None))
            .Should().BeTrue();

        // A cold instance replays the OLD token: it must see the revoked + ReplacedBy
        // tombstone so TokenService can burn the family (reuse detection spans replicas).
        var cold = await NewStore(kv).FindByHashAsync("hash-old", CancellationToken.None);
        cold.Should().NotBeNull();
        cold!.RevokedAt.Should().NotBeNull();
        cold.ReplacedByTokenId.Should().Be("new");
    }

    [Fact]
    public async Task Rotate_Second_Time_On_Same_Old_Returns_False_SingleWinner()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        await store.AddAsync(Token("old", "u5", "hash-old"), CancellationToken.None);

        (await store.RotateAsync("old", Token("new1", "u5", "hash-new1"), CancellationToken.None))
            .Should().BeTrue("the first rotate wins");

        // Replay of the now-rotated token loses: no active state ⇒ false (reuse).
        (await store.RotateAsync("old", Token("new2", "u5", "hash-new2"), CancellationToken.None))
            .Should().BeFalse("the token is already rotated");

        // The losing replacement was NOT persisted (no orphaned active token).
        (await store.FindByHashAsync("hash-new2", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Rotate_Concurrent_DoubleRotation_YieldsOneWinner_And_ReuseBurn_Revokes_The_Winner_Token()
    {
        // PP-11 blast-radius PIN (Finding-1). Two concurrent RotateAsync calls present the
        // SAME still-active old token — a BENIGN double-refresh (one client, two in-flight
        // refreshes of the same token). A rendezvous KV forces the genuine interleaving:
        // BOTH callers read the old token active AND persist their replacement BEFORE
        // either wins the seq+1 guard, so this exercises the reordered persist-before-guard
        // path (not the sequential IsActive short-circuit a synchronous fake collapses to).
        var kv = new RendezvousOnReplacementPersistStore(concurrentRotates: 2);
        var store = NewStore(kv);
        await store.AddAsync(Token("old", "u-conc", "hash-old"), CancellationToken.None);
        kv.Arm();

        // Dedicated threads (LongRunning) guarantee true concurrency regardless of the
        // thread-pool's sizing, so the rendezvous can't degenerate into a sequential run.
        var r1 = Task.Factory.StartNew(
            () => store.RotateAsync("old", Token("new1", "u-conc", "hash-new1"), CancellationToken.None),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        var r2 = Task.Factory.StartNew(
            () => store.RotateAsync("old", Token("new2", "u-conc", "hash-new2"), CancellationToken.None),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        var results = await Task.WhenAll(r1, r2);

        // (a) Exactly ONE winner — the seq+1 insert-once guard stays authoritative.
        results.Count(ok => ok).Should().Be(1, "the seq+1 guard elects exactly one rotation winner");
        results.Count(ok => !ok).Should().Be(1, "the concurrent loser is rejected (treated as reuse by the caller)");

        var winnerId = results[0] ? "new1" : "new2";
        var winnerHash = results[0] ? "hash-new1" : "hash-new2";
        var loserHash = results[0] ? "hash-new2" : "hash-new1";

        // (b) The old token is rotated toward the WINNER's replacement.
        var old = await store.FindByHashAsync("hash-old", CancellationToken.None);
        old!.RevokedAt.Should().NotBeNull();
        old.RevokedReason.Should().Be(RevocationReason.Rotated.ToString());
        old.ReplacedByTokenId.Should().Be(winnerId, "the seq-1 revision records the guard winner");

        // (c) BOTH replacements are persisted and ACTIVE — the reorder's new behaviour.
        // The winner's is reachable (its raw value was delivered to its caller); the
        // loser's is an UNREACHABLE orphan (raw value never returned). Under the OLD
        // ordering the loser persisted nothing; the persist-before-guard reorder makes the
        // loser deterministically leave a spuriously-active orphan.
        (await store.FindByHashAsync(winnerHash, CancellationToken.None))!.RevokedAt
            .Should().BeNull("the winner's replacement is delivered and active");
        (await store.FindByHashAsync(loserHash, CancellationToken.None))!.RevokedAt
            .Should().BeNull("the loser's replacement is persisted before it lost the guard → an active orphan");

        // (d) BLAST RADIUS — the honest, imperfect CURRENT behaviour (pinned, not fixed).
        // TokenService reacts to the loser's `false` by treating it as reuse and calling
        // RevokeChainAsync over the old token. Its UNCONDITIONAL revoke-all-for-user burns
        // EVERY active token for the user — INCLUDING the winner's just-delivered live
        // token. So a benign concurrent double-refresh silently force-logs-out the winner
        // on its next refresh. This is pinned so the true risk profile is visible in the
        // suite; the fix (a wider RotateAsync contract to tell benign collision from true
        // replay) is a design change deferred to the owner, out of PP-11 scope.
        var burned = await store.RevokeChainAsync("old", RevocationReason.ReuseDetected, CancellationToken.None);
        burned.Should().Be(2, "the reuse burn revokes BOTH replacements: the winner's live token AND the loser's orphan");

        var winnerAfterBurn = await store.FindByHashAsync(winnerHash, CancellationToken.None);
        winnerAfterBurn!.RevokedAt.Should().NotBeNull(
            "COLLATERAL: the loser's reuse burn revokes the winner's already-delivered token");
        winnerAfterBurn.RevokedReason.Should().Be(RevocationReason.ReuseDetected.ToString());
    }

    [Fact]
    public async Task Rotate_Is_Retryable_After_A_Transient_Revoke_Old_Write_Failure()
    {
        // Reviewer-B rec: prove the fail-open claim end-to-end. The FIRST rotate fails on
        // the revoke-old status write (a transient state-service blip) — the old token
        // must stay active. A RETRY against the now-healthy KV then completes the rotation,
        // proving the refresh is "safely retryable" rather than merely non-destructive.
        var kv = new TransientFaultOnStatusWriteIdempotencyStore(failuresBeforeHealthy: 1);
        var store = NewStore(kv);
        await store.AddAsync(Token("old", "u-retry", "hash-old"), CancellationToken.None);

        // First attempt: the revoke-old status write throws; the old token survives.
        await store
            .Invoking(s => s.RotateAsync("old", Token("new", "u-retry", "hash-new"), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>("the first revoke-old write hits the transient fault");
        (await store.FindByHashAsync("hash-old", CancellationToken.None))!.RevokedAt
            .Should().BeNull("fail-open: the presented token survives the failed rotate and stays retryable");

        // Retry on the healed KV (same old, same replacement). AddAsync is insert-once, so
        // re-persisting the replacement is an idempotent no-op and the guard write now lands.
        (await store.RotateAsync("old", Token("new", "u-retry", "hash-new"), CancellationToken.None))
            .Should().BeTrue("the refresh is safely retryable once the state-service write recovers");

        var oldAfter = await store.FindByHashAsync("hash-old", CancellationToken.None);
        oldAfter!.RevokedAt.Should().NotBeNull("the retry rotates the old token");
        oldAfter.RevokedReason.Should().Be(RevocationReason.Rotated.ToString());
        oldAfter.ReplacedByTokenId.Should().Be("new");
        (await store.FindByHashAsync("hash-new", CancellationToken.None))!.RevokedAt
            .Should().BeNull("the replacement is active after the successful retry");
    }

    [Fact]
    public async Task Rotate_Missing_Old_Returns_False()
    {
        var store = NewStore(new FakeIdempotencyStore());
        (await store.RotateAsync("ghost", Token("new", "u6", "hash-new"), CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Rotate_Expired_Old_Returns_False()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        await store.AddAsync(
            Token("old", "u7", "hash-old", DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken.None);

        (await store.RotateAsync("old", Token("new", "u7", "hash-new"), CancellationToken.None))
            .Should().BeFalse("an expired token is not active");
    }

    [Fact]
    public async Task Revoke_Marks_Revoked_And_Is_Idempotent()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        await store.AddAsync(Token("t8", "u8", "hash-8"), CancellationToken.None);

        await store.RevokeAsync("t8", RevocationReason.Logout, CancellationToken.None);

        var revoked = await store.FindByHashAsync("hash-8", CancellationToken.None);
        revoked!.RevokedAt.Should().NotBeNull();
        revoked.RevokedReason.Should().Be(RevocationReason.Logout.ToString());
        revoked.ReplacedByTokenId.Should().BeNull("a plain revoke does not chain");

        // Second revoke is a no-op (does not throw, reason unchanged).
        await store.RevokeAsync("t8", RevocationReason.Suspended, CancellationToken.None);
        var still = await store.FindByHashAsync("hash-8", CancellationToken.None);
        still!.RevokedReason.Should().Be(RevocationReason.Logout.ToString());
    }

    [Fact]
    public async Task Revoke_Missing_Token_Is_NoOp()
    {
        var store = NewStore(new FakeIdempotencyStore());
        await store.Invoking(s => s.RevokeAsync("ghost", RevocationReason.Logout, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task RevokeAllForUser_Revokes_Active_Returns_Count_And_Second_Call_Zero()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        await store.AddAsync(Token("a", "user-x", "hash-a"), CancellationToken.None);
        await store.AddAsync(Token("b", "user-x", "hash-b"), CancellationToken.None);
        await store.AddAsync(Token("c", "user-y", "hash-c"), CancellationToken.None); // different user

        var count = await store.RevokeAllForUserAsync("user-x", RevocationReason.Suspended, CancellationToken.None);
        count.Should().Be(2, "only user-x's two active tokens are revoked");

        (await store.FindByHashAsync("hash-a", CancellationToken.None))!.RevokedAt.Should().NotBeNull();
        (await store.FindByHashAsync("hash-b", CancellationToken.None))!.RevokedAt.Should().NotBeNull();
        (await store.FindByHashAsync("hash-c", CancellationToken.None))!.RevokedAt.Should().BeNull("user-y untouched");

        // Idempotent: a second sweep finds nothing active.
        (await store.RevokeAllForUserAsync("user-x", RevocationReason.Suspended, CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task RevokeChain_Revokes_Every_Active_Token_For_The_Owner()
    {
        var kv = new FakeIdempotencyStore();
        var store = NewStore(kv);
        await store.AddAsync(Token("head", "owner", "hash-head"), CancellationToken.None);
        await store.AddAsync(Token("sibling", "owner", "hash-sibling"), CancellationToken.None);

        var count = await store.RevokeChainAsync("head", RevocationReason.ReuseDetected, CancellationToken.None);
        count.Should().Be(2, "the whole family for the owner is burned");

        (await store.FindByHashAsync("hash-head", CancellationToken.None))!.RevokedReason
            .Should().Be(RevocationReason.ReuseDetected.ToString());
        (await store.FindByHashAsync("hash-sibling", CancellationToken.None))!.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeChain_Unknown_Start_Returns_Zero()
    {
        var store = NewStore(new FakeIdempotencyStore());
        (await store.RevokeChainAsync("ghost", RevocationReason.ReuseDetected, CancellationToken.None))
            .Should().Be(0);
    }

    // -----------------------------------------------------------------
    // fakes — insert-once opaque KV, mirroring StateServiceOfferRequestIndexTests
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
                .Select(kvp => new IdempotencyOutcome
                {
                    Inserted = false,
                    StatusCode = 200,
                    ResponseBodyJson = kvp.Value,
                })
                .ToList();
            return Task.FromResult(rows);
        }
    }

    /// <summary>
    /// Insert-once KV that also records the ORDER in which keys are written, so a test
    /// can assert the replacement is persisted before the presented token is revoked
    /// (PP-11 fail-open ordering).
    /// </summary>
    private sealed class OrderRecordingIdempotencyStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, string> _kv = new(StringComparer.Ordinal);

        public List<string> Writes { get; } = new();

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
        {
            Writes.Add(key);
            var stored = _kv.GetOrAdd(key, responseBodyJson);
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = ReferenceEquals(stored, responseBodyJson),
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
    }

    /// <summary>
    /// Insert-once KV that THROWS on any write to the revoke/rotate status chain
    /// (<c>refresh-token-status:</c>), simulating a transient state-service write failure
    /// on the revoke-old step. Reads and the replacement's base/index writes succeed, so a
    /// test can prove the replacement was already durable when the revoke-old write failed.
    /// </summary>
    private sealed class FaultOnStatusWriteIdempotencyStore : IIdempotencyStore
    {
        // Wire prefix of the append-only revoke/rotate status chain. Mirrors
        // StateServiceRefreshTokenStore.StatusKeyPrefix (internal to the gateway assembly).
        private const string StatusKeyPrefix = "refresh-token-status:";

        private readonly ConcurrentDictionary<string, string> _kv = new(StringComparer.Ordinal);

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
        {
            if (key.StartsWith(StatusKeyPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("simulated state-service write failure on revoke-old");
            }

            var stored = _kv.GetOrAdd(key, responseBodyJson);
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = ReferenceEquals(stored, responseBodyJson),
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
    }

    /// <summary>
    /// Insert-once KV that RENDEZVOUS-blocks concurrent rotates at the replacement's
    /// base-row write, so N concurrent RotateAsync calls all read the old token active and
    /// persist their replacement BEFORE any of them writes the seq+1 guard — deterministically
    /// exercising the reordered persist-before-guard double-rotation path (rather than the
    /// sequential IsActive short-circuit a synchronously-completing fake would collapse to).
    /// One-shot: once all parties arrive the gate stays open, so later single-threaded
    /// reads/writes (the assertions' find-by-hash and the reuse-burn) pass straight through.
    /// </summary>
    private sealed class RendezvousOnReplacementPersistStore : IIdempotencyStore
    {
        // A replacement base row is "refresh-token:{id}" (colon). The status/hash/user keys
        // start with "refresh-token-" (hyphen), so this prefix matches the base row ONLY.
        private const string RowKeyPrefix = "refresh-token:";

        private readonly ConcurrentDictionary<string, string> _kv = new(StringComparer.Ordinal);
        private readonly int _parties;
        private readonly ManualResetEventSlim _gate = new(false);
        private volatile bool _armed;
        private int _arrived;

        public RendezvousOnReplacementPersistStore(int concurrentRotates) => _parties = concurrentRotates;

        /// <summary>Enable the rendezvous only for the concurrent phase (setup writes run before this).</summary>
        public void Arm() => _armed = true;

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
        {
            // A replacement base-row write marks a rotate that has already read the old
            // token active. Hold each until all concurrent rotates arrive, so none reaches
            // the guard while another is still pre-guard. A 5s ceiling prevents a stuck
            // test from hanging CI (a missing party fails an assertion instead).
            if (_armed && key.StartsWith(RowKeyPrefix, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrived) >= _parties) _gate.Set();
                _gate.Wait(TimeSpan.FromSeconds(5), ct);
            }

            var stored = _kv.GetOrAdd(key, responseBodyJson);
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = ReferenceEquals(stored, responseBodyJson),
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
    }

    /// <summary>
    /// Insert-once KV whose revoke/rotate status-chain writes (<c>refresh-token-status:</c>)
    /// fail TRANSIENTLY: the first <c>failuresBeforeHealthy</c> such writes throw (a state-
    /// service blip), then writes heal. Base/hash/user writes and all reads always succeed,
    /// so a test can prove a rotate is safely RETRYABLE once the state-service recovers.
    /// </summary>
    private sealed class TransientFaultOnStatusWriteIdempotencyStore : IIdempotencyStore
    {
        private const string StatusKeyPrefix = "refresh-token-status:";

        private readonly ConcurrentDictionary<string, string> _kv = new(StringComparer.Ordinal);
        private readonly int _failuresBeforeHealthy;
        private int _statusWriteAttempts;

        public TransientFaultOnStatusWriteIdempotencyStore(int failuresBeforeHealthy)
            => _failuresBeforeHealthy = failuresBeforeHealthy;

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
        {
            if (key.StartsWith(StatusKeyPrefix, StringComparison.Ordinal)
                && Interlocked.Increment(ref _statusWriteAttempts) <= _failuresBeforeHealthy)
            {
                throw new InvalidOperationException("simulated TRANSIENT state-service write failure on revoke-old");
            }

            var stored = _kv.GetOrAdd(key, responseBodyJson);
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = ReferenceEquals(stored, responseBodyJson),
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
    }
}
