namespace JeebGateway.Requests;

/// <summary>
/// Status of a delivery request. Mirrors the <c>delivery_request_status</c>
/// enum in db/migrations/0004 — order matters: anything before <c>delivered</c>
/// is "active" and counts against the per-Client concurrency cap (BR-9).
/// </summary>
public static class RequestStatus
{
    public const string Scheduled = "scheduled";
    public const string Pending = "pending";
    public const string Matched = "matched";
    public const string Accepted = "accepted";
    public const string PickedUp = "picked_up";
    public const string HeadingOff = "heading_off";

    /// <summary>
    /// T-BE-019 (JEB-55): the Jeeber has arrived at the drop-off and is
    /// physically at the recipient's door. The 4-digit external handover
    /// OTP can only be issued / verified while a delivery is in this state.
    /// Treated as ACTIVE for BR-9/BR-10 — the row is still in flight.
    /// </summary>
    public const string AtDoor = "at_door";

    public const string Delivered = "delivered";
    public const string Rated = "rated";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
    public const string Disputed = "disputed";

    /// <summary>
    /// T-backend-024 (JEEB-42): non-terminal holding state used when a
    /// Client cancels after pickup. The row is parked here until an admin
    /// approves (→ <see cref="Cancelled"/>) or rejects (→ the previous
    /// status). Treated as ACTIVE for BR-9/BR-10 purposes — the Jeeber is
    /// still on the hook until the admin signs off.
    /// </summary>
    public const string CancellationRequested = "cancellation_requested";

    /// <summary>
    /// The active states from BR-9: every status strictly before
    /// <see cref="Delivered"/>. <see cref="Cancelled"/> and <see cref="Expired"/>
    /// are terminal and do NOT count against the limit. <see cref="Scheduled"/>
    /// is included so a Client cannot trivially bypass the cap by stacking
    /// future-dated requests (T-backend-046).
    /// </summary>
    public static readonly IReadOnlySet<string> ActiveStates = new HashSet<string>(StringComparer.Ordinal)
    {
        Scheduled,
        Pending,
        Matched,
        Accepted,
        PickedUp,
        HeadingOff,
        AtDoor,
        // Cancellation pending admin sign-off: the row is not yet terminal
        // and the Jeeber still owns it for capacity-cap purposes.
        CancellationRequested
    };

    /// <summary>
    /// BR-10 active states from the Jeeber's perspective: every status the
    /// Jeeber is on the hook for once an offer has been accepted. Pre-accept
    /// states (<c>scheduled</c>, <c>pending</c>, <c>matched</c>) are not in
    /// this set because no Jeeber is bound to the request yet — the BR-10
    /// cap only kicks in at acceptance time and only counts deliveries the
    /// Jeeber is actively running.
    /// </summary>
    public static readonly IReadOnlySet<string> JeeberActiveStates = new HashSet<string>(StringComparer.Ordinal)
    {
        Accepted,
        PickedUp,
        HeadingOff,
        AtDoor,
        // While an admin reviews a Client-requested cancellation the
        // delivery is still bound to the Jeeber — counting it against
        // BR-10 prevents the Jeeber from accepting fresh work that they
        // may have to drop again when the admin rejects.
        CancellationRequested
    };

    /// <summary>
    /// Statuses where the Client has not yet accepted any offer
    /// (T-backend-028 expiry-window scope). Strictly the two "auction
    /// open" states — <c>matched</c> means candidate Jeebers were
    /// notified but no offer has been accepted.
    /// </summary>
    public static readonly IReadOnlySet<string> PreAcceptanceStates = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Matched
    };

    /// <summary>
    /// Terminal statuses — once a request lands here it MUST NOT
    /// transition further. Mirrors the (terminal) markers in the
    /// 0004 migration's enum comments.
    /// </summary>
    public static readonly IReadOnlySet<string> TerminalStates = new HashSet<string>(StringComparer.Ordinal)
    {
        Delivered,
        Rated,
        Cancelled,
        Expired,
        Disputed
    };

    public static bool IsActive(string status) => ActiveStates.Contains(status);
    public static bool IsPreAcceptance(string status) => PreAcceptanceStates.Contains(status);
    public static bool IsTerminal(string status) => TerminalStates.Contains(status);
    public static bool IsJeeberActive(string status) => JeeberActiveStates.Contains(status);
}

