using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// S06 / ADR-HB-001 — typed proxy over the NEW reusable <c>heart-beat</c>
/// presence service (Go + Redis). heart-beat owns ONLY the presence primitive:
/// the <c>online</c> bit, the <c>lastSeenAt</c> recency watermark, and the
/// TTL-driven idle-sweep. Geo / radius candidate search stays in
/// geolocation-service + matching, and all Jeeb semantics (withdrawn-offer
/// accounting, vehicle/zone) stay in the gateway BFF. Per the owner law,
/// heart-beat calls no sibling service; this client is the gateway's single seam
/// onto it and the gateway composes presence + radius itself.
///
/// <para>
/// Used by <see cref="JeebGateway.Controllers.AvailabilityController"/> when
/// <c>FeatureFlags:Heartbeat:Enabled</c> is true. Default-OFF this round: the
/// availability surface keeps writing/reading through the delivery-service
/// presence store until heart-beat is deployed and the flag is flipped (deploy
/// <c>workflow_dispatch</c>), exactly like the cdn / contract-signing / kyc
/// net-new kill switches.
/// </para>
///
/// <para>
/// <b>Domain language is product-agnostic.</b> The wire vocabulary is
/// <c>userId</c> / <c>online</c> / <c>lastSeenAt</c> / opaque <c>roleKey</c>.
/// NO <c>jeeber</c>, NO <c>tier</c>, NO <c>Jeeb</c> token is interpreted by the
/// service — the gateway maps <c>jeeber → userId</c> and supplies an OPAQUE
/// <c>roleKey</c> string the service stores but never reads.
/// </para>
/// </summary>
public interface IHeartBeatServiceClient
{
    /// <summary>
    /// The presence toggle — <c>PATCH /v1/presence</c> with body
    /// <c>{ userId, online, roleKey?, lat?, lng? }</c>. <c>online:true</c> goes
    /// online (stamps the watermark + sets the idle TTL); <c>online:false</c>
    /// goes offline (idempotent). This is the N13 home: a single atomic upstream
    /// write, so a successful offline toggle never depends on volatile
    /// gateway-local state.
    /// </summary>
    Task<HeartBeatPresence> SetPresenceAsync(HeartBeatPresenceRequest body, CancellationToken ct);

    /// <summary>
    /// Pure read — <c>GET /v1/presence/{userId}</c>, no mutation (so the gateway
    /// GET stays side-effect-free). Returns <see langword="null"/> when the user
    /// has no presence row yet (upstream 404) so the controller can surface a
    /// never-online default rather than a 500.
    /// </summary>
    Task<HeartBeatPresence?> GetPresenceAsync(string userId, CancellationToken ct);
}

/// <summary>
/// Request body for heart-beat <c>PATCH /v1/presence</c>. heart-beat standardises
/// on <b>camelCase</b> (Go struct tags), which is exactly what the shared
/// <c>JsonSerializerDefaults.Web</c> options already emit — so, unlike the
/// snake_case delivery-service DTOs, no explicit property names are required.
/// They are stated anyway to lock the wire contract against any future global
/// naming-policy change.
/// </summary>
public sealed class HeartBeatPresenceRequest
{
    [JsonPropertyName("userId")]
    public required string UserId { get; init; }

    [JsonPropertyName("online")]
    public required bool Online { get; init; }

    /// <summary>
    /// OPAQUE consumer namespace string the gateway supplies (e.g. "jeeber").
    /// heart-beat stores it for optional "list online of roleKey" enumeration but
    /// NEVER interprets it — it carries no Jeeb domain meaning on the wire.
    /// </summary>
    [JsonPropertyName("roleKey")]
    public string? RoleKey { get; init; }

    [JsonPropertyName("lat")]
    public double? Lat { get; init; }

    [JsonPropertyName("lng")]
    public double? Lng { get; init; }
}

/// <summary>
/// 200 body of the heart-beat presence endpoints
/// (<c>PATCH /v1/presence</c>, <c>GET /v1/presence/{userId}</c>). camelCase per
/// the heart-beat contract (ADR-HB-001 §3). Only the fields the gateway maps onto
/// its public <see cref="JeebGateway.Availability.AvailabilityResponse"/> are
/// represented; unmapped fields are silently dropped by STJ.
/// </summary>
public sealed class HeartBeatPresence
{
    [JsonPropertyName("userId")]
    public required string UserId { get; init; }

    [JsonPropertyName("online")]
    public required bool Online { get; init; }

    [JsonPropertyName("lastSeenAt")]
    public DateTimeOffset? LastSeenAt { get; init; }

    [JsonPropertyName("wentOnlineAt")]
    public DateTimeOffset? WentOnlineAt { get; init; }

    [JsonPropertyName("roleKey")]
    public string? RoleKey { get; init; }

    [JsonPropertyName("lat")]
    public double? Lat { get; init; }

    [JsonPropertyName("lng")]
    public double? Lng { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// A non-2xx (and non-404) outcome from a heart-beat presence endpoint that the
/// controller must map to a non-500 ProblemDetails. The gateway is a thin BFF on
/// this path — it carries the upstream <see cref="StatusCode"/> through rather
/// than re-interpreting presence.
/// </summary>
public sealed class HeartBeatPresenceException : Exception
{
    public int StatusCode { get; }
    public string? Reason { get; }

    public HeartBeatPresenceException(int statusCode, string? reason)
        : base($"heart-beat presence returned {statusCode} ({reason ?? "no reason"}).")
    {
        StatusCode = statusCode;
        Reason = reason;
    }
}
