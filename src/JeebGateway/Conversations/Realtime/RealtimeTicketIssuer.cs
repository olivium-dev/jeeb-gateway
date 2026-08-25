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
/// ticket</b> the gateway hands a member at the <c>/v1/realtime/{tenant}:chat:{id}</c>
/// gate. The ticket is the WS-join authorization: the gateway runs the
/// chat-service membership check (the authority), then mints a ticket scoped to
/// (conversation_id, viewer_id, role); realtime-comunication-service verifies the
/// ticket at <c>join/3</c> so a non-member's join is rejected WITHOUT realtime ever
/// calling chat-service (org no-coupling law — membership authority is encoded in
/// the gateway-signed ticket).
///
/// <para>
/// The ticket is an HS256 JWT signed with a dedicated mounted key when
/// <c>Services:Realtime:MembershipTicketSigningKeyFile</c> is configured. The
/// existing <c>Jwt:SigningKey</c> remains a compatibility fallback for native
/// deployments. Realtime receives the corresponding key separately as
/// <c>GATEWAY_TICKET_SIGNING_KEY</c>; it is intentionally distinct from both the
/// session-token key and realtime's HS512 Guardian key. Claims: <c>sub</c> (viewer),
/// <c>conv</c> (conversation id), <c>role</c>
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
    string Issue(string conversationId, string viewerId, string roleInConvo);
}

/// <summary>
/// Default <see cref="IRealtimeTicketIssuer"/> — HS256 over the dedicated mounted
/// membership-ticket key, with <c>Jwt:SigningKey</c> as the legacy/native fallback.
/// Registered as a singleton; the signing key is read once at construction.
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
    // This issuer is part of the cross-service membership-ticket wire contract.
    // It must not inherit Jwt:Issuer, which may be an environment URL for session
    // tokens; realtime deliberately accepts only this fixed issuer.
    private const string MembershipTicketIssuer = "jeeb-gateway";

    private readonly TimeProvider _clock;
    private readonly SigningCredentials _signingCredentials;

    public RealtimeTicketIssuer(
        IOptions<JwtOptions> jwt,
        IOptions<JeebGateway.Realtime.RealtimeGuardianOptions> realtime,
        TimeProvider clock)
    {
        _clock = clock;

        var signingKey = JwtSigningKeySource.Resolve(
            jwt.Value.SigningKey,
            realtime.Value.MembershipTicketSigningKeyFile,
            "Services:Realtime:MembershipTicketSigningKeyFile");
        var keyBytes = Encoding.UTF8.GetBytes(signingKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "The realtime membership-ticket signing key must be at least 32 bytes "
                + "(256 bits).");
        }
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
    }

    public string Issue(string conversationId, string viewerId, string roleInConvo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleInConvo);
        var realtimeRole = NormalizeMembershipRole(roleInConvo);

        var now = _clock.GetUtcNow();
        var expires = now.Add(TicketLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, viewerId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(ConversationClaim, conversationId),
            new(RoleClaim, realtimeRole),
        };

        var jwt = new JwtSecurityToken(
            issuer: MembershipTicketIssuer,
            audience: "jeeb-realtime",
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    /// <summary>
    /// Translate chat-service's lifecycle roles onto realtime's deliberately small
    /// authorization vocabulary. Both offerer and winner are jeebers for message
    /// visibility; preserving either raw value would make realtime reject the ticket.
    /// Unknown values fail closed instead of being promoted to a participant role.
    /// </summary>
    private static string NormalizeMembershipRole(string roleInConvo)
        => roleInConvo.Trim().ToLowerInvariant() switch
        {
            "client" => "client",
            "jeeber" or "jeeber_offerer" or "jeeber_winner" => "jeeber",
            "admin" => "admin",
            "support" => "support",
            _ => throw new ArgumentException(
                $"Unsupported realtime membership role '{roleInConvo}'.",
                nameof(roleInConvo)),
        };
}
