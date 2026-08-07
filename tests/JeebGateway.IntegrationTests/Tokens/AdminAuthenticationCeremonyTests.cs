using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests.Tokens;

public sealed class AdminAuthenticationCeremonyTests
{
    private const string GatewayKey = "admin-ceremony-gateway-signing-key-at-least-32-bytes";
    private const string UmKey = "admin-ceremony-upstream-signing-key-at-least-32-bytes";

    [Fact]
    public async Task VerifiedMfaCeremonySurvivesRefreshRotationsWithoutAdvancingAuthTime()
    {
        var now = DateTimeOffset.UtcNow;
        var authTime = now.AddMinutes(-2).ToUnixTimeSeconds();
        var service = NewService(new InMemoryRefreshTokenStore(), now);
        var initial = await service.IssueAsync(
            "operator-1", new[] { "operations_admin" }, "operations_admin",
            new VerifiedAuthenticationContext(authTime, new[] { "pwd", "mfa" }),
            CancellationToken.None);

        var first = await service.RefreshAsync(initial.RefreshToken, ResolveAdmin, CancellationToken.None);
        var second = await service.RefreshAsync(first.Tokens!.RefreshToken, ResolveAdmin, CancellationToken.None);

        first.Outcome.Should().Be(RefreshOutcome.Ok);
        second.Outcome.Should().Be(RefreshOutcome.Ok);
        AssertCeremony(first.Tokens.AccessToken, authTime, "pwd", "mfa");
        AssertCeremony(second.Tokens!.AccessToken, authTime, "pwd", "mfa");
    }

    [Fact]
    public async Task PasswordOnlyOrLegacyRefreshRecordsNeverGainMfaClaims()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryRefreshTokenStore();
        var service = NewService(store, now);
        var passwordOnly = await service.IssueAsync(
            "operator-1", new[] { "operations_admin" }, "operations_admin",
            authentication: null, CancellationToken.None);
        var passwordRefresh = await service.RefreshAsync(
            passwordOnly.RefreshToken, ResolveAdmin, CancellationToken.None);

        var legacyRaw = "legacy-admin-refresh-token-with-no-ceremony-context";
        await store.AddAsync(new RefreshToken
        {
            TokenId = Guid.NewGuid().ToString("N"),
            UserId = "operator-legacy",
            TokenHash = TokenService.HashToken(legacyRaw),
            IssuedAt = now,
            ExpiresAt = now.AddDays(1),
        }, CancellationToken.None);
        var legacyRefresh = await service.RefreshAsync(
            legacyRaw, ResolveAdmin, CancellationToken.None);

