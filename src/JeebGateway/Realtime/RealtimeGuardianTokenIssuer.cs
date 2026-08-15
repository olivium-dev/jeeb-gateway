using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Realtime;

/// <summary>
/// Mints the credential <c>realtime-comunication-service</c> actually accepts:
/// a Guardian-shaped HS512 JWT, <b>scoped to one topic</b>.
///
/// <para><b>Why this exists.</b> The realtime service authenticates every ingest
/// (<c>IngestController.authenticate/1</c> → <c>Guardian.verify_token/1</c>) and every
/// socket (<c>LiveCommSocket.connect/3</c>) against <b>its own</b> Guardian secret —
/// a different key from the gateway's <c>Jwt:SigningKey</c>. A forwarded gateway
/// bearer therefore fails realtime auth. The service also ships an <b>open,
/// unauthenticated</b> minter at <c>POST /api/auth/token</c> which will hand
/// <c>topics:["*"], scopes:["subscribe","publish"]</c> to any caller. The client path
/// must never be built on that: it is an unauthenticated grant of the whole bus.
/// Instead the gateway — which already authenticated the user and already knows which
/// delivery is theirs — issues the credential itself, narrowed to the single topic the
/// caller was authorized for.</para>
///
/// <para><b>Wire shape.</b> Probed off the live service rather than assumed: header
/// <c>{"alg":"HS512","typ":"JWT"}</c>; claims <c>iss</c>/<c>aud</c> = the Guardian issuer
/// (<c>live_comm</c>), <c>typ</c> = <c>access</c>, plus <c>sub</c>, <c>jti</c>,
/// <c>iat</c>, <c>nbf</c>, <c>exp</c>, <c>role</c>, and the two JSON <b>arrays</b>
/// <c>scopes</c> and <c>topics</c> that <c>LiveComm.Policy.ACL.authorize/3</c> reads.
/// The arrays must serialize as arrays: <c>Topic.matches_any?/2</c> guards on
/// <c>is_list/1</c>, so a single-element claim flattened to a bare string stops matching
/// and every publish silently 403s.</para>
///
/// <para><b>Why this signs by hand instead of using JwtSecurityTokenHandler.</b> The
/// upstream accepts <b>HS512 only</b> — HS256 and HS384 are both rejected 401
/// (<c>evidence/03-algo-negotiation.txt</c>), because Guardian's default
/// <c>allowed_algos</c> is <c>["HS512"]</c> and nothing overrides it. But its Guardian
/// secret is whatever the operator configured, and the one the service runs with today
/// is 56 bytes; <c>Microsoft.IdentityModel</c> hard-refuses to sign HS512 below 512 bits
/// (<c>IDX10720</c>). So the two constraints are jointly unsatisfiable through that
/// library, and the JWT is assembled directly: two base64url JSON segments and an
/// <see cref="HMACSHA512"/> over them. This is signing only — no parsing, no
/// verification, no algorithm negotiation, i.e. none of the surface where hand-rolled
/// JWT code is normally dangerous. <c>HMACSHA512</c> follows RFC 2104 for any key
/// length.</para>
///
/// <para><b>Fail-closed.</b> When no secret is configured this returns <c>null</c>
/// rather than falling back to the open minter or to an unscoped token. Callers must
/// treat <c>null</c> as "realtime credential unavailable" and degrade — never as
/// "proceed without a credential".</para>
/// </summary>
public interface IRealtimeGuardianTokenIssuer
{
    /// <summary>
    /// True when a Guardian secret is configured and tokens can be minted.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Mint a token authorizing <paramref name="subject"/> to perform
    /// <paramref name="scopes"/> on <paramref name="topic"/> and on nothing else.
    /// Returns <c>null</c> when no Guardian secret is configured.
    /// </summary>
    /// <param name="subject">The <c>sub</c> claim — the acting user, never a wildcard.</param>
    /// <param name="topic">The single topic the credential is scoped to.</param>
    /// <param name="scopes"><c>subscribe</c> and/or <c>publish</c>. Never <c>admin</c>.</param>
    /// <param name="lifetime">Token lifetime; defaults to the configured TTL.</param>
    RealtimeGuardianToken? Issue(
        string subject,
        string topic,
        IReadOnlyList<string> scopes,
        TimeSpan? lifetime = null);
}

