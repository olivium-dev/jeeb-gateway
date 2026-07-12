using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using JeebGateway.Observability;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JeebGateway.Tokens;

/// <summary>
/// Issues short-lived JWT access tokens and opaque, rotated refresh
/// tokens. Refresh tokens are stored hashed (SHA-256), never raw, and
/// single-use — every successful refresh issues a new pair and revokes
/// the presented token.
/// </summary>
public class TokenService : ITokenService
{
    private readonly IRefreshTokenStore _store;
    private readonly IUsersStoreAdapter _users;
    private readonly TimeProvider _clock;
    private readonly JwtOptions _options;
    private readonly SigningCredentials _signingCredentials;

    public TokenService(
        IRefreshTokenStore store,
        IUsersStoreAdapter users,
        IOptions<JwtOptions> options,
        TimeProvider clock)
    {
        _store = store;
        _users = users;
        _clock = clock;
        _options = options.Value;

        var keyBytes = Encoding.UTF8.GetBytes(_options.SigningKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey must be at least 32 bytes (256 bits) for HMAC-SHA256.");
        }
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
    }

    public async Task<TokenPair> IssueAsync(string userId, IEnumerable<string> roles, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var accessExpires = now.AddMinutes(_options.AccessTokenMinutes);
        var refreshExpires = now.AddDays(_options.RefreshTokenDays);

        var activeRole = await _users.GetActiveRoleAsync(userId, ct);
        var access = BuildAccessToken(userId, roles, activeRole, now, accessExpires);
        var (refreshRaw, refreshRecord) = NewRefreshToken(userId, now, refreshExpires);
        await _store.AddAsync(refreshRecord, ct);

        return new TokenPair
        {
            AccessToken = access,
            RefreshToken = refreshRaw,
            AccessTokenExpiresAt = accessExpires,
            RefreshTokenExpiresAt = refreshExpires
        };
    }

