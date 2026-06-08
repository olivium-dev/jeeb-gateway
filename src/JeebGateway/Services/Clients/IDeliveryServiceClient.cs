using JeebGateway.Tiers;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over delivery-service (Go) Jeeb routes (internal/jeeb/handlers.go).
/// Used by the gateway controllers when <c>FeatureFlags:UseUpstream:Delivery</c>
/// is set.
/// </summary>
public interface IDeliveryServiceClient
{
    Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct);

    /// <summary>
    /// Reads shipments from the delivery-service DB via
    /// <c>GET /api/v1/shipments</c>. All parameters are optional.
    /// </summary>
    Task<ShipmentsListDto> ListShipmentsAsync(
        string? orderId,
        string? stage,
        int? limit,
        CancellationToken ct);

    Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct);

    /// <summary>
    /// SPINE-FOUNDATION / ADR-006: seed a durable delivery row in the canonical
    /// delivery-service <c>deliveries</c> table via the additive create-row
    /// endpoint <c>POST /api/v1/deliveries</c>. The gateway forwards its minted
    /// request id as the row <c>id</c> so the same id is stable across
    /// create → <c>POST /api/v1/matching/run</c> (request_id mode) — without
    /// this row the matching run returns <c>ErrUnknownRequest</c> (404).
    ///
    /// Idempotent upstream (<c>ON CONFLICT (id) DO NOTHING</c>) so a retried
    /// create collapses onto the same row. delivery-service owns the row's
    /// state machine; the gateway only seeds <c>(id, tenant_id, client_id,
    /// tier_id, pickup_lat/lng, status='Ordered')</c>. snake_case body (Go).
    /// </summary>
    /// <exception cref="DeliveryCreateRowException">
    /// Thrown for a non-2xx (and non-409-idempotent) upstream status so the
    /// durable store surfaces a real failure rather than silently dropping the
    /// row.
    /// </exception>
    Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct);

    Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct);

    Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct);

    /// <summary>
    /// T-BE-019 (JEB-55): canonical status transition. Delegates the
    /// status flip to the source-of-truth delivery-service so handover-OTP
    /// success can mark the delivery as 'done' there (commission settlement
    /// in T-BE-020 keys off the upstream record). The gateway must NEVER
    /// write status directly to its local store on the OTP-verified path —
    /// that splits the canonical write between two systems.
    /// </summary>
    Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct);

    /// <summary>
    /// T-BE-019 (JEB-55): the durable AtDoor-gate half of the handover OTP.
    /// Calls the frozen delivery-service contract
    /// <c>POST /api/v1/deliveries/{id}/otp/issue</c>. delivery-service owns
    /// the at_door gate: the gateway must call this BEFORE asking
    /// one-time-password to dispatch the SMS, so a courier who is not
    /// physically at the door never triggers an OTP. The raw code never
    /// leaves the gateway↔one-time-password hop (AC5) — only an optional
    /// <paramref name="codeHash"/> for support is forwarded.
    /// </summary>
    /// <returns><see cref="DeliveryHandoverIssueResult"/> on 200.</returns>
    /// <exception cref="DeliveryHandoverException">
    /// Thrown for 409 <c>not_at_door</c> and 404 so the controller maps the
    /// upstream status straight through as RFC 7807.
    /// </exception>
    Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct);

    /// <summary>
    /// T-BE-019 (JEB-55): the durable attempt-counter / 423-lock / settlement
    /// half of the handover OTP. Calls the frozen delivery-service contract
    /// <c>POST /api/v1/deliveries/{id}/otp/verify</c> with ONLY a success
    /// boolean (the gateway already validated the raw code against
    /// one-time-password; AC5 — the code never reaches delivery-service).
    /// delivery-service runs the AtDoor→Done transition + single-tx
    /// settlement on success and owns the durable, multi-replica-safe
    /// attempt counter and lock.
    /// </summary>
    /// <returns><see cref="DeliveryHandoverVerifyResult"/> on 200 (verified).</returns>
    /// <exception cref="DeliveryHandoverException">
    /// Thrown for 401 <c>invalid_code</c> (with <c>attempts_remaining</c>),
    /// 423 <c>locked</c> (with <c>escalation_id</c>), 409 <c>not_at_door</c>,
    /// and 404 so the controller maps the upstream status straight through
    /// as RFC 7807.
    /// </exception>
    Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, CancellationToken ct);

    Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct);

    /// <summary>
    /// S06 presence wire (DELIVERY-SERVICE-RELOCATION-DESIGN.md §8 — availability
    /// relocation). Writes the jeeber's online/offline + vehicle + zone +
    /// last-known location into the canonical delivery-service presence store —
    /// the SAME store the matching run (<see cref="RunMatchingAsync"/>) reads its
    /// online set from. Org-law: the gateway holds NO presence state of its own on
    /// this path; it is a thin BFF passthrough. Canonical route:
    /// <c>POST /api/v1/jeebers/{jeeberId}/availability</c> (snake_case body).
    /// </summary>
    Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct);

    /// <summary>
    /// S06 presence read (DELIVERY-SERVICE-RELOCATION-DESIGN.md §8). Reads the
    /// jeeber's current availability from the canonical delivery-service presence
    /// store without mutating it. Canonical route:
    /// <c>GET /api/v1/jeebers/{jeeberId}/availability</c>. Returns
    /// <see langword="null"/> when the jeeber has no presence row yet (upstream
    /// 404) so the controller can surface a never-online default rather than 500.
    /// </summary>
    Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct);

    /// <summary>
    /// S06 GPS heartbeat wire (DELIVERY-SERVICE-RELOCATION-DESIGN.md §8). Bumps
    /// the jeeber's <c>last_heartbeat_at</c> + last-known location in the SAME
    /// presence store the matching run reads for its freshness predicate — so a
    /// streaming GPS jeeber stays in the online set. Org-law: routed to
    /// delivery-service (not geolocation) to keep ONE presence store and avoid a
    /// cross-service DB read. Canonical route:
    /// <c>POST /api/v1/jeebers/{jeeberId}/heartbeat</c> with body
    /// <c>{ "lat": &lt;lat&gt;, "lng": &lt;lng&gt; }</c>.
    /// </summary>
    /// <exception cref="DeliveryAvailabilityException">
    /// Thrown for a 404 (jeeber never went online) so the controller can map it to
    /// a 409/400 ProblemDetails rather than a 500.
    /// </exception>
    Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct);

    /// <summary>
    /// Courier matching (relocated from the gateway's in-memory Haversine engine
    /// into delivery-service). Calls the canonical Go route
    /// <c>POST /api/v1/matching/run</c>. delivery-service owns the radius scan,
    /// vehicle-type filter, proximity/rating ordering, and the new-offer push
    /// fan-out; the gateway is a thin BFF that forwards the request and surfaces
    /// the result. Request + response are <b>snake_case</b> (Go) — the DTOs carry
    /// explicit <see cref="System.Text.Json.Serialization.JsonPropertyName"/>
    /// attributes so the shared web-default JsonOptions bind both directions
    /// without changing the global naming policy other client methods depend on.
    /// </summary>
    /// <returns><see cref="DeliveryMatchingRunResult"/> on 200.</returns>
    /// <exception cref="DeliveryMatchingException">
    /// Thrown for 400 (bad input), 404 (unknown tier), and 422 (validation) so
    /// the controller maps the upstream status straight through as RFC 7807.
    /// </exception>
    Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct);
}

