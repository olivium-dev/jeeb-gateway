using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using JeebGateway.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JeebGateway.Conversations.Realtime;

/// <summary>
/// S08 (D / H6,N2) — issues the short-lived, signed <b>realtime membership
/// ticket</b> the gateway hands a member at the <c>/v1/realtime/jeeb:chat:{id}</c>
/// gate. The ticket is the WS-join authorization: the gateway runs the
/// chat-service membership check (the authority), then mints a ticket scoped to
/// (conversation_id, viewer_id, role); realtime-comunication-service verifies the
/// ticket at <c>join/3</c> so a non-member's join is rejected WITHOUT realtime ever
/// calling chat-service (org no-coupling law — membership authority is encoded in
/// the gateway-signed ticket).
///
/// <para>
/// The ticket is an HS256 JWT signed with the gateway's existing fleet signing key
/// (<c>Jwt:SigningKey</c>) — the SAME HS256 secret the realtime service's Guardian
/// pipeline already verifies the session bearer with, so no new key distribution is
/// needed. Claims: <c>sub</c> (viewer), <c>conv</c> (conversation id), <c>role</c>
/// (role_in_convo), short <c>exp</c>. The gateway computes NO membership here — it
/// only stamps what chat-service authorized.
/// </para>
/// </summary>
public interface IRealtimeTicketIssuer
{
    /// <summary>
    /// Mint a signed, short-lived ticket scoping <paramref name="viewerId"/> to join
    /// the realtime channel for <paramref name="conversationId"/> as
    /// <paramref name="roleInConvo"/>. Caller MUST have verified membership first.
    /// </summary>
    string Issue(string conversationId, string viewerId, string? roleInConvo);
}

/// <summary>
/// Default <see cref="IRealtimeTicketIssuer"/> — HS256 over <c>Jwt:SigningKey</c>.
/// Registered as a singleton; the signing key is read once at construction
/// (mirrors <see cref="TokenService"/>'s validation that the key is ≥ 32 bytes).
/// </summary>
public sealed class RealtimeTicketIssuer : IRealtimeTicketIssuer
{
    /// <summary>
    /// Ticket lifetime. Short by design — the ticket is consumed immediately on the
    /// WS upgrade; 120 s tolerates clock skew + a slow handshake without leaving a
    /// long-lived join credential in flight.
    /// </summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(120);

    private const string ConversationClaim = "conv";
    private const string RoleClaim = "role";

    private readonly JwtOptions _jwt;
    private readonly TimeProvider _clock;
    private readonly SigningCredentials _signingCredentials;

    public RealtimeTicketIssuer(IOptions<JwtOptions> jwt, TimeProvider clock)
    {
        _jwt = jwt.Value;
        _clock = clock;

        var keyBytes = Encoding.UTF8.GetBytes(_jwt.SigningKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey must be at least 32 bytes (256 bits) to mint a realtime ticket.");
        }
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
    }

    public string Issue(string conversationId, string viewerId, string? roleInConvo)
    {
        var now = _clock.GetUtcNow();
        var expires = now.Add(TicketLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, viewerId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(ConversationClaim, conversationId),
        };
        if (!string.IsNullOrWhiteSpace(roleInConvo))
        {
            claims.Add(new Claim(RoleClaim, roleInConvo));
        }

        var jwt = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: "jeeb-realtime",
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
