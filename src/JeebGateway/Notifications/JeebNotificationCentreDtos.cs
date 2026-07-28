using System.Text.Json.Serialization;

namespace JeebGateway.Notifications;

/// <summary>
/// b02 step 6a — the six notification-centre wire DTOs that had no writer.
///
/// <para><b>Where these shapes come from.</b> Not from the target doc and not invented here: each
/// record was transcribed field-by-field from the LIVE notification centre's own
/// <c>GET :10026/openapi.json</c> on 2026-07-26. Every property below is in that schema's
/// <c>required</c> list, which is why none of them is optional here. Probing
/// <c>POST :10026/notifications/&lt;type&gt;</c> with <c>{}</c> returns a pydantic 422 naming exactly
/// the envelope fields <c>sender, receiver, title, subtitle, description, notification_type,
/// payload</c> — so the envelope is uniform across types and only <c>payload</c> differs. That is
/// the reason for <see cref="JeebNotificationRecordEnvelope{TPayload}"/>: one envelope, six payloads.
/// The two pre-existing records (<see cref="OfferReceivedNotificationRecord"/>,
/// <see cref="OfferAcceptedNotificationRecord"/>) are deliberately left alone — rewriting live,
/// proven wire DTOs to share this base would be a behavioural risk for zero user benefit.</para>
///
/// <para><b>The payloads are CLOSED.</b> The centre's per-type payload schemas name a fixed field
/// set; there is no object-typed escape hatch and none is added here. A caller-supplied value that
/// does not map onto one of these fields does not reach the centre. That is intentional: an inbox
/// row is level-held state a user reads days later, so its shape is a contract, not a bag.</para>
///
/// <para><b>Absence is explicit, never silently zero.</b> Several of these fields are authoritative
/// data the gateway does not own (a settlement's <c>transaction_id</c>, a KYC decision's
/// <c>approved_by</c>). When a caller omits one, the builder in
/// <c>ServiceCallbacksController</c> stamps <see cref="Absent"/> and increments
/// <c>notif.durable_write.field_absent</c> rather than writing an empty string that reads as real
/// data in the inbox. Numeric and boolean fields cannot carry that sentinel, so their defaults are
/// documented at each property instead.</para>
/// </summary>
public static class JeebNotificationCentre
{
    /// <summary>
    /// The owning-store absence sentinel for a required STRING field the caller did not supply.
    /// Chosen to be obviously non-data in a UI so an operator reading the inbox sees "the upstream
    /// did not send this", not a plausible blank. Mirrors the intent of the existing
    /// <c>notif.durable_write.field_absent</c> telemetry at the offer seats.
    /// </summary>
    public const string Absent = "unknown";
}

