using FluentAssertions;
using JeebGateway.Tokens;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Tokens;

/// <summary>
/// JEBV4-260 — bounded rotation grace window.
///
/// When two refresh requests present the SAME still-active refresh token
/// near-simultaneously (a client that does not single-flight its refresh),
/// exactly one wins the rotation. Before this fix, the LOSER's request ran the
/// reuse path and burned the entire token family — including the winner's
/// just-delivered token — silently logging the winning client out on its next
/// refresh.
///
/// These tests reproduce the concurrent race deterministically via a store
/// double that rotates the token (simulating the concurrent winner) exactly
/// between the loser's load (still active) and the loser's own RotateAsync
/// (which then fails). They prove:
///   * within the grace window, the loser fails soft (401) and the winner's
///     token SURVIVES (no family burn);
///   * with the grace window disabled, the strict burn-on-race behavior is
///     preserved;
///   * a rotation older than the window still burns the chain;
///   * genuine sequential stale-token replay is still detected as reuse
///     (unchanged theft detection).
/// </summary>
public class RefreshRotationGraceWindowTests
{
    private const string Key = "grace-window-test-signing-key-at-least-32-bytes-long!!";

    [Fact]
    public async Task Concurrent_DoubleUse_WithinGrace_LoserFailsSoft_WinnerSurvives()
    {
        var store = new ConcurrentRaceStore();
        var svc = NewService(store, graceSeconds: 10);

        var pair = await svc.IssueAsync("u1", new[] { "customer" }, CancellationToken.None);

        // Arm the race: the loser will load the token as active, then a
        // concurrent winner rotates it before the loser's RotateAsync.
        store.ArmRaceFor(TokenService.HashToken(pair.RefreshToken));

        var loser = await svc.RefreshAsync(pair.RefreshToken, CancellationToken.None);

        // Loser fails soft (no fresh pair) — it was a benign queued duplicate.
        loser.Outcome.Should().Be(RefreshOutcome.Revoked);
        loser.Tokens.Should().BeNull();

        // The whole point: the concurrent winner's freshly-issued token was NOT
        // burned — the session is preserved.
        store.WinnerReplacement.Should().NotBeNull();
        store.WinnerReplacement!.RevokedAt.Should().BeNull(
            "the loser's benign double-refresh must not revoke the winner's just-delivered token");
        store.ChainBurned.Should().BeFalse("no family burn within the grace window");
    }

    [Fact]
    public async Task Concurrent_DoubleUse_GraceDisabled_BurnsChain_PreservesStrictBehavior()
    {
        var store = new ConcurrentRaceStore();
        var svc = NewService(store, graceSeconds: 0);

        var pair = await svc.IssueAsync("u1", new[] { "customer" }, CancellationToken.None);
        store.ArmRaceFor(TokenService.HashToken(pair.RefreshToken));

        var loser = await svc.RefreshAsync(pair.RefreshToken, CancellationToken.None);

        // graceSeconds = 0 restores the strict burn-on-race path.
        loser.Outcome.Should().Be(RefreshOutcome.ReuseDetected);
        store.ChainBurned.Should().BeTrue("with the grace window disabled the race burns the chain");
    }

    [Fact]
    public async Task Concurrent_DoubleUse_RotationOlderThanWindow_BurnsChain()
    {
        var store = new ConcurrentRaceStore { BackdateRotationBy = TimeSpan.FromSeconds(60) };
        var svc = NewService(store, graceSeconds: 10);

        var pair = await svc.IssueAsync("u1", new[] { "customer" }, CancellationToken.None);
        store.ArmRaceFor(TokenService.HashToken(pair.RefreshToken));

        var loser = await svc.RefreshAsync(pair.RefreshToken, CancellationToken.None);

        // The winner's rotation is 60s old — outside the 10s window → still burns.
        loser.Outcome.Should().Be(RefreshOutcome.ReuseDetected);
        store.ChainBurned.Should().BeTrue("a rotation older than the grace window is treated as reuse");
    }

