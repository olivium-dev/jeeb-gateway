using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using JeebGateway.Auth.Oidc;
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
    internal const string RuntimeSessionExpiryClaim = "jeeb_runtime_exp";
    internal const string RuntimeRefreshHashClaim = "jeeb_runtime_refresh_hash";
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
        var activeRole = await _users.GetActiveRoleAsync(userId, ct);
        return await IssueAsync(userId, roles, activeRole, null, ct);
    }

    public Task<TokenPair> IssueAsync(
        string userId,
        IEnumerable<string> roles,
        string activeRole,
        VerifiedAuthenticationContext? authentication,
        CancellationToken ct) =>
        IssueCoreAsync(userId, roles, activeRole, authentication, null, ct);

    public Task<TokenPair> IssueBoundedAsync(
        string userId,
        IEnumerable<string> roles,
        string activeRole,
        DateTimeOffset absoluteSessionExpiresAt,
        CancellationToken ct) =>
        IssueCoreAsync(
            userId,
            roles,
            activeRole,
            null,
            absoluteSessionExpiresAt,
            ct);

    private async Task<TokenPair> IssueCoreAsync(
        string userId,
        IEnumerable<string> roles,
        string activeRole,
        VerifiedAuthenticationContext? authentication,
        DateTimeOffset? absoluteSessionExpiresAt,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var normalizedRoles = roles
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (authentication?.PersistRoleContext == true)
            ValidateExternalAuthenticationContext(
                authentication, normalizedRoles, activeRole, now);

        var sessionDeadline =
            authentication?.SessionExpiresAt ?? absoluteSessionExpiresAt;
        var accessExpires = BoundExpiry(
            now.AddMinutes(_options.AccessTokenMinutes), sessionDeadline);
        var refreshExpires = BoundExpiry(
            now.AddDays(_options.RefreshTokenDays), sessionDeadline);
        if (accessExpires <= now || refreshExpires <= now)
            throw new InvalidOperationException("The verified authentication session has expired.");
        if (absoluteSessionExpiresAt is not null
            && await _store.IsBoundedSessionRevokedAsync(userId, ct))
            throw new InvalidOperationException("The bounded runtime session has been revoked.");

        var (refreshRaw, refreshRecord) = NewRefreshToken(
            userId,
            now,
            refreshExpires,
            authentication,
            normalizedRoles,
            activeRole,
            absoluteSessionExpiresAt);
        var access = BuildAccessToken(
            userId, normalizedRoles, activeRole, authentication,
            absoluteSessionExpiresAt,
            absoluteSessionExpiresAt is null ? null : refreshRecord.TokenHash,
            now, accessExpires);
        await _store.AddAsync(refreshRecord, ct);

        return new TokenPair
        {
            AccessToken = access,
            RefreshToken = refreshRaw,
            AccessTokenExpiresAt = accessExpires,
            RefreshTokenExpiresAt = refreshExpires
        };
    }

    public Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct) =>
        RefreshCoreAsync(refreshToken, null, ct);

    public Task<RefreshResult> RefreshAsync(
        string refreshToken,
        Func<string, CancellationToken, Task<TokenRoleContext?>> roleResolver,
        CancellationToken ct) => RefreshCoreAsync(refreshToken, roleResolver, ct);

    private async Task<RefreshResult> RefreshCoreAsync(
        string refreshToken,
        Func<string, CancellationToken, Task<TokenRoleContext?>>? roleResolver,
        CancellationToken ct)
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

        // Retire sessions minted by the historical fail-open OTP fallback before
        // role resolution, rotation, or either token-mint path can run. Revocation
        // is idempotent and best-effort; rejection remains fail-closed on a store
        // fault so a legacy phone subject can never regain a session.
        if (JeebGateway.Auth.LegacyPhoneSessionRejection.IsLegacySubject(existing.UserId))
        {
            BusinessOutcomeTelemetry.RecordLegacySessionRejection(
                LegacySessionRejectionReason.RefreshTokenLegacySubject);
            await JeebGateway.Auth.LegacyPhoneSessionRejection.RevokeRefreshFamiliesAsync(
                _store, existing.UserId, ct);
            return new RefreshResult { Outcome = RefreshOutcome.Revoked };
        }

        var now = _clock.GetUtcNow();

        if (existing.AbsoluteSessionExpiresAt is not null
            && await _store.IsBoundedSessionRevokedAsync(existing.UserId, ct))
            return new RefreshResult { Outcome = RefreshOutcome.Revoked };

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

        if (existing.AuthenticationSessionExpiresAt is not null
            && existing.AuthenticationSessionExpiresAt <= now)
            return new RefreshResult { Outcome = RefreshOutcome.AuthenticationExpired };
        if (existing.AbsoluteSessionExpiresAt is not null
            && existing.AbsoluteSessionExpiresAt <= now)
            return new RefreshResult { Outcome = RefreshOutcome.AuthenticationExpired };

        if (existing.ExpiresAt <= now)
        {
            return new RefreshResult { Outcome = RefreshOutcome.Expired };
        }

        TokenRoleContext roleContext;
        VerifiedAuthenticationContext? persistedAuthentication;
        if (existing.AbsoluteSessionExpiresAt is not null)
        {
            if (!TryBoundedRuntimeSession(existing, now, out roleContext))
                return new RefreshResult { Outcome = RefreshOutcome.AuthenticationExpired };
            persistedAuthentication = null;
        }
        else if (HasExternalSessionFields(existing))
        {
            if (!TryExternalSession(
                    existing, now, out roleContext, out persistedAuthentication))
                return new RefreshResult { Outcome = RefreshOutcome.AuthenticationExpired };
        }
        else
        {
            if (roleResolver is null)
            {
                var roles = await _users.GetRolesAsync(existing.UserId, ct);
                var activeRole = await _users.GetActiveRoleAsync(existing.UserId, ct);
                roleContext = new TokenRoleContext(roles, activeRole);
            }
            else
            {
                var resolved = await roleResolver(existing.UserId, ct);
                if (resolved is null)
                    return new RefreshResult { Outcome = RefreshOutcome.RoleResolutionFailed };
                roleContext = resolved;
            }
            persistedAuthentication = AuthenticationFrom(existing);
        }

        // External records reach this point only with the complete, verified
        // provider+roles+active-role+methods+auth-time+deadline tuple. A partial
        // record never receives the ordinary 30-day fallback below.
        var sessionDeadline =
            existing.AuthenticationSessionExpiresAt ?? existing.AbsoluteSessionExpiresAt;
        var accessExpires = BoundExpiry(
            now.AddMinutes(_options.AccessTokenMinutes), sessionDeadline);
        var refreshExpires = BoundExpiry(
            now.AddDays(_options.RefreshTokenDays), sessionDeadline);
        var (refreshRaw, replacement) = NewRefreshToken(
            existing.UserId, now, refreshExpires, persistedAuthentication,
            roleContext.Roles, roleContext.ActiveRole,
            existing.AbsoluteSessionExpiresAt);
        var access = BuildAccessToken(
            existing.UserId, roleContext.Roles, roleContext.ActiveRole,
            persistedAuthentication, existing.AbsoluteSessionExpiresAt,
            existing.AbsoluteSessionExpiresAt is null ? null : replacement.TokenHash,
            now, accessExpires);

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

    public async Task<int> RevokeBoundedSessionForUserAsync(
        string userId,
        RevocationReason reason,
        CancellationToken ct)
    {
        await _store.MarkBoundedSessionRevokedAsync(userId, ct);
        return await _store.RevokeAllForUserAsync(userId, reason, ct);
    }

    private string BuildAccessToken(
        string userId,
        IEnumerable<string> roles,
        string activeRole,
        VerifiedAuthenticationContext? authentication,
        DateTimeOffset? absoluteSessionExpiresAt,
        string? runtimeRefreshHash,
        DateTimeOffset now,
        DateTimeOffset expires)
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
        if (absoluteSessionExpiresAt is { } runtimeDeadline)
        {
            claims.Add(new Claim(
                RuntimeSessionExpiryClaim,
                runtimeDeadline.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64));
            if (string.IsNullOrWhiteSpace(runtimeRefreshHash))
                throw new InvalidOperationException(
                    "A bounded runtime access token requires a refresh-record binding.");
            claims.Add(new Claim(RuntimeRefreshHashClaim, runtimeRefreshHash));
        }
        if (authentication is not null)
        {
            claims.Add(new Claim("auth_time", authentication.AuthTime.ToString(), ClaimValueTypes.Integer64));
            foreach (var method in authentication.Methods
                         .Where(static method => !string.IsNullOrWhiteSpace(method))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                claims.Add(new Claim("amr", method));
            if (!string.IsNullOrWhiteSpace(authentication.Provider))
                claims.Add(new Claim("idp", authentication.Provider));
            if (authentication.PersistRoleContext)
                claims.Add(new Claim(
                    ExternalAdminSessionRequirement.SessionClaim,
                    ExternalAdminSessionRequirement.SessionClaimValue));
            if (!string.IsNullOrWhiteSpace(authentication.DisplayName))
                claims.Add(new Claim("name", authentication.DisplayName));
            if (!string.IsNullOrWhiteSpace(authentication.Email))
                claims.Add(new Claim("email", authentication.Email));
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

    private (string raw, RefreshToken record) NewRefreshToken(
        string userId,
        DateTimeOffset now,
        DateTimeOffset expires,
        VerifiedAuthenticationContext? authentication,
        IReadOnlyList<string> roles,
        string activeRole,
        DateTimeOffset? absoluteSessionExpiresAt = null)
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
            ExpiresAt = expires,
            AuthenticationTime = authentication?.AuthTime,
            AuthenticationMethods = authentication?.Methods
                .Where(static method => !string.IsNullOrWhiteSpace(method) && method.Length <= 64)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray(),
            IdentityProvider = authentication?.Provider,
            AuthenticationSessionExpiresAt = authentication?.SessionExpiresAt,
            AbsoluteSessionExpiresAt = absoluteSessionExpiresAt,
            DisplayName = authentication?.DisplayName,
            Email = authentication?.Email,
            RoleSnapshot = authentication?.PersistRoleContext == true
                || absoluteSessionExpiresAt is not null
                ? roles.Where(static role => !string.IsNullOrWhiteSpace(role))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(16)
                    .ToArray()
                : null,
            ActiveRoleSnapshot = authentication?.PersistRoleContext == true
                || absoluteSessionExpiresAt is not null
                ? activeRole
                : null,
        };
        return (raw, record);
    }

    private static VerifiedAuthenticationContext? AuthenticationFrom(RefreshToken token)
    {
        if (token.AuthenticationTime is null
            || token.AuthenticationMethods is not { Count: > 0 })
            return null;
        var methods = token.AuthenticationMethods
            .Where(static method => !string.IsNullOrWhiteSpace(method) && method.Length <= 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return methods.Length == 0
            ? null
            : new VerifiedAuthenticationContext(
                token.AuthenticationTime.Value,
                methods,
                token.IdentityProvider,
                token.AuthenticationSessionExpiresAt,
                token.DisplayName,
                token.Email,
                token.RoleSnapshot is { Count: > 0 });
    }

    private static bool HasExternalSessionFields(RefreshToken token) =>
        token.IdentityProvider is not null
        || token.AuthenticationSessionExpiresAt is not null
        || token.RoleSnapshot is not null
        || token.ActiveRoleSnapshot is not null;

    private static bool TryBoundedRuntimeSession(
        RefreshToken token,
        DateTimeOffset now,
        out TokenRoleContext roleContext)
    {
        roleContext = null!;
        var rawRoles = token.RoleSnapshot ?? [];
        var roles = rawRoles
            .Where(static role => !string.IsNullOrWhiteSpace(role) && role.Length <= 128)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(17)
            .ToArray();
        if (token.AbsoluteSessionExpiresAt is null
            || token.AbsoluteSessionExpiresAt <= now
            || rawRoles.Count != roles.Length
            || roles.Length is 0 or > 16
            || string.IsNullOrWhiteSpace(token.ActiveRoleSnapshot)
            || token.ActiveRoleSnapshot.Length > 128
            || !roles.Contains(token.ActiveRoleSnapshot, StringComparer.OrdinalIgnoreCase))
            return false;

        roleContext = new TokenRoleContext(roles, token.ActiveRoleSnapshot);
        return true;
    }

    private static bool TryExternalSession(
        RefreshToken token,
        DateTimeOffset now,
        out TokenRoleContext roleContext,
        out VerifiedAuthenticationContext? authentication)
    {
        roleContext = null!;
        authentication = null;

        var rawRoles = token.RoleSnapshot ?? [];
        var roles = rawRoles
            .Where(static role => !string.IsNullOrWhiteSpace(role) && role.Length <= 128)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(17)
            .ToArray();
        var rawMethods = token.AuthenticationMethods ?? [];
        var methods = rawMethods
            .Where(static method => !string.IsNullOrWhiteSpace(method) && method.Length <= 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(9)
            .ToArray();
        if (string.IsNullOrWhiteSpace(token.IdentityProvider)
            || token.IdentityProvider.Length > 2_048
            || token.AuthenticationTime is null or <= 0
            || token.AuthenticationTime > now.AddSeconds(30).ToUnixTimeSeconds()
            || token.AuthenticationSessionExpiresAt is null
            || token.AuthenticationSessionExpiresAt <= now
            || rawRoles.Count != roles.Length
            || roles.Length is 0 or > 16
            || string.IsNullOrWhiteSpace(token.ActiveRoleSnapshot)
            || token.ActiveRoleSnapshot.Length > 128
            || !roles.Contains(token.ActiveRoleSnapshot, StringComparer.OrdinalIgnoreCase)
            || rawMethods.Count != methods.Length
            || methods.Length is 0 or > 8
            || !methods.Contains("mfa", StringComparer.OrdinalIgnoreCase))
            return false;

        roleContext = new TokenRoleContext(roles, token.ActiveRoleSnapshot);
        authentication = new VerifiedAuthenticationContext(
            token.AuthenticationTime.Value,
            methods,
            token.IdentityProvider,
            token.AuthenticationSessionExpiresAt,
            token.DisplayName,
            token.Email,
            persistRoleContext: true);
        return true;
    }

    private static void ValidateExternalAuthenticationContext(
        VerifiedAuthenticationContext authentication,
        IReadOnlyList<string> roles,
        string activeRole,
        DateTimeOffset now)
    {
        var normalizedMethods = authentication.Methods?
            .Where(static method => !string.IsNullOrWhiteSpace(method) && method.Length <= 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (string.IsNullOrWhiteSpace(authentication.Provider)
            || authentication.Provider.Length > 2_048
            || authentication.AuthTime <= 0
            || authentication.AuthTime > now.AddSeconds(30).ToUnixTimeSeconds()
            || authentication.Methods is not { Count: > 0 }
            || authentication.Methods.Count != normalizedMethods.Length
            || normalizedMethods.Length > 8
            || !normalizedMethods.Contains("mfa", StringComparer.OrdinalIgnoreCase)
            || authentication.SessionExpiresAt is null
            || authentication.SessionExpiresAt <= now
            || roles.Count is 0 or > 16
            || string.IsNullOrWhiteSpace(activeRole)
            || !roles.Contains(activeRole, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The external operator authentication context is incomplete.");
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

    private static DateTimeOffset BoundExpiry(
        DateTimeOffset requested,
        DateTimeOffset? authenticationSessionExpiresAt) =>
        authenticationSessionExpiresAt is not null && authenticationSessionExpiresAt < requested
            ? authenticationSessionExpiresAt.Value
            : requested;
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