        passwordRefresh.Outcome.Should().Be(RefreshOutcome.Ok);
        legacyRefresh.Outcome.Should().Be(RefreshOutcome.Ok);
        AssertNoCeremony(passwordRefresh.Tokens!.AccessToken);
        AssertNoCeremony(legacyRefresh.Tokens!.AccessToken);
    }

    [Fact]
    public void UpstreamCeremonyClaimsAreAcceptedOnlyFromAValidUmToken()
    {
        var now = DateTimeOffset.UtcNow;
        var authTime = now.AddMinutes(-1).ToUnixTimeSeconds();
        var validator = new UmAuthenticationContextValidator(
            Options.Create(new UmJwtOptions
            {
                Issuer = "user-management",
                Audience = "user-management",
                SigningKey = UmKey,
            }),
            Options.Create(GatewayOptions()),
            new FixedTimeProvider(now));

        var valid = MintUmToken(UmKey, now, authTime);
        var forged = MintUmToken("forged-ceremony-signing-key-at-least-32-bytes-long", now, authTime);

        var context = validator.Validate(valid);
        context.Should().NotBeNull();
        context!.AuthTime.Should().Be(authTime);
        context.Methods.Should().BeEquivalentTo("pwd", "mfa");
        validator.Validate(forged).Should().BeNull();
        validator.Validate("not-a-token").Should().BeNull();
    }

    [Fact]
    public async Task ExternalOperatorRefreshUsesVerifiedRoleSnapshotAndExpiresAtBoundedSessionDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        var service = new TokenService(
            new InMemoryRefreshTokenStore(),
            new ThrowingUsersStoreAdapter(),
            Options.Create(GatewayOptions()),
            clock);
        var context = new VerifiedAuthenticationContext(
            now.ToUnixTimeSeconds(),
            new[] { "pwd", "mfa" },
            provider: "https://identity.example.test",
            sessionExpiresAt: now.AddHours(8),
            displayName: "Finance Operator",
            email: "finance@example.test",
            persistRoleContext: true);
        var initial = await service.IssueAsync(
            "oidc_operator", new[] { "finance_approver" }, "finance_approver", context,
            CancellationToken.None);

        var refreshed = await service.RefreshAsync(
            initial.RefreshToken,
            (_, _) => throw new InvalidOperationException("OIDC refresh must not query user-management"),
            CancellationToken.None);

        refreshed.Outcome.Should().Be(RefreshOutcome.Ok);
        var access = new JwtSecurityTokenHandler().ReadJwtToken(refreshed.Tokens!.AccessToken);
        access.Claims.Where(claim => claim.Type == "roles").Select(claim => claim.Value)
            .Should().Equal("finance_approver");
        access.Claims.Single(claim => claim.Type == "idp").Value.Should()
            .Be("https://identity.example.test");
        access.Claims.Single(claim => claim.Type == "name").Value.Should().Be("Finance Operator");
        access.Claims.Single(claim => claim.Type == "email").Value.Should().Be("finance@example.test");
        initial.AccessTokenExpiresAt.Should().Be(now.AddMinutes(15));
        initial.RefreshTokenExpiresAt.Should().Be(now.AddHours(8));

        clock.Now = now.AddHours(7).AddMinutes(55);
        var nearDeadline = await service.RefreshAsync(
            refreshed.Tokens.RefreshToken,
            (_, _) => throw new InvalidOperationException("OIDC refresh must use the role snapshot"),
            CancellationToken.None);
        nearDeadline.Outcome.Should().Be(RefreshOutcome.Ok);
        nearDeadline.Tokens!.AccessTokenExpiresAt.Should().Be(now.AddHours(8));
        nearDeadline.Tokens.RefreshTokenExpiresAt.Should().Be(now.AddHours(8));

        clock.Now = now.AddHours(8).AddSeconds(1);
        var expired = await service.RefreshAsync(
            nearDeadline.Tokens.RefreshToken,
            (_, _) => throw new InvalidOperationException("expired OIDC session must fail before role lookup"),
            CancellationToken.None);
        expired.Outcome.Should().Be(RefreshOutcome.AuthenticationExpired);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("roles")]
    [InlineData("active_role")]
    [InlineData("methods")]
    [InlineData("auth_time")]
    [InlineData("session_deadline")]
    public async Task PartialExternalRefreshTupleIsRejectedWithoutOrdinaryLifetimeFallback(
        string missing)
    {
        var now = DateTimeOffset.UtcNow;
        var raw = "partial-external-refresh-" + missing;
        var store = new InMemoryRefreshTokenStore();
        await store.AddAsync(new RefreshToken
        {
            TokenId = Guid.NewGuid().ToString("N"),
            UserId = "oidc_operator",
            TokenHash = TokenService.HashToken(raw),
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
            IdentityProvider = missing == "provider" ? null : "https://identity.example.test",
            RoleSnapshot = missing == "roles" ? null : new[] { "finance_approver" },
            ActiveRoleSnapshot = missing == "active_role" ? null : "finance_approver",
            AuthenticationMethods = missing == "methods" ? null : new[] { "mfa" },
            AuthenticationTime = missing == "auth_time" ? null : now.ToUnixTimeSeconds(),
            AuthenticationSessionExpiresAt = missing == "session_deadline" ? null : now.AddHours(8),
        }, CancellationToken.None);
        var service = new TokenService(
            store,
            new ThrowingUsersStoreAdapter(),
            Options.Create(GatewayOptions()),
            new FixedTimeProvider(now));

        var result = await service.RefreshAsync(
            raw,
            (_, _) => throw new InvalidOperationException("partial external context must not resolve roles"),
            CancellationToken.None);

        result.Outcome.Should().Be(RefreshOutcome.AuthenticationExpired);
        result.Tokens.Should().BeNull();
    }

    private static Task<TokenRoleContext?> ResolveAdmin(string userId, CancellationToken ct) =>
        Task.FromResult<TokenRoleContext?>(
            new TokenRoleContext(new[] { "operations_admin" }, "operations_admin"));

    private static TokenService NewService(IRefreshTokenStore store, DateTimeOffset now) =>
        new(store, new FakeUsersStoreAdapter(), Options.Create(GatewayOptions()), new FixedTimeProvider(now));

    private static JwtOptions GatewayOptions() => new()
    {
        Issuer = "jeeb-gateway",
        Audience = "jeeb-clients",
        SigningKey = GatewayKey,
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30,
    };

    private static string MintUmToken(string key, DateTimeOffset now, long authTime)
    {
        var jwt = new JwtSecurityToken(
            issuer: "user-management",
            audience: "user-management",
            claims: new[]
            {
                new Claim("sub", "operator-1"),
                new Claim("auth_time", authTime.ToString(), ClaimValueTypes.Integer64),
                new Claim("amr", "pwd"),
                new Claim("amr", "mfa"),
            },
            notBefore: now.AddMinutes(-5).UtcDateTime,
            expires: now.AddMinutes(5).UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static void AssertCeremony(string rawToken, long authTime, params string[] methods)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(rawToken);
        token.Claims.Single(claim => claim.Type == "auth_time").Value.Should().Be(authTime.ToString());
        token.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value)
            .Should().BeEquivalentTo(methods);
    }

    private static void AssertNoCeremony(string rawToken)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(rawToken);
        token.Claims.Should().NotContain(claim =>
            claim.Type == "auth_time" || claim.Type == "amr");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeUsersStoreAdapter : IUsersStoreAdapter
    {
        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "operations_admin" });

        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct) =>
            Task.FromResult("operations_admin");
    }

    private sealed class ThrowingUsersStoreAdapter : IUsersStoreAdapter
    {
        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct) =>
            throw new InvalidOperationException("OIDC sessions do not query user-management");

        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct) =>
            throw new InvalidOperationException("OIDC sessions do not query user-management");
    }
}