    [Fact]
    public async Task Sequential_StaleTokenReplay_StillDetectedAsReuse_UnchangedTheftDetection()
    {
        // Genuine replay: rotate the token to completion first, then present the
        // now-spent token again. This hits the already-revoked-at-load path,
        // which is unaffected by the grace window — theft detection is preserved.
        var svc = NewService(new InMemoryRefreshTokenStore(), graceSeconds: 10);

        var pair = await svc.IssueAsync("u1", new[] { "customer" }, CancellationToken.None);
        var first = await svc.RefreshAsync(pair.RefreshToken, CancellationToken.None);
        first.Outcome.Should().Be(RefreshOutcome.Ok);

        var replay = await svc.RefreshAsync(pair.RefreshToken, CancellationToken.None);
        replay.Outcome.Should().Be(RefreshOutcome.ReuseDetected,
            "presenting an already-rotated (spent) token is genuine reuse and must still burn the chain");
    }

    private static TokenService NewService(IRefreshTokenStore store, int graceSeconds)
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = "jeeb-gateway",
            Audience = "jeeb-clients",
            SigningKey = Key,
            AccessTokenMinutes = 60,
            RefreshTokenDays = 30,
            RefreshRotationGraceSeconds = graceSeconds,
        });

        return new TokenService(store, new FakeUsersStoreAdapter(), options, TimeProvider.System);
    }

    /// <summary>
    /// Wraps <see cref="InMemoryRefreshTokenStore"/> and, on the loser's first
    /// load of the armed token (while still active), simulates a concurrent
    /// winner rotating it — so the loser's subsequent RotateAsync fails and the
    /// grace re-read observes the rotation. Records whether the family was burned
    /// and exposes the winner's replacement so tests can assert it survives.
    /// </summary>
    private sealed class ConcurrentRaceStore : IRefreshTokenStore
    {
        private readonly InMemoryRefreshTokenStore _inner = new();
        private string? _armedHash;

        public TimeSpan BackdateRotationBy { get; init; } = TimeSpan.Zero;
        public RefreshToken? WinnerReplacement { get; private set; }
        public bool ChainBurned { get; private set; }

        public void ArmRaceFor(string tokenHash) => _armedHash = tokenHash;

        public Task AddAsync(RefreshToken token, CancellationToken ct) => _inner.AddAsync(token, ct);

        public async Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct)
        {
            var current = await _inner.FindByHashAsync(tokenHash, ct);

            if (_armedHash is not null && tokenHash == _armedHash
                && current is not null && current.RevokedAt is null)
            {
                // Fire the race exactly once: a concurrent winner rotates the
                // token right now.
                _armedHash = null;

                WinnerReplacement = new RefreshToken
                {
                    TokenId = Guid.NewGuid().ToString(),
                    UserId = current.UserId,
                    TokenHash = "winner-" + Guid.NewGuid().ToString("N"),
                    IssuedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                };
                await _inner.RotateAsync(current.TokenId, WinnerReplacement, ct);

                if (BackdateRotationBy > TimeSpan.Zero && current.RevokedAt is not null)
                {
                    current.RevokedAt = current.RevokedAt.Value - BackdateRotationBy;
                }

                // Return a pre-race snapshot so the caller still sees it active
                // (models the load-then-rotate happens-before).
                return new RefreshToken
                {
                    TokenId = current.TokenId,
                    UserId = current.UserId,
                    TokenHash = current.TokenHash,
                    IssuedAt = current.IssuedAt,
                    ExpiresAt = current.ExpiresAt,
                    RevokedAt = null,
                };
            }

            return current;
        }

        public Task<bool> RotateAsync(string oldTokenId, RefreshToken replacement, CancellationToken ct) =>
            _inner.RotateAsync(oldTokenId, replacement, ct);

        public Task RevokeAsync(string tokenId, RevocationReason reason, CancellationToken ct) =>
            _inner.RevokeAsync(tokenId, reason, ct);

        public Task<int> RevokeAllForUserAsync(string userId, RevocationReason reason, CancellationToken ct) =>
            _inner.RevokeAllForUserAsync(userId, reason, ct);

        public Task<int> RevokeChainAsync(string startTokenId, RevocationReason reason, CancellationToken ct)
        {
            ChainBurned = true;
            return _inner.RevokeChainAsync(startTokenId, reason, ct);
        }
    }

    private sealed class FakeUsersStoreAdapter : IUsersStoreAdapter
    {
        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "customer" });

        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct)
            => Task.FromResult("customer");
    }
}