    public async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return new RefreshResult { Outcome = RefreshOutcome.NotFound };
        }

        var hash = HashToken(refreshToken);
        var existing = await _store.FindByHashAsync(hash, ct);
        if (existing is null)
        {
            return new RefreshResult { Outcome = RefreshOutcome.NotFound };
        }

        var now = _clock.GetUtcNow();

        // Reuse of an already-rotated token signals theft → burn the chain.
        if (existing.RevokedAt is not null)
        {
            if (existing.ReplacedByTokenId is not null)
            {
                await _store.RevokeChainAsync(existing.TokenId, RevocationReason.ReuseDetected, ct);
                BusinessOutcomeTelemetry.RefreshReuseDetected.Add(1);
                return new RefreshResult { Outcome = RefreshOutcome.ReuseDetected };
            }
            return new RefreshResult { Outcome = RefreshOutcome.Revoked };
        }

        if (existing.ExpiresAt <= now)
        {
            return new RefreshResult { Outcome = RefreshOutcome.Expired };
        }

        var accessExpires = now.AddMinutes(_options.AccessTokenMinutes);
        var refreshExpires = now.AddDays(_options.RefreshTokenDays);

        var roles = await _users.GetRolesAsync(existing.UserId, ct);
        var activeRole = await _users.GetActiveRoleAsync(existing.UserId, ct);
        var access = BuildAccessToken(existing.UserId, roles, activeRole, now, accessExpires);
        var (refreshRaw, replacement) = NewRefreshToken(existing.UserId, now, refreshExpires);

        var rotated = await _store.RotateAsync(existing.TokenId, replacement, ct);
        if (!rotated)
        {
            // Lost the race: another caller rotated this token between our load
            // (RevokedAt was null above) and our RotateAsync.
            //
            // JEBV4-260 — bounded rotation grace window. Distinguish a BENIGN
            // concurrent double-refresh (a client that does not single-flight;
            // queued duplicate refresh calls after an access-token expiry) from
            // genuine stale-token reuse/theft. Re-read the presented token's
            // CURRENT state: if it was rotated normally (RevocationReason.Rotated)
            // within RefreshRotationGraceSeconds, treat the loser's request as a
            // benign no-op and do NOT burn the family — the concurrent winner's
            // freshly-issued token stays valid, so the session is preserved
            // instead of being silently logged out on its next refresh.
            //
            // Safety: true stale-token replay is already caught earlier (the
            // RevokedAt-set-at-load path returns ReuseDetected before we get
            // here), so this window does NOT weaken detection of a replayed spent
            // token. It only softens the extremely narrow "thief races the
            // legitimate holder inside the rotation window" case — the standard,
            // accepted OAuth rotation-leeway trade-off. Any rotation older than
            // the window, or revoked for a non-rotation reason (theft/logout),
            // still burns the chain. The comparison uses wall-clock (UtcNow)
            // because the store stamps RevokedAt with UtcNow, keeping both sides
            // in one clock domain.
            var graceSeconds = _options.RefreshRotationGraceSeconds;
            if (graceSeconds > 0)
            {
                var current = await _store.FindByHashAsync(hash, ct);
                if (current?.RevokedAt is not null
                    && string.Equals(current.RevokedReason, RevocationReason.Rotated.ToString(), StringComparison.Ordinal)
                    && (DateTimeOffset.UtcNow - current.RevokedAt.Value) <= TimeSpan.FromSeconds(graceSeconds))
                {
                    BusinessOutcomeTelemetry.RefreshConcurrentGraceAccepted.Add(1);
                    // Benign duplicate: the winner already delivered a fresh pair
                    // to the client; this queued duplicate simply fails soft
                    // (401) without destroying the winner's session.
                    return new RefreshResult { Outcome = RefreshOutcome.Revoked };
                }
            }

            // Outside the grace window (or grace disabled) → treat as reuse.
            await _store.RevokeChainAsync(existing.TokenId, RevocationReason.ReuseDetected, ct);
            BusinessOutcomeTelemetry.RefreshReuseDetected.Add(1);
            return new RefreshResult { Outcome = RefreshOutcome.ReuseDetected };
        }

        return new RefreshResult
        {
            Outcome = RefreshOutcome.Ok,
            Tokens = new TokenPair
            {
                AccessToken = access,
                RefreshToken = refreshRaw,
                AccessTokenExpiresAt = accessExpires,
                RefreshTokenExpiresAt = refreshExpires
            }
        };
    }

    public async Task RevokeAsync(string refreshToken, RevocationReason reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        var existing = await _store.FindByHashAsync(HashToken(refreshToken), ct);
        if (existing is null) return;
        await _store.RevokeAsync(existing.TokenId, reason, ct);
    }

    public Task<int> RevokeAllForUserAsync(string userId, RevocationReason reason, CancellationToken ct) =>
        _store.RevokeAllForUserAsync(userId, reason, ct);

    private string BuildAccessToken(string userId, IEnumerable<string> roles, string activeRole, DateTimeOffset now, DateTimeOffset expires)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("active_role", activeRole)
        };
        foreach (var r in roles.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            claims.Add(new Claim("roles", r));
        }

        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private (string raw, RefreshToken record) NewRefreshToken(string userId, DateTimeOffset now, DateTimeOffset expires)
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        var raw = Base64UrlEncode(buffer);
        var record = new RefreshToken
        {
            TokenId = Guid.NewGuid().ToString(),
            UserId = userId,
            TokenHash = HashToken(raw),
            IssuedAt = now,
            ExpiresAt = expires
        };
        return (raw, record);
    }

    internal static string HashToken(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

/// <summary>
/// Indirection so TokenService does not depend on JeebGateway.Users
/// directly — keeps the tokens module free to be lifted into a shared
/// library later. The default adapter pulls roles from IUsersStore.
/// </summary>
public interface IUsersStoreAdapter
{
    Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct);

    /// <summary>
    /// T-backend-041. Returns the user's persisted active role for embedding
    /// in the JWT "active_role" claim. Falls back to <see cref="Users.Roles.Client"/>
    /// when the user does not exist yet.
    /// </summary>
    Task<string> GetActiveRoleAsync(string userId, CancellationToken ct);
}
