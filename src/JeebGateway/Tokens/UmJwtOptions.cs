namespace JeebGateway.Tokens;

/// <summary>
/// Trust configuration for user-management (UM) re-issued access tokens (H-B5,
/// S02 ADR-001-rev3 token-authority design). After a role switch UM re-mints the
/// caller's JWT with <c>iss=user-management</c> / <c>aud=user-management</c> (see
/// user-management <c>UserAuthRepository.GenerateAuthToken</c>). The gateway must
/// ACCEPT and FULLY VALIDATE that token on protected routes — signature + iss +
/// aud + exp — alongside its own <c>iss=jeeb-gateway</c> tokens.
///
/// This is wired as a SECOND, named JwtBearer scheme keyed on the <c>iss</c> claim
/// (NOT a widened ValidIssuers/multi-key single scheme), so each issuer is pinned
/// to exactly one signing key and one (iss,aud) pair — closing the key-confusion
/// hole the H-B5 security review flagged.
///
/// SECURITY (sec-1 / known-debt #6): the HS256 key the gateway trusts for UM MUST
/// come from a secret, never a committed literal. Today UM and the gateway derive
/// HS256 from the SAME fleet secret (<c>JEEB_JWT_SIGNING_KEY</c>), so when
/// <see cref="SigningKey"/> is unset the gateway falls back to the gateway's own
/// <c>Jwt:SigningKey</c> at composition time. Supplying a distinct
/// <c>UmJwt:SigningKey</c> (env <c>UmJwt__SigningKey</c>) lets UM rotate OFF the
/// leaked fleet key — or move to RS256/JWKS — with NO gateway code change.
///
/// OPTIONAL / NO FAIL-CLOSED (g03): an absent <c>UmJwt</c> section never crashes
/// boot. The defaults below + the gateway-key fallback keep the UM scheme valid,
/// and the policy scheme still routes <c>iss=jeeb-gateway</c> tokens unchanged.
/// </summary>
public class UmJwtOptions
{
    public const string SectionName = "UmJwt";

    /// <summary>The issuer UM stamps on re-issued tokens (UM <c>Jwt:Issuer</c>).</summary>
    public string Issuer { get; set; } = "user-management";

    /// <summary>
    /// The audience UM stamps on re-issued tokens. UM signs with
    /// <c>audience: _configuration["Jwt:Issuer"]</c>, so aud == iss == the UM
    /// issuer value. Kept as a distinct setting so a future UM aud split is a
    /// config change, not a code change.
    /// </summary>
    public string Audience { get; set; } = "user-management";

    /// <summary>
    /// HMAC-SHA256 key the gateway uses to VERIFY UM token signatures. Empty by
    /// default; when empty the composition falls back to the gateway's own
    /// <c>Jwt:SigningKey</c> (operationally the same fleet secret today). MUST be
    /// supplied from a secret in production — never a committed literal in the
    /// gateway. Rotating this to a UM-dedicated key removes the over-trust on the
    /// leaked fleet key.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;
}