/// <summary>
/// Request body for delivery-service <c>POST /api/v1/matching/run</c>.
/// delivery-service (Go) reads <b>snake_case</b>; the explicit
/// <see cref="System.Text.Json.Serialization.JsonPropertyName"/> attributes
/// scope the snake_case mapping to exactly this DTO so the shared
/// <c>JsonSerializerDefaults.Web</c> options (camelCase) do not emit
/// <c>requestId</c>/<c>pickupLat</c> where Go expects <c>request_id</c>/<c>pickup_lat</c>.
/// </summary>
public sealed class DeliveryMatchingRunRequest
{
    /// <summary>Existing delivery request id. When set, delivery-service resolves
    /// pickup + tier from the row. Null for the dry-run preview shape.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("pickup_lat")]
    public double? PickupLat { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("pickup_lng")]
    public double? PickupLng { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("tier_id")]
    public string? TierId { get; init; }

    /// <summary>Optional vehicle allowlist (wire strings: car / motorbike /
    /// bicycle / scooter / walk). Null/empty means "any".</summary>
    [System.Text.Json.Serialization.JsonPropertyName("allowed_vehicle_types")]
    public IReadOnlyList<string>? AllowedVehicleTypes { get; init; }

    /// <summary>Tenant scope — required by delivery-service.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("tenant_id")]
    public required string TenantId { get; init; }
}

