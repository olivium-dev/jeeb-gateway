using System.Diagnostics.Metrics;
using FluentAssertions;
using JeebGateway.Auth;
using JeebGateway.Observability;
using JeebGateway.Tokens;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class LegacyPhoneSessionRejectionTests
{
    private const string SigningKey =
        "legacy-session-test-signing-key-at-least-32-bytes";

    [Theory]
    [InlineData("+12345678", true)]
    [InlineData("+123456789012345", true)]
    [InlineData("+1234567", false)]
    [InlineData("+1234567890123456", false)]
    [InlineData("+01234567", false)]
    [InlineData("0012345678", false)]
    [InlineData(" +12345678", false)]
    [InlineData("+12345678 ", false)]
    [InlineData("+123 45678", false)]
    [InlineData("+123-45678", false)]
    [InlineData("+123.45678", false)]
    [InlineData("+١٢٣٤٥٦٧٨", false)]
    [InlineData("+１２３４５６７８", false)]
    [InlineData("11111111-2222-3333-4444-555555555555", false)]
    [InlineData("oidc_operator", false)]
    [InlineData("operations_admin", false)]
    [InlineData("partner:example", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Classifier_MatchesOnlyStrictCompactAsciiE164Subjects(
        string? subject,
        bool expected)
    {
        LegacyPhoneSessionRejection.IsLegacySubject(subject).Should().Be(expected);
    }

    [Fact]
    public async Task Refresh_LegacySubject_RevokesExactSubjectBeforeAuthorityOrMint()
    {
        const string legacySubject = "+99999999";
        const string otherSubject = "+88888888";
        const string rawToken = "legacy-refresh-token";
        var expectedCancellation = new CancellationTokenSource().Token;
        var store = new RecordingRefreshStore(Token(rawToken, legacySubject));
        var users = new RecordingUsersStoreAdapter();
        var service = NewService(store, users);
        var resolverCalls = 0;

        var result = await service.RefreshAsync(
            rawToken,
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult<TokenRoleContext?>(
                    new TokenRoleContext(["client"], "client"));
            },
            expectedCancellation);

        result.Outcome.Should().Be(RefreshOutcome.Revoked);
        result.Tokens.Should().BeNull();
        store.RevokeAllCalls.Should().ContainSingle().Which.Should().Be(
            (legacySubject, RevocationReason.LegacyPhoneSubject, expectedCancellation));
        store.RevokeAllCalls.Should().NotContain(call => call.UserId == otherSubject);
        store.RotateCalls.Should().Be(0);
        store.AddCalls.Should().Be(0);
        users.RoleLookups.Should().Be(0);
        users.ActiveRoleLookups.Should().Be(0);
        resolverCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_LegacySubject_RejectsWhenRevocationFaultsOrCancels(
        bool cancellation)
    {
        const string legacySubject = "+99999999";
        const string rawToken = "faulting-legacy-refresh-token";
        using var cts = new CancellationTokenSource();
        if (cancellation) cts.Cancel();
        var store = new RecordingRefreshStore(Token(rawToken, legacySubject))
        {
            RevokeFailure = cancellation
                ? new OperationCanceledException(cts.Token)
                : new InvalidOperationException("simulated revocation-store failure"),
        };
        var users = new RecordingUsersStoreAdapter();
        var service = NewService(store, users);

        var result = await service.RefreshAsync(rawToken, cts.Token);

        result.Outcome.Should().Be(RefreshOutcome.Revoked,
            "revocation failure must never reactivate a retired legacy session");
        result.Tokens.Should().BeNull();
        store.RevokeAllCalls.Should().ContainSingle().Which.CancellationToken
            .Should().Be(cts.Token);
        store.RotateCalls.Should().Be(0);
        store.AddCalls.Should().Be(0);
        users.RoleLookups.Should().Be(0);
        users.ActiveRoleLookups.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_CanonicalGuid_RemainsUnchanged()
    {
        const string canonicalSubject = "11111111-2222-3333-4444-555555555555";
        var inner = new InMemoryRefreshTokenStore();
        var users = new RecordingUsersStoreAdapter();
        var service = NewService(inner, users);
        var issued = await service.IssueAsync(
            canonicalSubject, ["client"], "client", authentication: null,
            CancellationToken.None);

        var result = await service.RefreshAsync(issued.RefreshToken, CancellationToken.None);

        result.Outcome.Should().Be(RefreshOutcome.Ok);
        result.Tokens.Should().NotBeNull();
        users.RoleLookups.Should().Be(1);
        users.ActiveRoleLookups.Should().Be(1);
    }

    [Fact]
    public void Telemetry_UsesOnlyTheClosedReasonVocabulary()
    {
        var measurements = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == BusinessOutcomeTelemetry.MeterName
                && instrument.Name == "auth.session.legacy_phone_rejections")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            measurements.Add(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
        });
        listener.Start();

        foreach (var reason in Enum.GetValues<LegacySessionRejectionReason>())
            BusinessOutcomeTelemetry.RecordLegacySessionRejection(reason);

        foreach (var tags in measurements)
            tags.Keys.Should().Equal("reason");
        measurements.Select(tags => tags["reason"]).Should().BeEquivalentTo(
            new object?[]
            {
                "access_legacy_subject",
                "refresh_legacy_subject",
                "revocation_failure",
            });
        foreach (var value in measurements.SelectMany(tags => tags.Values))
            (value?.ToString()?.Contains('+', StringComparison.Ordinal) ?? false).Should().BeFalse();
    }

    private static TokenService NewService(
        IRefreshTokenStore store,
        IUsersStoreAdapter users) =>
        new(
            store,
            users,
            Options.Create(new JwtOptions
            {
                Issuer = "jeeb-gateway",
                Audience = "jeeb-clients",
                SigningKey = SigningKey,
                AccessTokenMinutes = 15,
                RefreshTokenDays = 30,
            }),
            TimeProvider.System);

    private static RefreshToken Token(string rawToken, string userId) => new()
    {
        TokenId = "legacy-token-id",
        UserId = userId,
        TokenHash = TokenService.HashToken(rawToken),
        IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
    };

    private sealed class RecordingUsersStoreAdapter : IUsersStoreAdapter
    {
        public int RoleLookups { get; private set; }
        public int ActiveRoleLookups { get; private set; }

        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct)
        {
            RoleLookups++;
            return Task.FromResult<IReadOnlyList<string>>(["client"]);
        }

        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct)
        {
            ActiveRoleLookups++;
            return Task.FromResult("client");
        }
    }

    private sealed class RecordingRefreshStore(RefreshToken record) : IRefreshTokenStore
    {
        public List<(string UserId, RevocationReason Reason, CancellationToken CancellationToken)>
            RevokeAllCalls
        { get; } = [];
        public Exception? RevokeFailure { get; init; }
        public int AddCalls { get; private set; }
        public int RotateCalls { get; private set; }

        public Task AddAsync(RefreshToken token, CancellationToken ct)
        {
            AddCalls++;
            return Task.CompletedTask;
        }

        public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct) =>
            Task.FromResult<RefreshToken?>(
                string.Equals(tokenHash, record.TokenHash, StringComparison.Ordinal) ? record : null);

        public Task<bool> RotateAsync(
            string oldTokenId,
            RefreshToken replacement,
            CancellationToken ct)
        {
            RotateCalls++;
            return Task.FromResult(false);
        }

        public Task RevokeAsync(string tokenId, RevocationReason reason, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<int> RevokeAllForUserAsync(
            string userId,
            RevocationReason reason,
            CancellationToken ct)
        {
            RevokeAllCalls.Add((userId, reason, ct));
            return RevokeFailure is null
                ? Task.FromResult(1)
                : Task.FromException<int>(RevokeFailure);
        }

        public Task<int> RevokeChainAsync(
            string startTokenId,
            RevocationReason reason,
            CancellationToken ct) => Task.FromResult(0);
    }
}