/// <summary>A minted credential and the instant it stops being valid.</summary>
public sealed record RealtimeGuardianToken(string Token, DateTimeOffset ExpiresAt);

/// <inheritdoc />
public sealed class RealtimeGuardianTokenIssuer : IRealtimeGuardianTokenIssuer
{
    /// <summary>The <c>scopes</c> value for the gateway's own server-side publish.</summary>
    public static readonly IReadOnlyList<string> PublishOnly = new[] { "publish" };

    /// <summary>The <c>scopes</c> value handed to a client that only reads a stream.</summary>
    public static readonly IReadOnlyList<string> SubscribeOnly = new[] { "subscribe" };

    /// <summary>
    /// The gateway's own floor on realtime key material, matching the one
    /// <see cref="JeebGateway.Conversations.Realtime.RealtimeTicketIssuer"/> already
    /// enforces on <c>Jwt:SigningKey</c>. Deliberately NOT HS512's nominal 64 bytes:
    /// the key has to match whatever the upstream verifies with, and rejecting a
    /// working secret would just relocate the outage.
    /// </summary>
    private const int MinimumSecretBytes = 32;

    /// <summary>Below this, HS512 is being used with less key material than its digest.</summary>
    private const int RecommendedSecretBytes = 64;

    private static readonly JsonSerializerOptions ClaimJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RealtimeGuardianOptions _options;
    private readonly TimeProvider _clock;
    private readonly byte[]? _key;

    public RealtimeGuardianTokenIssuer(
        IOptions<RealtimeGuardianOptions> options,
        TimeProvider clock,
        ILogger<RealtimeGuardianTokenIssuer> log)
    {
        _options = options.Value;
        _clock = clock;

        var secret = _options.GuardianSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            // Not configured: IsConfigured stays false and every caller fails closed.
            return;
        }

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        if (keyBytes.Length < MinimumSecretBytes)
        {
            throw new InvalidOperationException(
                $"{RealtimeGuardianOptions.SectionName}:GuardianSecret must be at least "
                + $"{MinimumSecretBytes} bytes to sign a realtime credential; the configured "
                + $"value is {keyBytes.Length} bytes.");
        }

        if (keyBytes.Length < RecommendedSecretBytes)
        {
            log.LogWarning(
                "{Section}:GuardianSecret is {Length} bytes; HS512 wants at least {Recommended}. "
                + "This is accepted because it must match the secret realtime-comunication-service "
                + "verifies with, but it is weaker key material than the algorithm assumes.",
                RealtimeGuardianOptions.SectionName, keyBytes.Length, RecommendedSecretBytes);
        }