/// <summary>
/// 200 body of delivery-service <c>POST /api/v1/matching/run</c>:
/// <c>{ request_id, tier_id, radius_km, notified_count, candidate_count,
/// candidates:[{ user_id, vehicle_type, distance_km, rating }], elapsed_ms }</c>.
///
/// delivery-service (Go) emits <b>snake_case</b>; without the explicit
/// <see cref="System.Text.Json.Serialization.JsonPropertyName"/> attributes the
/// shared <c>JsonSerializerDefaults.Web</c> (camelCase) options would fail to
/// bind <c>request_id</c> onto the <c>required</c> <see cref="RequestId"/> and
/// throw a JsonException on the SUCCESS path — surfacing as an unhandled 500
/// after delivery-service already ran matching + fanned out the offers. The
/// attributes scope the snake_case mapping to these DTOs without mutating the
/// global naming policy.
/// </summary>
public sealed class DeliveryMatchingRunResult
{
    [System.Text.Json.Serialization.JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("tier_id")]
    public required string TierId { get; init; }

    /// <summary>
    /// The tier's stable lowercase <b>code</b> (<c>flash</c>/<c>standard</c>/
    /// <c>express</c>) — delivery-service's <c>tier_code</c> (RunOutcome.TierCode,
    /// run.go). ADDITIVE bind: <see cref="TierId"/> still carries the tier UUID
    /// unchanged for existing consumers that key off the id. The gateway surfaces
    /// this code as the client-facing <c>$.tierId</c> (MatchingController.Run),
    /// which is the human-readable tier the client ordered. Nullable so an older
    /// delivery-service build that omits the field deserializes without throwing
    /// (the controller falls back to <see cref="TierId"/>).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("tier_code")]
    public string? TierCode { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("radius_km")]
    public double RadiusKm { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("notified_count")]
    public int NotifiedCount { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("candidate_count")]
    public int CandidateCount { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("candidates")]
    public IReadOnlyList<DeliveryMatchedCandidate> Candidates { get; init; } = Array.Empty<DeliveryMatchedCandidate>();

    [System.Text.Json.Serialization.JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; init; }
}

/// <summary>
/// One element of the <c>candidates</c> array in
/// <see cref="DeliveryMatchingRunResult"/>. snake_case (Go).
/// </summary>
public sealed class DeliveryMatchedCandidate
{
    [System.Text.Json.Serialization.JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("vehicle_type")]
    public required string VehicleType { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("distance_km")]
    public double DistanceKm { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("rating")]
    public double Rating { get; init; }
}

/// <summary>
/// A non-200 outcome from delivery-service <c>POST /api/v1/matching/run</c>
/// (400 bad input / 404 unknown tier / 422 validation). The gateway is a thin
/// BFF on this path — it surfaces the upstream <see cref="StatusCode"/> +
/// <see cref="Reason"/> back to the caller as RFC 7807 ProblemDetails rather
/// than re-interpreting the matching contract.
/// </summary>
public sealed class DeliveryMatchingException : Exception
{
    public int StatusCode { get; }
    public string? Reason { get; }

    public DeliveryMatchingException(int statusCode, string? reason)
        : base($"delivery-service matching returned {statusCode} ({reason ?? "no reason"}).")
    {
        StatusCode = statusCode;
        Reason = reason;
    }
}

public sealed class CreateDeliveryRequestUpstream
{
    public required string ClientId { get; init; }
    public required string Description { get; init; }
    public string? AudioUrl { get; init; }
    public IReadOnlyList<string> Photos { get; init; } = Array.Empty<string>();
    public required string TierId { get; init; }
    public required LatLngUpstream Pickup { get; init; }
    public required LatLngUpstream Dropoff { get; init; }
    public string? PickupAddress { get; init; }
    public string? DropoffAddress { get; init; }
}

public sealed class LatLngUpstream
{
    public required double Lat { get; init; }
    public required double Lng { get; init; }
}

