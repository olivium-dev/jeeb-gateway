using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// AC5 + AC5b — issues the JWT pair returned by <c>POST /v1/auth/otp/verify</c>
/// and rotates it on <c>POST /v1/auth/refresh</c>.
///
/// Locked by audit #14764:
///   <list type="bullet">
///     <item>Signature: HS512 (NOT HS256).</item>
///     <item>Access TTL: 1 hour (3600 s).</item>
///     <item>Refresh TTL: 30 days (2_592_000 s).</item>
///     <item>Refresh-token-family rotation; detected reuse revokes the family.</item>
///   </list>
///
/// Refresh tokens are JWTs (not opaque strings) so the mobile client can
/// inspect <c>exp</c> without an extra introspection round-trip. The
/// gateway-side family bookkeeping is kept in
/// <see cref="IRefreshTokenFamilyStore"/> — JWT validity is necessary but NOT
/// sufficient; <c>/v1/auth/refresh</c> ALSO verifies the JTI is still active
/// on the gateway side, defeating stolen refresh tokens once any member of
/// the family is reused.
/// </summary>
public interface IJeebJwtIssuer
{
    JeebTokenPair Issue(Guid userId, string activeRole, string[] availableRoles, string phoneHash);

    /// <summary>
    /// Validate a refresh JWT against the family store. Returns the new pair on success,
    /// <see cref="RefreshOutcome.RevokedOrInvalid"/> if the token is unknown, expired, or
    /// revoked, and <see cref="RefreshOutcome.ReuseDetected"/> if the token has already
    /// been rotated — in which case the whole family is revoked as a side effect.
    /// </summary>
    Task<RefreshOutcomeResult> RefreshAsync(string refreshToken, CancellationToken ct);
}

public sealed record JeebTokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid UserId,
    string ActiveRole,
    string[] AvailableRoles);

public enum RefreshOutcome
{
    Ok,
    RevokedOrInvalid,
    ReuseDetected,
}

public sealed record RefreshOutcomeResult(RefreshOutcome Outcome, JeebTokenPair? Pair);

public sealed class JeebJwtIssuer : IJeebJwtIssuer
{
    private static readonly JwtSecurityTokenHandler Handler = new() { MapInboundClaims = false };

    private readonly JeebJwtOptions _options;
    private readonly IRefreshTokenFamilyStore _store;
    private readonly TimeProvider _clock;
    private readonly SigningCredentials _signing;

