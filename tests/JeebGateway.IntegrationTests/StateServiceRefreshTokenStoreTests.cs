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
}