/// <summary>
/// SPINE-FOUNDATION / ADR-006: request body for the additive create-row
/// endpoint <c>POST /api/v1/deliveries</c>. delivery-service (Go) reads
/// <b>snake_case</b>; the explicit
/// <see cref="System.Text.Json.Serialization.JsonPropertyName"/> attributes
/// scope the snake_case mapping to exactly this DTO so the shared
/// <c>JsonSerializerDefaults.Web</c> (camelCase) options do not emit
/// <c>clientId</c>/<c>tierId</c> where Go expects <c>client_id</c>/<c>tier_id</c>.
///
/// The gateway supplies its minted request id as <see cref="Id"/> so the row id
/// is stable across create → matching/run. Only the matching-resolve columns are
/// seeded (<c>tier_id</c>, <c>pickup_lat/lng</c>); delivery-service owns the rest
/// of the lifecycle.
/// </summary>
public sealed class CreateDeliveryRowUpstream
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public required string Id { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("tenant_id")]
    public required string TenantId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("tier_id")]
    public required string TierId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("pickup_lat")]
    public required double PickupLat { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("pickup_lng")]
    public required double PickupLng { get; init; }
}

/// <summary>
/// SPINE-FOUNDATION / ADR-006: 2xx body of <c>POST /api/v1/deliveries</c>.
/// delivery-service (Go, rest.go) emits <b>snake_case</b> and keys the echoed
/// row id as <c>delivery_id</c> — the SAME shape as the OTP issue/verify bodies,
/// NOT <c>id</c>. The gateway only needs that echoed id to confirm the seeded
/// row id equals the minted request id (request_id stability).
/// </summary>
/// <remarks>
/// A4 fix: bind the id from the canonical <c>delivery_id</c> wire key. Before
/// this, the id bound from <c>id</c>, so against the real <c>delivery_id</c> body
/// it read back <c>null</c> and <c>DurableRequestsStore</c>'s stability assertion
/// (<c>row.Id == created.Id</c>) threw → 500 on the durable create path →
/// <c>durable_requests</c> rolled back → A4 idempotent replay returned a NEW id.
/// A backward-compatible <c>id</c> alias is retained as a fallback so any
/// older/alternate upstream shape that still emits <c>id</c> binds (additive,
/// non-breaking). <c>delivery_id</c> wins when both keys are present, regardless
/// of document order.
/// </remarks>
public sealed class DeliveryRowUpstream
{
    private string? _id;

    /// <summary>
    /// Canonical row id, bound from <c>delivery_id</c> (delivery-service rest.go)
    /// and, as a fallback, from a legacy <c>id</c> key. Non-empty after a
    /// successful 2xx bind so the durable stability assertion can compare it
    /// against the request id.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Id
    {
        get => _id ?? string.Empty;
        init { if (!string.IsNullOrEmpty(value)) _id = value; }
    }

    /// <summary>Primary wire mapping: delivery-service emits <c>delivery_id</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("delivery_id")]
    [System.Text.Json.Serialization.JsonInclude]
    public string? DeliveryId
    {
        get => _id;
        init { if (!string.IsNullOrEmpty(value)) _id = value; }
    }

    /// <summary>Backward-compatible fallback: legacy <c>id</c> key (loses to <c>delivery_id</c>).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    [System.Text.Json.Serialization.JsonInclude]
    public string? LegacyId
    {
        get => null;
        init { if (_id is null && !string.IsNullOrEmpty(value)) _id = value; }
    }

    [System.Text.Json.Serialization.JsonPropertyName("tenant_id")]
    public string? TenantId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>
/// SPINE-FOUNDATION / ADR-006: a non-2xx (and non-idempotent-409) outcome from
/// <c>POST /api/v1/deliveries</c>. The durable store surfaces this rather than
/// silently dropping the row — a missing delivery row is exactly the bug
/// (matching 404) ADR-006 fixes, so a seed failure must not be swallowed.
/// </summary>
public sealed class DeliveryCreateRowException : Exception
{
    public int StatusCode { get; }
    public string? Reason { get; }