    public JeebJwtIssuer(
        IOptions<JeebJwtOptions> options,
        IRefreshTokenFamilyStore store,
        TimeProvider clock)
    {
        _options = options.Value;
        _store   = store;
        _clock   = clock;

        var keyBytes = Encoding.UTF8.GetBytes(_options.SigningKey);
        // HMACSHA512 requires the key to be at least the hash output size
        // (512 bits / 64 bytes). The data-annotation [MinLength(32)] is the
        // baseline that AC5 calls out; HS512 imposes the stricter 64-byte
        // floor — we fail fast at startup with a precise message.
        if (keyBytes.Length < 64)
        {
            throw new InvalidOperationException(
                "JeebJwt:SigningKey must be at least 64 bytes (512 bits) for HS512 " +
                "(see Microsoft.IdentityModel.Tokens.CryptoProviderFactory.ValidateKeySize). " +
                "AC5: load from env / sealed secret, not from appsettings.json.");
        }

        // AC5: HS512 — NOT HS256. Audit comment #14764 locked HS512.
        _signing = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes),
            SecurityAlgorithms.HmacSha512);
    }

    public JeebTokenPair Issue(Guid userId, string activeRole, string[] availableRoles, string phoneHash)
    {
        var familyId = Guid.NewGuid();
        return MintPair(userId, activeRole, availableRoles, phoneHash, familyId);
    }

    public async Task<RefreshOutcomeResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return new RefreshOutcomeResult(RefreshOutcome.RevokedOrInvalid, null);
        }

        // Step 1 — validate signature + lifetime against the same parameters
        // we issue with. Failure (bad signature, expired, etc.) collapses to
        // RevokedOrInvalid; the caller maps that to 401.
        ClaimsPrincipal principal;
        SecurityToken validated;
        try
        {
            // We MUST validate lifetime against the injected TimeProvider, not
            // DateTime.UtcNow — otherwise integration tests with FakeTimeProvider
            // (set to a 2026 epoch advancing forward) would have tokens issued
            // at FakeNow rejected against the real system clock.
            var now = _clock.GetUtcNow();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidIssuer              = _options.Issuer,
                ValidateAudience         = true,
                ValidAudience            = _options.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = _signing.Key,
                ValidateLifetime         = true,
                ValidAlgorithms          = new[] { SecurityAlgorithms.HmacSha512 },
                ClockSkew                = TimeSpan.FromSeconds(30),
                NameClaimType            = JwtRegisteredClaimNames.Sub,
                LifetimeValidator        = (notBefore, expires, _, _) =>
                {
                    var skew = TimeSpan.FromSeconds(30);
                    if (notBefore.HasValue && now.UtcDateTime + skew < notBefore.Value) return false;
                    if (expires.HasValue   && now.UtcDateTime - skew > expires.Value)   return false;
                    return true;
                },
            };
            principal = Handler.ValidateToken(refreshToken, parameters, out validated);
        }
        catch (Exception)
        {
            return new RefreshOutcomeResult(RefreshOutcome.RevokedOrInvalid, null);
        }

        if (validated is not JwtSecurityToken jwt)
        {
            return new RefreshOutcomeResult(RefreshOutcome.RevokedOrInvalid, null);
        }

        // The refresh token type marker — defends against confused-deputy: a
        // stolen access token must NOT be acceptable as a refresh token.
        if (!string.Equals(jwt.Header["typ"]?.ToString(), "jeeb+refresh", StringComparison.Ordinal)
            && !string.Equals(GetClaim(jwt, "token_use"), "refresh", StringComparison.Ordinal))
        {
            return new RefreshOutcomeResult(RefreshOutcome.RevokedOrInvalid, null);
        }

        var jtiStr = GetClaim(jwt, JwtRegisteredClaimNames.Jti);
        var familyStr = GetClaim(jwt, "family");
        var userStr = GetClaim(jwt, JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(jtiStr, out var jti)
            || !Guid.TryParse(familyStr, out var familyId)
            || !Guid.TryParse(userStr, out var userId))
        {
            return new RefreshOutcomeResult(RefreshOutcome.RevokedOrInvalid, null);
        }

        // Step 2 — atomic rotate on the family store. Three outcomes:
        //   Rotated        → mint a new pair under the SAME family id.
        //   Reused         → revoke the family entirely; return ReuseDetected.
        //   FamilyRevoked  → return RevokedOrInvalid (legit holder lost the race).
        var rotation = await _store.TryRotateAsync(familyId, jti, _clock.GetUtcNow(), ct);

        if (rotation == RotateOutcome.Reused)
        {
            await _store.RevokeFamilyAsync(familyId, ct);
            return new RefreshOutcomeResult(RefreshOutcome.ReuseDetected, null);
        }
        if (rotation != RotateOutcome.Rotated)
        {
            return new RefreshOutcomeResult(RefreshOutcome.RevokedOrInvalid, null);
        }

        // Carry the original claims into the new pair so the downstream
        // active_role/available_roles do not flicker across a refresh.
        var activeRole      = GetClaim(jwt, "active_role")           ?? "customer";
        var availableRoles  = GetClaim(jwt, "available_roles")?.Split(',') ?? new[] { activeRole };
        var phoneHash       = GetClaim(jwt, "phone_hash")            ?? string.Empty;

        var pair = MintPair(userId, activeRole, availableRoles, phoneHash, familyId);
        return new RefreshOutcomeResult(RefreshOutcome.Ok, pair);
    }

    private JeebTokenPair MintPair(
        Guid userId, string activeRole, string[] availableRoles, string phoneHash, Guid familyId)
    {
        var now             = _clock.GetUtcNow();
        var accessExpires   = now.AddSeconds(_options.AccessTtlSeconds);
        var refreshExpires  = now.AddSeconds(_options.RefreshTtlSeconds);
        var accessJti       = Guid.NewGuid();
        var refreshJti      = Guid.NewGuid();

        var accessToken  = BuildJwt(userId, activeRole, availableRoles, phoneHash, familyId,
                                    accessJti, "access",  now, accessExpires,  typ: "jeeb+access");
        var refreshToken = BuildJwt(userId, activeRole, availableRoles, phoneHash, familyId,
                                    refreshJti, "refresh", now, refreshExpires, typ: "jeeb+refresh");

        _store.RegisterIssued(familyId, refreshJti, userId, refreshExpires);

        return new JeebTokenPair(
            AccessToken:           accessToken,
            RefreshToken:          refreshToken,
            AccessTokenExpiresAt:  accessExpires,
            RefreshTokenExpiresAt: refreshExpires,
            UserId:                userId,
            ActiveRole:            activeRole,
            AvailableRoles:        availableRoles);
    }

    private string BuildJwt(
        Guid userId,
        string activeRole,
        string[] availableRoles,
        string phoneHash,
        Guid familyId,
        Guid jti,
        string tokenUse,
        DateTimeOffset issuedAt,
        DateTimeOffset expires,
        string typ)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, jti.ToString()),
            new(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new("token_use", tokenUse),
            new("family", familyId.ToString()),
            new("active_role", activeRole),
            new("available_roles", string.Join(',', availableRoles)),
            new("phone_hash", phoneHash),
        };

        var header = new JwtHeader(_signing) { ["typ"] = typ };
        var payload = new JwtPayload(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: issuedAt.UtcDateTime,
            expires: expires.UtcDateTime,
            issuedAt: issuedAt.UtcDateTime);

        return Handler.WriteToken(new JwtSecurityToken(header, payload));
    }

    private static string? GetClaim(JwtSecurityToken jwt, string type) =>
        jwt.Claims.FirstOrDefault(c => string.Equals(c.Type, type, StringComparison.Ordinal))?.Value;
}
