using System.IdentityModel.Tokens.Jwt;
using System.Text;
using FluentAssertions;
using JeebGateway.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests.Tokens;

/// <summary>
/// JEB-1480 (T-BE-001 / AC5) — JWT algorithm reconciliation.
///
/// The original JEB-37 acceptance criterion mandated <b>HS512</b>, but the live
/// <see cref="TokenService"/> mints — and the entire fleet validates — access
/// tokens with <b>HS256</b> over the shared <c>Jwt:SigningKey</c>. Flipping the
/// live signer to HS512 would invalidate every token already in flight from the
/// HS256 path: a breaking change to the product/session contract (GR1).
///
/// JEB-1480 therefore RECONCILES the mandate to the live HS256 (an explicit,
/// non-breaking decision) and keeps the ≥32-byte key requirement. These tests are
/// the explicit non-breaking AC that locks that decision:
///   * a minted access token's <c>alg</c> header is exactly HS256;
///   * the token validates against the same symmetric key (HS256), proving an
///     existing HS256 consumer keeps working (no deprecation window needed);
///   * a sub-32-byte key is rejected at construction (the key-strength floor).
/// </summary>
public class JwtAlgorithmReconciliationTests
{
    private const string Key = "reconciliation-test-signing-key-at-least-32-bytes-long!!";

    [Fact]
    public async Task AccessToken_IsSignedWith_HS256()
    {
        var svc = NewService(Key);

        var pair = await svc.IssueAsync("user-1", new[] { "customer" }, CancellationToken.None);

        var header = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken).Header;
        header.Alg.Should().Be(SecurityAlgorithms.HmacSha256,
            "the live TokenService mints HS256 — JEB-1480 reconciles the HS512 mandate to the live algorithm");
    }

    [Fact]
    public async Task AccessToken_Validates_Against_The_Same_HS256_Key()
    {
        var svc = NewService(Key);
        var pair = await svc.IssueAsync("user-1", new[] { "customer" }, CancellationToken.None);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "jeeb-gateway",
            ValidateAudience = true,
            ValidAudience = "jeeb-clients",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
            ValidateLifetime = false,
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
        };

        var act = () => new JwtSecurityTokenHandler()
            .ValidateToken(pair.AccessToken, parameters, out _);

        act.Should().NotThrow("an existing HS256 consumer must keep validating gateway tokens (non-breaking, GR1)");
    }

    [Fact]
    public void Constructing_With_A_SubThirtyTwoByte_Key_Is_Rejected()
    {
        var act = () => NewService("too-short");

        act.Should().Throw<InvalidOperationException>(
            "the signing key floor (≥32 bytes for HMAC-SHA256) is preserved by the reconciliation");
    }

    private static TokenService NewService(string signingKey)
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = "jeeb-gateway",
            Audience = "jeeb-clients",
            SigningKey = signingKey,
            AccessTokenMinutes = 60,
            RefreshTokenDays = 30,
        });

        return new TokenService(
            new InMemoryRefreshTokenStore(),
            new FakeUsersStoreAdapter(),
            options,
            TimeProvider.System);
    }

    private sealed class FakeUsersStoreAdapter : IUsersStoreAdapter
    {
        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "customer" });

        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct)
            => Task.FromResult("customer");
    }
}