    public DeliveryCreateRowException(int statusCode, string? reason)
        : base($"delivery-service create-row returned {statusCode} ({reason ?? "no reason"}).")
    {
        StatusCode = statusCode;
        Reason = reason;
    }
}

public sealed class DeliveryRequestUpstream
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }
    public required string Status { get; init; }
    public string? Description { get; init; }
    public string? AudioUrl { get; init; }
    public IReadOnlyList<string> Photos { get; init; } = Array.Empty<string>();
    public string? TierId { get; init; }
    public LatLngUpstream? Pickup { get; init; }
    public LatLngUpstream? Dropoff { get; init; }
    public string? PickupAddress { get; init; }
    public string? DropoffAddress { get; init; }
    public string? JeeberId { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public bool GpsTrackingActive { get; init; }
    public int OtpAttemptCount { get; init; }
    public DateTimeOffset? OtpLockedAt { get; init; }
    public string? OtpEscalationId { get; init; }

    /// <summary>
    /// T-BE-019 (JEB-55): E.164 phone for the 4-digit handover OTP.
    /// </summary>
    public string? RecipientPhone { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? CancelledBy { get; init; }
    public string? CancellationReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class DeliveryOtpVerifyResult
{
    public DeliveryRequestUpstream? Request { get; init; }
    public bool Verified { get; init; }
    public int AttemptsRemaining { get; init; }
    public string? Reason { get; init; }
    public string? EscalationId { get; init; }
    public DateTimeOffset? LockedAt { get; init; }
}

/// <summary>
/// T-BE-019 (JEB-55): 200 body of <c>POST /api/v1/deliveries/{id}/otp/issue</c>
/// — <c>{ delivery_id, issued:true }</c>.
///
/// delivery-service (Go) emits <b>snake_case</b>; the shared
/// <c>JsonSerializerDefaults.Web</c> options in <see cref="DeliveryServiceClient"/>
/// map PascalCase → camelCase, which would fail to bind <c>delivery_id</c> onto
/// the <c>required</c> <see cref="DeliveryId"/> and throw a JsonException on the
/// SUCCESS path. The explicit <see cref="System.Text.Json.Serialization.JsonPropertyName"/>
/// attributes scope the snake_case mapping to exactly this DTO without changing
/// the global naming policy (which other client methods depend on).
/// </summary>
public sealed class DeliveryHandoverIssueResult
{
    [System.Text.Json.Serialization.JsonPropertyName("delivery_id")]
    public required string DeliveryId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("issued")]
    public bool Issued { get; init; }
}

/// <summary>
/// T-BE-019 (JEB-55): 200 body of <c>POST /api/v1/deliveries/{id}/otp/verify</c>
/// — <c>{ delivery_id, verified:true, status:"Done" }</c>.
///
/// delivery-service (Go) emits <b>snake_case</b>. As with
/// <see cref="DeliveryHandoverIssueResult"/>, the explicit
/// <see cref="System.Text.Json.Serialization.JsonPropertyName"/> attributes bind
/// <c>delivery_id</c>/<c>verified</c>/<c>status</c> under the shared web-default
/// options without mutating the global naming policy. Without them STJ throws on
/// the SUCCESS path — after delivery-service has already committed
/// AtDoor→Done + settlement — surfacing as an unhandled 500.
/// </summary>
public sealed class DeliveryHandoverVerifyResult
{
    [System.Text.Json.Serialization.JsonPropertyName("delivery_id")]
    public required string DeliveryId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>
/// T-BE-019 (JEB-55): a non-200 outcome from the frozen delivery-service
/// handover-OTP endpoints. The gateway is a thin BFF on this path — it does
/// NOT re-interpret the durable gate; it surfaces the upstream
/// <see cref="StatusCode"/> + <see cref="Reason"/> back to the caller as
/// RFC 7807 ProblemDetails.
///
/// Carries the contract's typed extension fields so the controller can echo
/// them: <see cref="AttemptsRemaining"/> for 401 <c>invalid_code</c> and
/// <see cref="EscalationId"/> for 423 <c>locked</c>.
/// </summary>
public sealed class DeliveryHandoverException : Exception
{
    public int StatusCode { get; }
    public string? Reason { get; }
    public int? AttemptsRemaining { get; }
    public string? EscalationId { get; }

    /// <summary>
    /// T-BE-019 (JEB-55): the upstream <c>locked_at</c> stamp from a 423 body
    /// (RFC3339). The controller echoes this instead of synthesizing a gateway
    /// clock value so the lock instant matches the source-of-truth record.
    /// Null when the upstream omits it.
    /// </summary>
    public DateTimeOffset? LockedAt { get; }

    public DeliveryHandoverException(
        int statusCode,
        string? reason,
        int? attemptsRemaining = null,
        string? escalationId = null,
        DateTimeOffset? lockedAt = null)
        : base($"delivery-service handover returned {statusCode} ({reason ?? "no reason"}).")
    {
        StatusCode = statusCode;
        Reason = reason;
        AttemptsRemaining = attemptsRemaining;
        EscalationId = escalationId;
        LockedAt = lockedAt;
    }
}

public sealed class DeliveryCancelUpstreamRequest
{
    public required string Role { get; init; }
    public required string UserId { get; init; }
    public string? Reason { get; init; }
}

public sealed class DeliveryCancelResult
{
    public required DeliveryRequestUpstream Request { get; init; }
    public required string Outcome { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Request body for delivery-service <c>POST /api/v1/jeebers/{id}/availability</c>.
/// delivery-service (Go) reads <b>snake_case</b>; the explicit
/// <see cref="System.Text.Json.Serialization.JsonPropertyName"/> attributes scope
/// the snake_case mapping to exactly this DTO so the shared
/// <c>JsonSerializerDefaults.Web</c> (camelCase) options do not emit
/// <c>vehicleType</c> where Go expects <c>vehicle_type</c>.
/// </summary>
public sealed class JeeberAvailabilityUpstreamRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("online")]
    public required bool Online { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("vehicle_type")]
    public string? VehicleType { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("zone")]
    public string? Zone { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("lat")]
    public double? Lat { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("lng")]
    public double? Lng { get; init; }
}

/// <summary>
/// S06: a non-2xx outcome from a delivery-service presence endpoint
/// (<c>POST /api/v1/jeebers/{id}/heartbeat</c>) that the controller must map to a
/// non-500 ProblemDetails. The common case is 404 — a heartbeat for a jeeber who
/// never went online — which the gateway surfaces as a 409 Conflict rather than
/// leaking the upstream 404 as an unhandled 500. The gateway is a thin BFF here:
/// it carries the upstream <see cref="StatusCode"/> through, it does not
/// re-interpret presence.
/// </summary>
public sealed class DeliveryAvailabilityException : Exception
{
    public int StatusCode { get; }
    public string? Reason { get; }

    public DeliveryAvailabilityException(int statusCode, string? reason)
        : base($"delivery-service presence returned {statusCode} ({reason ?? "no reason"}).")
    {
        StatusCode = statusCode;
        Reason = reason;
    }
}

/// <summary>
/// 2xx body of the delivery-service presence endpoints
/// (<c>POST/GET /api/v1/jeebers/{id}/availability</c> and
/// <c>POST /api/v1/jeebers/{id}/heartbeat</c>). delivery-service (Go) emits
/// <b>snake_case</b>; without the explicit
/// <see cref="System.Text.Json.Serialization.JsonPropertyName"/> attributes the
/// shared <c>JsonSerializerDefaults.Web</c> (camelCase) options would fail to bind
/// <c>jeeber_id</c> onto the <c>required</c> <see cref="JeeberId"/> and throw a
/// JsonException on the SUCCESS path — surfacing as an unhandled 500 after
/// delivery-service already committed the presence write. The attributes scope the
/// snake_case mapping to this DTO without mutating the global naming policy.
/// </summary>
public sealed class JeeberAvailabilityUpstream
{
    [System.Text.Json.Serialization.JsonPropertyName("jeeber_id")]
    public required string JeeberId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("online")]
    public required bool Online { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("vehicle_type")]
    public string? VehicleType { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("zone")]
    public string? Zone { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("lat")]
    public double? Lat { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("lng")]
    public double? Lng { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("last_seen_at")]
    public DateTimeOffset? LastSeenAt { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Maps the delivery-service <c>GET /api/v1/shipments</c> response envelope.
/// Matches the Go struct <c>ShipmentsListResponse</c> in
/// <c>delivery-service/internal/api/models.go</c>.
/// </summary>
public sealed class ShipmentsListDto
{
    public IReadOnlyList<ShipmentDetailDto> Shipments { get; init; } = Array.Empty<ShipmentDetailDto>();
    public int Count { get; init; }
}

/// <summary>
/// Maps a single element from the <c>shipments</c> array returned by
/// delivery-service. Only the fields the gateway exposes downstream are
/// represented here; unmapped fields are silently dropped by STJ.
/// </summary>
public sealed class ShipmentDetailDto
{
    public required string Id { get; init; }
    public string? TenantId { get; init; }
    public string? OrderId { get; init; }
    public string? WorkflowId { get; init; }
    public int WorkflowVersion { get; init; }
    public required string CurrentStage { get; init; }
    public DateTimeOffset StageEnteredAt { get; init; }
    public string? CarrierName { get; init; }
    public string? CarrierTrackingId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
