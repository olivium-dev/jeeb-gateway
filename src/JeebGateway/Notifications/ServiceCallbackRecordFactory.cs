using System.Globalization;

namespace JeebGateway.Notifications;

/// <summary>
/// b02 step 6a — turns an inbound <c>POST /svc-callbacks/notify</c> into the right typed
/// notification-centre record and hands it to <see cref="INotificationRecordWriter"/>.
///
/// <para><b>Why this exists at all.</b> Step 6a's deliverable is not six methods, it is six inbox
/// categories that stop being dead taxonomy — and a writer with no caller produces no row, which is
/// the same "publisher with no subscriber" failure the execution plan warns about: it reviews clean
/// and does nothing. <c>/svc-callbacks/notify</c> is the correct and only caller: all six of these
/// types are microservice-originated by definition (delivery, settlement, KYC, dispute, rating are
/// owned by other services), and that endpoint is the inbound door those services use under push
/// architecture rule 3.</para>
///
/// <para><b>Offer callback types are deliberately NOT handled here.</b>
/// <c>jeeb.offer_received</c> and <c>jeeb.offer_accepted</c> already have live centre writers at
/// their real in-gateway seats (<see cref="OfferPushNotifier"/>), which read authoritative amounts
/// and addresses from the request store. Minting them from a caller-supplied flat map as well would
/// put two producers on one taxonomy and — because the centre does NOT deduplicate on
/// <c>notification_id</c> (measured for FM-1, see <see cref="NotificationCorrelationId"/>) —
/// produce two inbox rows for one event. So this factory answers <c>false</c> for them and the
/// callback stays push-only, exactly as it behaved before step 6a.</para>
///
/// <para><b>Absence handling.</b> The callback carries <c>data</c> as a flat string map; the centre
/// payloads are closed and typed. Every required string the caller omits becomes
/// <see cref="JeebNotificationCentre.Absent"/> and is counted in
/// <c>notif.durable_write.field_absent</c>, so a thin inbox row is visible in telemetry instead of
/// looking like real data. Numbers cannot carry that sentinel; they default to 0 and are counted
/// too. Nothing here invents a value that could be mistaken for authoritative.</para>
/// </summary>
public static class ServiceCallbackRecordFactory
{
    /// <summary>The <c>sender</c> stamped on every row this factory produces.</summary>
    public const string Sender = "jeeb-gateway";

    /// <summary>
    /// True when <paramref name="templateKey"/> is one of the six types this factory can write.
    /// Used by the callback endpoint to decide whether a centre write is even attempted, so the
    /// offer types and any future push-only type are not silently dropped into a default.
    /// </summary>
    public static bool CanWrite(string? templateKey) => templateKey switch
    {
        DeliveryStatusUpdatedNotificationRecord.TemplateKey => true,
        SettlementPaidNotificationRecord.TemplateKey => true,
        KycApprovedNotificationRecord.TemplateKey => true,
        KycRejectedNotificationRecord.TemplateKey => true,
        DisputeResolvedNotificationRecord.TemplateKey => true,
        RatingAutoRevealedNotificationRecord.TemplateKey => true,
        _ => false,
    };