        _key = keyBytes;
    }

    public bool IsConfigured => _key is not null;

    public RealtimeGuardianToken? Issue(
        string subject,
        string topic,
        IReadOnlyList<string> scopes,
        TimeSpan? lifetime = null)
    {
        if (_key is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
        {
            throw new ArgumentException("At least one scope is required.", nameof(scopes));
        }

        var now = _clock.GetUtcNow();
        var expires = now.Add(lifetime ?? TimeSpan.FromSeconds(_options.TokenTtlSeconds));
        var issuer = _options.GuardianIssuer;

        var header = new GuardianHeader();
        var payload = new GuardianClaims
        {
            Aud = issuer,
            Iss = issuer,
            // Guardian's token_type. Distinct from the JOSE header's "typ": "JWT".
            Typ = "access",
            Sub = subject,
            Jti = Guid.NewGuid().ToString("N"),
            Iat = now.ToUnixTimeSeconds(),
            // 1s of backdate on nbf mirrors what the service's own minter emits and
            // absorbs sub-second clock skew between the gateway and realtime hosts.
            Nbf = now.ToUnixTimeSeconds() - 1,
            Exp = expires.ToUnixTimeSeconds(),
            Role = "user",
            Scopes = scopes,
            // ONE topic. The whole point of this issuer: never "*", never a list the
            // caller supplied.
            Topics = new[] { topic },
        };

        var signingInput =
            Base64Url(JsonSerializer.SerializeToUtf8Bytes(header, ClaimJson))
            + "."
            + Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, ClaimJson));

        var signature = HMACSHA512.HashData(_key, Encoding.ASCII.GetBytes(signingInput));

        return new RealtimeGuardianToken(
            signingInput + "." + Base64Url(signature), expires);
    }

    /// <summary>base64url without padding, per RFC 7515 §2.</summary>
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class GuardianHeader
    {
        [JsonPropertyName("alg")] public string Alg => "HS512";
        [JsonPropertyName("typ")] public string Typ => "JWT";
    }

    private sealed class GuardianClaims
    {
        [JsonPropertyName("aud")] public required string Aud { get; init; }
        [JsonPropertyName("iss")] public required string Iss { get; init; }
        [JsonPropertyName("typ")] public required string Typ { get; init; }
        [JsonPropertyName("sub")] public required string Sub { get; init; }
        [JsonPropertyName("jti")] public required string Jti { get; init; }
        [JsonPropertyName("iat")] public required long Iat { get; init; }
        [JsonPropertyName("nbf")] public required long Nbf { get; init; }
        [JsonPropertyName("exp")] public required long Exp { get; init; }
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("scopes")] public required IReadOnlyList<string> Scopes { get; init; }
        [JsonPropertyName("topics")] public required IReadOnlyList<string> Topics { get; init; }
    }
}

/// <summary>
/// Binds <c>Services:Realtime</c>. The secret is supplied by the environment
/// (<c>Services__Realtime__GuardianSecret</c>) and is never committed.
/// </summary>
public sealed class RealtimeGuardianOptions
{
    public const string SectionName = "Services:Realtime";

    /// <summary>Legacy tenant segment; stays the accepted route alias forever so
    /// pre-rename phone URLs keep resolving after any config flip.</summary>
    public const string DefaultTenantPrefix = "jeeb";

    /// <summary>Tenant segment for every LiveComm name ("{p}:chat", "{p}:delivery:{id}",
    /// "{p}_conversation:{id}"); the default keeps live names byte-identical.</summary>
    public string TenantPrefix { get; set; } = DefaultTenantPrefix;

    /// <summary>
    /// The realtime service's Guardian signing secret — the value its own
    /// <c>LiveComm.Guardian</c> verifies with. HS512, so >= 64 bytes.
    /// Empty (the committed default) disables minting and every dependent path
    /// fails closed.
    /// </summary>
    public string? GuardianSecret { get; set; }

    /// <summary>The <c>iss</c>/<c>aud</c> pair Guardian is configured with.</summary>
    public string GuardianIssuer { get; set; } = "live_comm";

    /// <summary>Credential lifetime in seconds. Matches Guardian's own "access" TTL.</summary>
    public int TokenTtlSeconds { get; set; } = 900;

    /// <summary>
    /// The WebSocket URL a <b>device</b> can reach, e.g.
    /// <c>ws://192.168.2.39:5804/socket/websocket</c>. Distinct from
    /// <c>Services:Realtime:BaseUrl</c>, which is routinely a loopback address that
    /// means nothing on a phone — handing a device <c>ws://127.0.0.1/...</c> is a
    /// silent-failure landmine, so an unset value yields a null url rather than a
    /// derived-from-loopback guess.
    /// </summary>
    public string? PublicSocketUrl { get; set; }
}
