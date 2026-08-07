using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.Tokens;
using Microsoft.IdentityModel.Tokens;

namespace JeebGateway.Auth.Oidc;

internal sealed record ValidatedAdminOidcIdentity(
    string UserId,
    string? DisplayName,
    string? Email,
    IReadOnlyList<string> Roles,
    VerifiedAuthenticationContext Authentication);

internal static class AdminOidcTokenValidator
{
    public static ValidatedAdminOidcIdentity Validate(
        string idToken,
        string jwksJson,
        string expectedNonce,
        AdminOidcOptions options,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 64_000)
            throw new SecurityTokenException("The OIDC id_token is missing or oversized.");

        var keySet = new JsonWebKeySet(jwksJson);
        var keys = keySet.GetSigningKeys();
        if (keys.Count == 0) throw new SecurityTokenException("The OIDC key set is empty.");

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.ClientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = options.AllowedSigningAlgorithms,
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (notBefore, expires, _, _) =>
                expires is not null
                && expires.Value >= now.UtcDateTime.Subtract(TimeSpan.FromSeconds(30))
                && (notBefore is null
                    || notBefore.Value <= now.UtcDateTime.Add(TimeSpan.FromSeconds(30))),
            NameClaimType = "name",
            RoleClaimType = "__never_trust_provider_roles",
        };

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(idToken, parameters, out var validated);
        if (validated is not JwtSecurityToken jwt
            || !options.AllowedSigningAlgorithms.Contains(jwt.Header.Alg, StringComparer.Ordinal))
            throw new SecurityTokenException("The OIDC token algorithm is not allowed.");

        var authorizedParty = principal.FindFirst("azp")?.Value;
        if (authorizedParty is not null
            && !string.Equals(authorizedParty, options.ClientId, StringComparison.Ordinal))
            throw new SecurityTokenException("The OIDC authorized party is invalid.");
        if (jwt.Audiences.Skip(1).Any())
        {
            if (authorizedParty is null)
                throw new SecurityTokenException("The OIDC authorized party is invalid.");
        }

        var nonce = principal.FindFirst("nonce")?.Value;
        if (nonce is null || !AdminOidcCorrelationProtector.FixedEquals(nonce, expectedNonce))
            throw new SecurityTokenException("The OIDC nonce is invalid.");

        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 1024)
            throw new SecurityTokenException("The OIDC subject is missing or oversized.");

        var rawAuthTime = principal.FindFirst("auth_time")?.Value;
        if (!long.TryParse(rawAuthTime, NumberStyles.None, CultureInfo.InvariantCulture, out var authTime))
            throw new SecurityTokenException("The OIDC authentication time is missing.");
        var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(authTime);
        var age = now - authenticatedAt;
        if (age < TimeSpan.FromSeconds(-30)
            || age > TimeSpan.FromMinutes(options.MaxAuthenticationAgeMinutes))
            throw new SecurityTokenException("The OIDC authentication ceremony is not fresh.");

        var methods = ClaimValues(principal, "amr").ToArray();
        if (methods.Length > 16
            || methods.Distinct(StringComparer.OrdinalIgnoreCase).Count() != methods.Length
            || methods.Any(static method =>
                method.Length is 0 or > 64 || method.Any(char.IsControl)))
            throw new SecurityTokenException("The OIDC authentication methods are invalid or oversized.");

        // MFA POLICY: never infer assurance from factor-looking AMR values or
        // from the number of values. A lone hwk/webauthn/fido claim can describe
        // one factor, and arbitrary AMR combinations are provider-specific.
        // Accept only the provider's explicit `mfa` assertion OR an exact ACR
        // value the operator deliberately configured as sufficient assurance.
        var explicitMfa = methods.Contains("mfa", StringComparer.OrdinalIgnoreCase);
        var assertedAcr = principal.FindFirst("acr")?.Value;
        var configuredAcr = options.RequiredAcrValues?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var configuredAcrAssurance = assertedAcr is not null
                                     && configuredAcr.Contains(assertedAcr, StringComparer.Ordinal);
        if (!explicitMfa && !configuredAcrAssurance)
            throw new SecurityTokenException(
                "The OIDC token does not contain an explicit MFA or configured assurance assertion.");

        var groups = ClaimValues(principal, options.GroupClaim);
        var roles = AdminOidcRoleMapper.Map(options, groups);
        if (roles.Count == 0)
            throw new SecurityTokenException("The OIDC identity has no allowed operator role.");

        var hasVerifiedEmail = bool.TryParse(
            principal.FindFirst("email_verified")?.Value,
            out var verifiedEmail) && verifiedEmail;
        var email = hasVerifiedEmail ? BoundedClaim(principal, "email", 320) : null;

        var displayName = BoundedClaim(principal, "name", 256)
                          ?? BoundedClaim(principal, "preferred_username", 256);
        var sessionExpiresAt = now.AddHours(options.OperatorSessionHours);
        // Preserve provider methods and normalize the accepted explicit-MFA or
        // configured-ACR ceremony only after the signed checks above pass.
        var gatewayMethods = methods.Contains("mfa", StringComparer.OrdinalIgnoreCase)
            ? methods
            : methods.Append("mfa").ToArray();
        var authentication = new VerifiedAuthenticationContext(
            authTime,
            gatewayMethods,
            provider: options.Issuer,
            sessionExpiresAt: sessionExpiresAt,
            displayName: displayName,
            email: email,
            persistRoleContext: true);

        return new ValidatedAdminOidcIdentity(
            StableUserId(options.Issuer, subject), displayName, email, roles, authentication);
    }

    internal static IReadOnlyList<string> ClaimValues(ClaimsPrincipal principal, string claimName)
    {
        var values = new List<string>();
        foreach (var claim in principal.FindAll(claimName))
        {
            var raw = claim.Value.Trim();
            if (raw.Length == 0) continue;
            if (raw[0] == '[')
            {
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        values.AddRange(document.RootElement.EnumerateArray()
                            .Where(static item => item.ValueKind == JsonValueKind.String)
                            .Select(static item => item.GetString()!)
                            .Where(static item => !string.IsNullOrWhiteSpace(item)));
                        continue;
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }
            values.AddRange(raw.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        return values;
    }

    private static string StableUserId(string issuer, string subject)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\n" + subject));
        return "oidc_" + Base64UrlEncoder.Encode(digest);
    }

    private static string? BoundedClaim(ClaimsPrincipal principal, string name, int maximum)
    {
        var value = principal.FindFirst(name)?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Length > maximum
               || value.Any(char.IsControl)
            ? null
            : value;
    }
}