/// <summary>
/// The envelope every notification-centre create shares (verified identical across all eight served
/// <c>jeeb.*</c> types). <typeparamref name="TPayload"/> is the closed per-type payload.
/// </summary>
/// <remarks>
/// <c>subtitle</c> is REQUIRED by the centre (its 422 names it) even though it carries no meaning
/// for most Jeeb types; it defaults to empty rather than being duplicated from the title, because a
/// duplicated title renders as a stutter in the inbox row.
/// </remarks>
public abstract record JeebNotificationRecordEnvelope<TPayload>
    where TPayload : class
{
    public required string Sender { get; init; }

    public required string Receiver { get; init; }

    /// <summary>
    /// The gateway-minted correlation id. Serialized as <c>notification_id</c> because that is the
    /// field <see cref="JeebNotificationRecordClient.ContainsCorrelationIdAsync"/> reads back to
    /// classify an ambiguous POST — the two must not drift.
    /// </summary>
    [JsonPropertyName("notification_id")]
    public required string NotificationCorrelationId { get; init; }

    public required string Title { get; init; }

    public string Subtitle { get; init; } = string.Empty;

    public required string Description { get; init; }

    [JsonPropertyName("notification_type")]
    public required string NotificationType { get; init; }

    public required TPayload Payload { get; init; }

    [JsonPropertyName("senderProfilePicture")]
    public string SenderProfilePicture { get; init; } = string.Empty;

    public string Nickname { get; init; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// jeeb.delivery_status_updated
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Wire DTO for <c>jeeb.delivery_status_updated</c>.
///
/// <para><b>⚠️ THE "GATED SILENT, NO ROW TODAY" CLAIM THAT USED TO BE HERE IS RETRACTED.</b>
/// It described owner ruling D4 (2026-07-26), which put the <c>delivery</c> refresh category on
/// the silent-only side. That was <b>REVERSED on 2026-07-27</b>: delivery is <b>shade + stored</b>,
/// and <see cref="PushSilencePolicy"/> maps this template key to
/// <see cref="PushDeliveryMode.ShadeAndStored"/> (see <c>ModeByCategory</c>, where
/// <c>CategoryDelivery</c> sits in the stored block). <see cref="NotificationRecordWriter"/> does
/// NOT short-circuit this type — it POSTs. Resolve the symbol before believing any comment on this
/// subject: the retracted sentence outlived the ruling it described, which is how a live writer
/// gets read as dormant.</para>
///
/// <para>If a specific delivery MILESTONE ever warrants different treatment from the rest of the
/// category, that is a new notification TYPE of its own per the corollary documented in
/// <see cref="PushSilencePolicy"/>, and it needs an owner decision first.</para>
/// </summary>
public sealed record DeliveryStatusUpdatedNotificationRecord
    : JeebNotificationRecordEnvelope<DeliveryStatusUpdatedNotificationPayload>
{
    public const string TemplateKey = "jeeb.delivery_status_updated";
}

/// <summary>The eight-field closed payload published by notification-service.</summary>
public sealed record DeliveryStatusUpdatedNotificationPayload
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("delivery_id")]
    public required string DeliveryId { get; init; }

    [JsonPropertyName("order_id")]
    public required string OrderId { get; init; }

    [JsonPropertyName("previous_status")]
    public required string PreviousStatus { get; init; }

    [JsonPropertyName("current_status")]
    public required string CurrentStatus { get; init; }

    [JsonPropertyName("status_message")]
    public required string StatusMessage { get; init; }

    [JsonPropertyName("estimated_arrival")]
    public required string EstimatedArrival { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// jeeb.settlement_paid
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Wire DTO for <c>jeeb.settlement_paid</c> (shade + stored per owner ruling D4).</summary>
public sealed record SettlementPaidNotificationRecord
    : JeebNotificationRecordEnvelope<SettlementPaidNotificationPayload>
{
    public const string TemplateKey = "jeeb.settlement_paid";
}

/// <summary>The seven-field closed payload published by notification-service.</summary>
public sealed record SettlementPaidNotificationPayload
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("settlement_id")]
    public required string SettlementId { get; init; }

    /// <summary>
    /// Money. A numeric field cannot carry the <see cref="JeebNotificationCentre.Absent"/>
    /// sentinel, so an omitted amount arrives as 0 — which in an inbox row reads as "you were paid
    /// nothing". The builder therefore logs <c>notif.durable_write.field_absent</c> for it, so a
    /// 0 that came from absence is distinguishable in telemetry from a genuine 0.
    /// </summary>
    [JsonPropertyName("payment_amount")]
    public required decimal PaymentAmount { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("payment_method")]
    public required string PaymentMethod { get; init; }

    [JsonPropertyName("transaction_id")]
    public required string TransactionId { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// jeeb.kyc_approved / jeeb.kyc_rejected
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Wire DTO for <c>jeeb.kyc_approved</c> (shade + stored per owner ruling D4).</summary>
public sealed record KycApprovedNotificationRecord
    : JeebNotificationRecordEnvelope<KycApprovedNotificationPayload>
{
    public const string TemplateKey = "jeeb.kyc_approved";
}

/// <summary>The six-field closed payload published by notification-service.</summary>
public sealed record KycApprovedNotificationPayload
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("kyc_id")]
    public required string KycId { get; init; }

    [JsonPropertyName("verification_level")]
    public required string VerificationLevel { get; init; }

    /// <summary>Required by the centre as an array; an omitted list is empty, not a sentinel.</summary>
    [JsonPropertyName("approved_documents")]
    public IReadOnlyList<string> ApprovedDocuments { get; init; } = Array.Empty<string>();

    [JsonPropertyName("approved_by")]
    public required string ApprovedBy { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Wire DTO for <c>jeeb.kyc_rejected</c> (shade + stored per owner ruling D4).</summary>
public sealed record KycRejectedNotificationRecord
    : JeebNotificationRecordEnvelope<KycRejectedNotificationPayload>
{
    public const string TemplateKey = "jeeb.kyc_rejected";
}

/// <summary>The seven-field closed payload published by notification-service.</summary>
public sealed record KycRejectedNotificationPayload
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("kyc_id")]
    public required string KycId { get; init; }

    [JsonPropertyName("rejection_reason")]
    public required string RejectionReason { get; init; }

    [JsonPropertyName("required_documents")]
    public IReadOnlyList<string> RequiredDocuments { get; init; } = Array.Empty<string>();

    [JsonPropertyName("rejection_details")]
    public required string RejectionDetails { get; init; }

    /// <summary>
    /// Defaults to <c>true</c> when the caller omits it. A boolean cannot carry an absence
    /// sentinel, and of the two possible defaults only this one is safe: telling a user they may
    /// resubmit when they may not is a correctable annoyance, whereas telling them they may NOT
    /// resubmit when they may is a dead end that loses a verified user.
    /// </summary>
    [JsonPropertyName("resubmission_allowed")]
    public required bool ResubmissionAllowed { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// jeeb.dispute_resolved
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Wire DTO for <c>jeeb.dispute_resolved</c> (shade + stored per owner ruling D4).</summary>
public sealed record DisputeResolvedNotificationRecord
    : JeebNotificationRecordEnvelope<DisputeResolvedNotificationPayload>
{
    public const string TemplateKey = "jeeb.dispute_resolved";
}

/// <summary>The eight-field closed payload published by notification-service.</summary>
public sealed record DisputeResolvedNotificationPayload
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("dispute_id")]
    public required string DisputeId { get; init; }

    [JsonPropertyName("order_id")]
    public required string OrderId { get; init; }

    [JsonPropertyName("resolution_type")]
    public required string ResolutionType { get; init; }

    /// <summary>
    /// Money, same absence caveat as <see cref="SettlementPaidNotificationPayload.PaymentAmount"/>:
    /// an omitted amount arrives as 0 and is counted in <c>notif.durable_write.field_absent</c>.
    /// This record does NOT move money — it reports a resolution the dispute owner already
    /// committed. Nothing here is a payments route.
    /// </summary>
    [JsonPropertyName("resolution_amount")]
    public required decimal ResolutionAmount { get; init; }

    [JsonPropertyName("resolution_details")]
    public required string ResolutionDetails { get; init; }

    [JsonPropertyName("resolved_by")]
    public required string ResolvedBy { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// jeeb.rating_auto_revealed
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Wire DTO for <c>jeeb.rating_auto_revealed</c> (shade + stored per owner ruling D4).</summary>
public sealed record RatingAutoRevealedNotificationRecord
    : JeebNotificationRecordEnvelope<RatingAutoRevealedNotificationPayload>
{
    public const string TemplateKey = "jeeb.rating_auto_revealed";
}

/// <summary>The seven-field closed payload published by notification-service.</summary>
public sealed record RatingAutoRevealedNotificationPayload
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("delivery_id")]
    public required string DeliveryId { get; init; }

    [JsonPropertyName("order_id")]
    public required string OrderId { get; init; }

    /// <summary>
    /// The revealed rating. Absent ⇒ 0, and counted in <c>notif.durable_write.field_absent</c>:
    /// 0 is outside the product's 1–5 scale, so an absent value is visibly not a real rating
    /// rather than silently reading as the worst possible one.
    /// </summary>
    [JsonPropertyName("rating_value")]
    public required decimal RatingValue { get; init; }

    [JsonPropertyName("rating_type")]
    public required string RatingType { get; init; }

    [JsonPropertyName("auto_reveal_reason")]
    public required string AutoRevealReason { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }
}
