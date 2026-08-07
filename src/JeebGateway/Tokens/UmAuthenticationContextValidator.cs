using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JeebGateway.Tokens;

public interface IUmAuthenticationContextValidator
{
    VerifiedAuthenticationContext? Validate(string? accessToken);
}

/// <summary>
/// Extracts MFA ceremony claims only after validating the user-management JWT
/// with the same issuer/audience/signing-key boundary as the runtime bearer
/// scheme. Missing or unverified claims are intentionally not synthesized.
/// </summary>
public sealed class UmAuthenticationContextValidator : IUmAuthenticationContextValidator
{
    private readonly TokenValidationParameters _validation;
    private readonly TimeProvider _clock;

    public UmAuthenticationContextValidator(
        IOptions<UmJwtOptions> umOptions,
        IOptions<JwtOptions> gatewayOptions,
        TimeProvider clock)
    {
        var um = umOptions.Value;
        var gateway = gatewayOptions.Value;
        var key = string.IsNullOrWhiteSpace(um.SigningKey) ? gateway.SigningKey : um.SigningKey;
        _clock = clock;
        _validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = um.Issuer,
            ValidateAudience = true,
            ValidAudience = um.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "roles",
        };
    }

    public VerifiedAuthenticationContext? Validate(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(accessToken, _validation, out var validated);
            if (validated is not JwtSecurityToken jwt
                || !string.Equals(jwt.Header.Alg, SecurityAlgorithms.HmacSha256, StringComparison.Ordinal))
                return null;

            var rawAuthTime = principal.FindFirst("auth_time")?.Value;
            if (!long.TryParse(rawAuthTime, NumberStyles.None, CultureInfo.InvariantCulture, out var authTime))
                return null;
            var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(authTime);
            if (authenticatedAt > _clock.GetUtcNow().AddSeconds(30)) return null;

            var methods = principal.FindAll("amr")
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(static method => method.Length <= 64)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return methods.Length == 0 ? null : new VerifiedAuthenticationContext(authTime, methods);
        }
        catch (Exception error) when (error is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }
}
