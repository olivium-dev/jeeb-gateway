using FluentAssertions;
using JeebGateway.Tokens;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class BoundedSessionRevocationTests
{
    [Fact]
    public async Task DurableFamilyMarkerMakesRowSweepFailureACompletedCleanup()
    {
        var store = new MarkerThenSweepFailureStore();
        var service = new TokenService(
            store,
            users: null!,
            Options.Create(new JwtOptions
            {
                SigningKey = "bounded-session-tests-signing-key-32bytes",
            }),
            TimeProvider.System);

        var revoked = await service.RevokeBoundedSessionAsync(
            "exact-family", RevocationReason.DevCredentialRemoved, CancellationToken.None);

        revoked.Should().Be(0);
        store.MarkedFamilies.Should().Equal("exact-family");
        store.SweptFamilies.Should().Equal("exact-family");
    }

    private sealed class MarkerThenSweepFailureStore : IRefreshTokenStore
    {
        public List<string> MarkedFamilies { get; } = [];
        public List<string> SweptFamilies { get; } = [];

        public Task MarkBoundedSessionRevokedAsync(string sessionFamilyId, CancellationToken ct)
        {
            MarkedFamilies.Add(sessionFamilyId);
            return Task.CompletedTask;
        }

        public Task<int> RevokeBoundedFamilyAsync(
            string sessionFamilyId,
            RevocationReason reason,
            CancellationToken ct)
        {
            SweptFamilies.Add(sessionFamilyId);
            throw new InvalidOperationException("simulated post-marker row-sweep outage");
        }

        public Task AddAsync(RefreshToken token, CancellationToken ct) => throw new NotSupportedException();
        public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RotateAsync(string oldTokenId, RefreshToken replacement, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeAsync(string tokenId, RevocationReason reason, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> RevokeAllForUserAsync(string userId, RevocationReason reason, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsBoundedSessionRevokedAsync(string sessionFamilyId, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> RevokeChainAsync(string startTokenId, RevocationReason reason, CancellationToken ct) => throw new NotSupportedException();
    }
}