    /// <summary>
    /// Build the typed record for <paramref name="templateKey"/> and write it. Returns the writer's
    /// classification, or <c>null</c> when the type has no centre writer (see
    /// <see cref="CanWrite"/>). The silent gate is NOT re-implemented here — every path below goes
    /// through <see cref="INotificationRecordWriter"/>, which is where step 3 enforces it.
    /// </summary>
    public static Task<NotificationRecordWriteOutcome>? WriteAsync(
        INotificationRecordWriter writer,
        string templateKey,
        string recipientUserId,
        NotificationTemplate template,
        IReadOnlyDictionary<string, string>? data,
        string notificationCorrelationId,
        CancellationToken ct)
    {
        var createdAt = ReadTimestamp(data, templateKey);

        return templateKey switch
        {
            DeliveryStatusUpdatedNotificationRecord.TemplateKey => writer.WriteDeliveryStatusUpdatedAsync(
                new DeliveryStatusUpdatedNotificationRecord
                {
                    Sender = Sender,
                    Receiver = recipientUserId,
                    NotificationCorrelationId = notificationCorrelationId,
                    Title = template.Title,
                    Description = template.Body,
                    NotificationType = templateKey,
                    Payload = new DeliveryStatusUpdatedNotificationPayload
                    {
                        UserId = recipientUserId,
                        DeliveryId = Text(data, templateKey, "delivery_id", "deliveryId"),
                        OrderId = Text(data, templateKey, "order_id", "orderId"),
                        PreviousStatus = Text(data, templateKey, "previous_status", "previousStatus"),
                        CurrentStatus = Text(data, templateKey, "current_status", "currentStatus"),
                        StatusMessage = Text(data, templateKey, "status_message", "statusMessage"),
                        EstimatedArrival = Text(data, templateKey, "estimated_arrival", "estimatedArrival"),
                        CreatedAt = createdAt,
                    },
                },
                ct),

            SettlementPaidNotificationRecord.TemplateKey => writer.WriteSettlementPaidAsync(
                new SettlementPaidNotificationRecord
                {
                    Sender = Sender,
                    Receiver = recipientUserId,
                    NotificationCorrelationId = notificationCorrelationId,
                    Title = template.Title,
                    Description = template.Body,
                    NotificationType = templateKey,
                    Payload = new SettlementPaidNotificationPayload
                    {
                        UserId = recipientUserId,
                        SettlementId = Text(data, templateKey, "settlement_id", "settlementId"),
                        PaymentAmount = Number(data, templateKey, "payment_amount", "paymentAmount"),
                        Currency = Text(data, templateKey, "currency"),
                        PaymentMethod = Text(data, templateKey, "payment_method", "paymentMethod"),
                        TransactionId = Text(data, templateKey, "transaction_id", "transactionId"),
                        CreatedAt = createdAt,
                    },
                },
                ct),

            KycApprovedNotificationRecord.TemplateKey => writer.WriteKycApprovedAsync(
                new KycApprovedNotificationRecord
                {
                    Sender = Sender,
                    Receiver = recipientUserId,
                    NotificationCorrelationId = notificationCorrelationId,
                    Title = template.Title,
                    Description = template.Body,
                    NotificationType = templateKey,
                    Payload = new KycApprovedNotificationPayload
                    {
                        UserId = recipientUserId,
                        KycId = Text(data, templateKey, "kyc_id", "kycId"),
                        VerificationLevel = Text(data, templateKey, "verification_level", "verificationLevel"),
                        ApprovedDocuments = List(data, "approved_documents", "approvedDocuments"),
                        ApprovedBy = Text(data, templateKey, "approved_by", "approvedBy"),
                        CreatedAt = createdAt,
                    },
                },
                ct),

            KycRejectedNotificationRecord.TemplateKey => writer.WriteKycRejectedAsync(
                new KycRejectedNotificationRecord
                {
                    Sender = Sender,
                    Receiver = recipientUserId,
                    NotificationCorrelationId = notificationCorrelationId,
                    Title = template.Title,
                    Description = template.Body,
                    NotificationType = templateKey,
                    Payload = new KycRejectedNotificationPayload
                    {
                        UserId = recipientUserId,
                        KycId = Text(data, templateKey, "kyc_id", "kycId"),
                        RejectionReason = Text(data, templateKey, "rejection_reason", "rejectionReason"),
                        RequiredDocuments = List(data, "required_documents", "requiredDocuments"),
                        RejectionDetails = Text(data, templateKey, "rejection_details", "rejectionDetails"),
                        // Default true — see the property doc: wrongly telling a user they may not
                        // resubmit is a dead end, wrongly telling them they may is correctable.
                        ResubmissionAllowed = Flag(
                            data, "resubmission_allowed", "resubmissionAllowed", defaultValue: true),
                        CreatedAt = createdAt,
                    },
                },
                ct),

            DisputeResolvedNotificationRecord.TemplateKey => writer.WriteDisputeResolvedAsync(
                new DisputeResolvedNotificationRecord
                {
                    Sender = Sender,
                    Receiver = recipientUserId,
                    NotificationCorrelationId = notificationCorrelationId,
                    Title = template.Title,
                    Description = template.Body,
                    NotificationType = templateKey,
                    Payload = new DisputeResolvedNotificationPayload
                    {
                        UserId = recipientUserId,
                        DisputeId = Text(data, templateKey, "dispute_id", "disputeId"),
                        OrderId = Text(data, templateKey, "order_id", "orderId"),
                        ResolutionType = Text(data, templateKey, "resolution_type", "resolutionType"),
                        ResolutionAmount = Number(data, templateKey, "resolution_amount", "resolutionAmount"),
                        ResolutionDetails = Text(data, templateKey, "resolution_details", "resolutionDetails"),
                        ResolvedBy = Text(data, templateKey, "resolved_by", "resolvedBy"),
                        CreatedAt = createdAt,
                    },
                },
                ct),

            RatingAutoRevealedNotificationRecord.TemplateKey => writer.WriteRatingAutoRevealedAsync(
                new RatingAutoRevealedNotificationRecord
                {
                    Sender = Sender,
                    Receiver = recipientUserId,
                    NotificationCorrelationId = notificationCorrelationId,
                    Title = template.Title,
                    Description = template.Body,
                    NotificationType = templateKey,
                    Payload = new RatingAutoRevealedNotificationPayload
                    {
                        UserId = recipientUserId,
                        DeliveryId = Text(data, templateKey, "delivery_id", "deliveryId"),
                        OrderId = Text(data, templateKey, "order_id", "orderId"),
                        RatingValue = Number(data, templateKey, "rating_value", "ratingValue"),
                        RatingType = Text(data, templateKey, "rating_type", "ratingType"),
                        AutoRevealReason = Text(data, templateKey, "auto_reveal_reason", "autoRevealReason"),
                        CreatedAt = createdAt,
                    },
                },
                ct),

            // No writer for this type — the caller checks CanWrite first, so reaching here means a
            // push-only type. Not an error.
            _ => null,
        };
    }