/// <summary>
/// WGS84 lon/lat pair. Mirrors the GEOGRAPHY(Point, 4326) columns in
/// db/migrations/0004 — radius math at the matching layer is in metres.
/// </summary>
public class GeoPoint
{
    public required double Lat { get; init; }
    public required double Lng { get; init; }

    public bool IsValid() =>
        Lat is >= -90 and <= 90 && Lng is >= -180 and <= 180
        && !double.IsNaN(Lat) && !double.IsNaN(Lng);
}

public class DeliveryRequest
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }
    public required string Status { get; set; }
    public required string Description { get; init; }

    /// <summary>
    /// Raw speech-to-text output preserved alongside the user-edited
    /// <see cref="Description"/> so admins can audit how the final text
    /// diverged from what was dictated (FR-3.4).
    /// </summary>
    public string? Transcription { get; init; }

    /// <summary>
    /// S04 (JEB-43): STT confidence (0..1) from the voice-create. Null on the
    /// typed-text path. Persisted so the read-back echoes the same value.
    /// </summary>
    public double? TranscriptionConfidence { get; init; }

    /// <summary>
    /// Object-storage URL of the original voice recording. NULL when the
    /// Client typed the description rather than recording it.
    /// </summary>
    public string? AudioUrl { get; init; }

    /// <summary>
    /// Optional photos attached at creation (parcel snapshots, label
    /// shots). Each entry is an object-storage URL; the gateway validates
    /// the protocol shape only — content moderation runs in the
    /// prohibited-items scanner pipeline.
    /// </summary>
    public IReadOnlyList<string> Photos { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Selected tier code (flash / express / standard / on_the_way / eco).
    /// Validated against the active tier catalog at creation. Maps to
    /// <c>delivery_tiers.code</c> in the DB; production wiring resolves
    /// code → UUID before persisting.
    /// </summary>
    public string? TierId { get; init; }

    public GeoPoint? PickupLocation { get; init; }
    public GeoPoint? DropoffLocation { get; init; }
    public string? PickupAddress { get; init; }
    public string? DropoffAddress { get; init; }

    /// <summary>
    /// T-BE-019 (JEB-55): E.164 phone number that receives the 4-digit
    /// handover OTP via the external one-time-password service. Distinct
    /// from <c>ClientId</c> because the recipient at drop-off may be a
    /// different person than the requester (e.g. a gift). Null until set
    /// by the request-creation flow; the OTP trigger endpoint returns 400
    /// when missing so a hardcoded placeholder phone is never used.
    /// </summary>
    public string? RecipientPhone { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the Client wants the delivery to happen (T-backend-046, Phase 2).
    /// Null for an immediate delivery — matching kicks off at creation. When
    /// set, the request enters <see cref="RequestStatus.Scheduled"/> and the
    /// <c>ScheduledDeliveryActivator</c> flips it to <see cref="RequestStatus.Pending"/>
    /// at <c>ScheduledAt - MatchingBuffer</c> (default 30 min before).
    /// </summary>
    public DateTimeOffset? ScheduledAt { get; init; }

    /// <summary>
    /// When the scheduled-delivery activator transitioned the request out of
    /// <see cref="RequestStatus.Scheduled"/> and fired the matching window
    /// reminder. Idempotence guard for the activator — set once, never reset.
    /// </summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>
    /// When the no-offer "try expanding tier" prompt was sent for this
    /// request (T-backend-028). The expiry sweeper sets this once and
    /// then refuses to re-fire so the Client doesn't receive a duplicate
    /// push if the sweeper runs at a high cadence.
    /// </summary>
    public DateTimeOffset? NudgedAt { get; set; }

    /// <summary>
    /// When the request was terminally expired by the sweeper. Null until
    /// the 30-min window elapses without an accepted offer.
    /// </summary>
    public DateTimeOffset? ExpiredAt { get; set; }

    /// <summary>
    /// The Jeeber currently fulfilling this request (BR-10, T-backend-039).
    /// Null until an offer has been accepted. Once set, the request counts
    /// against the Jeeber's <see cref="JeeberActiveStates"/> cap until the
    /// status leaves that set (delivered / rated / cancelled / expired /
    /// disputed).
    /// </summary>
    public string? JeeberId { get; set; }

    /// <summary>
    /// When the offer was accepted and <see cref="JeeberId"/> was bound to
    /// the request. Set in the same atomic transition as the
    /// <c>matched/pending → accepted</c> flip.
    /// </summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>
    /// One-time code the Client presents to the Jeeber at hand-off
    /// (T-backend-013). Issued by the store when the row enters
    /// <see cref="RequestStatus.Accepted"/> and consumed on the
    /// <c>heading_off → delivered</c> transition — the patch endpoint
    /// rejects with 400 when the supplied OTP does not match.
    /// </summary>
    public string? DeliveryOtp { get; set; }

    /// <summary>
    /// Cumulative count of incorrect OTP submissions against
    /// POST /deliveries/{id}/verify-otp (T-backend-015). Successful
    /// verification resets the counter implicitly by transitioning the
    /// row out of the handover window. Reaching
    /// <c>OtpHandoverOptions.MaxAttempts</c> locks the OTP and creates
    /// an admin escalation entry.
    /// </summary>
    public int OtpAttemptCount { get; set; }

    /// <summary>
    /// Wall-clock moment the OTP became locked after
    /// <c>OtpHandoverOptions.MaxAttempts</c> consecutive bad submissions
    /// (T-backend-015). Null while OTP entry is still permitted. Once
    /// stamped, every subsequent POST /verify-otp returns 423 Locked.
    /// </summary>
    public DateTimeOffset? OtpLockedAt { get; set; }

    /// <summary>
    /// When the Jeeber flagged the Client as unreachable at drop-off
    /// (T-backend-015). The <c>OtpHandoverSweeper</c> escalates the
    /// delivery to an admin once
    /// <c>now - ClientUnreachableAt &gt;= OtpHandoverOptions.ClientUnreachableWindow</c>
    /// (default 15 minutes).
    /// </summary>
    public DateTimeOffset? ClientUnreachableAt { get; set; }

    /// <summary>
    /// Id of the admin escalation row that owns this delivery's
    /// hand-off dispute (T-backend-015). Set the first time either the
    /// OTP locks out or the unreachable timer elapses; the field is
    /// write-once so the sweeper cannot create duplicate escalations
    /// when it polls.
    /// </summary>
    public string? OtpEscalationId { get; set; }

    /// <summary>
    /// Whether the row's GPS-tracking requirement is active
    /// (T-backend-013). Flipped true when the state machine moves the
    /// row into <see cref="RequestStatus.PickedUp"/>; downstream
    /// telemetry uses this as the gate to start ingesting Jeeber
    /// location updates.
    /// </summary>
    public bool GpsTrackingActive { get; set; }

    /// <summary>
    /// T-backend-024 (JEEB-42): who initiated the cancellation.
    /// "client" or "jeeber". Null until a cancellation lands.
    /// </summary>
    public string? CancelledBy { get; set; }

    /// <summary>
    /// T-backend-024: reason supplied with the cancellation. Mandatory
    /// for Jeeber cancellations; optional for Client cancellations.
    /// Surfaced on the admin queue when a post-pickup Client cancel
    /// gets escalated.
    /// </summary>
    public string? CancellationReason { get; set; }

    /// <summary>
    /// T-backend-024: when the cancellation was requested. For pre-pickup
    /// Client cancels and all Jeeber cancels this equals the moment the
    /// row went terminal; for post-pickup Client cancels it's the moment
    /// the row entered <see cref="RequestStatus.CancellationRequested"/>.
    /// </summary>
    public DateTimeOffset? CancellationRequestedAt { get; set; }

    /// <summary>
    /// T-backend-024: when an admin approved a post-pickup Client cancel
    /// and the row transitioned to <see cref="RequestStatus.Cancelled"/>.
    /// </summary>
    public DateTimeOffset? CancellationApprovedAt { get; set; }

    /// <summary>
    /// T-backend-024: when an admin rejected a post-pickup Client cancel
    /// and reverted the row to <see cref="CancellationPreviousStatus"/>.
    /// </summary>
    public DateTimeOffset? CancellationRejectedAt { get; set; }

    /// <summary>
    /// T-backend-024: status the row held immediately before the
    /// cancellation request. Used to revert the row on admin reject so
    /// the delivery resumes from exactly where it left off.
    /// </summary>
    public string? CancellationPreviousStatus { get; set; }
}

public class CreateRequestBody
{
    public string? Description { get; set; }

    /// <summary>
    /// Raw STT output preserved for audit. The Client edits this on the
    /// confirmation screen — the edited text lands in <see cref="Description"/>.
    /// </summary>
    public string? Transcription { get; set; }

    /// <summary>
    /// Object-storage URL of the original voice recording. MUST start with
    /// https://, http://, or s3:// — mirrors the DB CHECK constraint.
    /// </summary>
    public string? AudioUrl { get; set; }

    /// <summary>
    /// Photos attached to the request. Each entry must be an absolute URL
    /// (https / http / s3). MVP cap: 10 photos per request.
    /// </summary>
    public List<string>? Photos { get; set; }

    /// <summary>
    /// Selected tier code (e.g. "flash", "express"). Required at creation
    /// per T-backend-007 — the single-screen tier picker means we always
    /// know the tier when the request lands.
    /// </summary>
    public string? TierId { get; set; }

    public GeoPoint? PickupLocation { get; set; }
    public GeoPoint? DropoffLocation { get; set; }
    public string? PickupAddress { get; set; }
    public string? DropoffAddress { get; set; }

    /// <summary>
    /// Optional. When set, the delivery is scheduled for the given moment;
    /// matching only starts at <c>ScheduledAt - MatchingBuffer</c>. Must be
    /// in the future. Absent / null means an immediate delivery (existing
    /// behavior — status starts as <c>pending</c>).
    /// </summary>
    public DateTimeOffset? ScheduledAt { get; set; }
}

public class DeliveryRequestDto
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }
    public required string Status { get; init; }
    public required string Description { get; init; }
    public string? Transcription { get; init; }
    public string? AudioUrl { get; init; }
    public IReadOnlyList<string> Photos { get; init; } = Array.Empty<string>();
    public string? TierId { get; init; }
    public GeoPoint? PickupLocation { get; init; }
    public GeoPoint? DropoffLocation { get; init; }
    public string? PickupAddress { get; init; }
    public string? DropoffAddress { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public string? JeeberId { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }

    /// <summary>
    /// True once the row has transitioned through <see cref="RequestStatus.PickedUp"/>
    /// (T-backend-013). Mobile clients show the live-tracking indicator
    /// and begin streaming Jeeber location updates when this flips.
    /// </summary>
    public bool GpsTrackingActive { get; init; }

    /// <summary>
    /// OTP attempt count exposed so the mobile UI can render "2 of 3
    /// remaining" (T-backend-015).
    /// </summary>
    public int OtpAttemptCount { get; init; }

    /// <summary>
    /// Non-null when OTP entry is locked out and the row has been
    /// escalated to an admin (T-backend-015). The presence of this
    /// value gates the UI on the "contact support" CTA.
    /// </summary>
    public DateTimeOffset? OtpLockedAt { get; init; }

    /// <summary>
    /// Non-null when the Jeeber flagged the Client as unreachable
    /// (T-backend-015). Pair with <c>OtpEscalationId</c> to tell apart
    /// "timer running" from "already escalated".
    /// </summary>
    public DateTimeOffset? ClientUnreachableAt { get; init; }

    /// <summary>
    /// Non-null once the row has been escalated to an admin via either
    /// the OTP lockout path or the client-unreachable path
    /// (T-backend-015).
    /// </summary>
    public string? OtpEscalationId { get; init; }
}

/// <summary>
/// PATCH /deliveries/{id}/status body (T-backend-013). Clients hand in the
/// target status and, for the <c>heading_off → delivered</c> transition,
/// the OTP previously issued at accept time.
/// </summary>
public class PatchStatusBody
{
    public string? Status { get; set; }

    /// <summary>
    /// Required only when transitioning to <see cref="RequestStatus.Delivered"/>.
    /// Compared against the row's <see cref="DeliveryRequest.DeliveryOtp"/>;
    /// a missing or mismatched value rejects with 400.
    /// </summary>
    public string? Otp { get; set; }
}