    /// <summary>
    /// A required string from <c>data</c>, accepting the snake and camel spellings a caller might
    /// send. Absent/blank ⇒ <see cref="JeebNotificationCentre.Absent"/> plus a
    /// <c>notif.durable_write.field_absent</c> count, so a thin row is visible rather than
    /// indistinguishable from real data.
    /// </summary>
    private static string Text(
        IReadOnlyDictionary<string, string>? data,
        string templateKey,
        params string[] keys)
    {
        var raw = Raw(data, keys);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw.Trim();
        }

        NotificationDurableWriteTelemetry.FieldAbsent.Add(
            1,
            new("field", keys[0]),
            new("templateKey", templateKey));
        return JeebNotificationCentre.Absent;
    }

    /// <summary>
    /// A required number from <c>data</c>. Invariant culture on purpose: this is a wire value, and
    /// parsing "1,5" as fifteen because of a server locale is the kind of money bug that never
    /// shows up in review. Absent or unparseable ⇒ 0, counted as absent.
    /// </summary>
    private static decimal Number(
        IReadOnlyDictionary<string, string>? data,
        string templateKey,
        params string[] keys)
    {
        var raw = Raw(data, keys);
        if (!string.IsNullOrWhiteSpace(raw)
            && decimal.TryParse(
                raw,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        NotificationDurableWriteTelemetry.FieldAbsent.Add(
            1,
            new("field", keys[0]),
            new("templateKey", templateKey));
        return 0m;
    }

    /// <summary>
    /// A boolean from <c>data</c>. An unparseable value falls back to
    /// <paramref name="defaultValue"/> rather than to <c>false</c>: "false" is a real claim about
    /// the world and must not be manufactured out of a typo.
    /// </summary>
    private static bool Flag(
        IReadOnlyDictionary<string, string>? data,
        string snake,
        string camel,
        bool defaultValue)
    {
        var raw = Raw(data, snake, camel);
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    /// <summary>
    /// A string list from <c>data</c>, comma-separated (the flat map cannot carry an array). Absent
    /// ⇒ empty, and NOT counted as absent: the centre schema requires the field but an empty
    /// document list is a legitimate value, so counting it would make the absence metric noisy.
    /// </summary>
    private static IReadOnlyList<string> List(
        IReadOnlyDictionary<string, string>? data,
        string snake,
        string camel)
    {
        var raw = Raw(data, snake, camel);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    /// <summary>
    /// The row's <c>created_at</c>. A caller-supplied timestamp wins because the upstream event may
    /// predate this callback (retries, queue lag) and the inbox orders on it. Absent/unparseable ⇒
    /// now, counted as absent so a systematically missing timestamp is visible.
    /// </summary>
    private static DateTimeOffset ReadTimestamp(
        IReadOnlyDictionary<string, string>? data,
        string templateKey)
    {
        var raw = Raw(data, "created_at", "createdAt", "occurred_at", "occurredAt");
        if (!string.IsNullOrWhiteSpace(raw)
            && DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        NotificationDurableWriteTelemetry.FieldAbsent.Add(
            1,
            new("field", "created_at"),
            new("templateKey", templateKey));
        return DateTimeOffset.UtcNow;
    }

    private static string? Raw(IReadOnlyDictionary<string, string>? data, params string[] keys)
    {
        if (data is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
