namespace JeebGateway.NotificationPreferences;

public class NotificationPreferencesResponse
{
    public required string UserId { get; init; }
    public required NotificationPreferencesCategoryToggles Preferences { get; init; }

    /// <summary>
    /// Channels the user cannot mute via this API. The notification-service must
    /// always deliver these regardless of <see cref="Preferences"/>.
    /// </summary>
    public required IReadOnlyList<string> AlwaysOn { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public class NotificationPreferencesCategoryToggles
{
    public required bool Offers { get; init; }
    public required bool Chat { get; init; }
    public required bool StatusChanges { get; init; }
    public required bool RatingReminders { get; init; }
    public required bool Promotions { get; init; }
    /// <summary>jeeb.settlement_paid notifications (JEB-1498).</summary>
    public required bool Settlements { get; init; }
}

/// <summary>
/// PATCH body — every category is optional; unspecified fields are not modified.
/// Transactional channels (otp, systemCritical, kyc, disputes) cannot be disabled;
/// sending false returns 400.
/// </summary>
public class NotificationPreferencesPatchRequest
{
    public bool? Offers { get; set; }
    public bool? Chat { get; set; }
    public bool? StatusChanges { get; set; }
    public bool? RatingReminders { get; set; }
    public bool? Promotions { get; set; }
    public bool? Settlements { get; set; }

    /// <summary>Always-on. Sending false returns 400.</summary>
    public bool? Otp { get; set; }
    /// <summary>Always-on. Sending false returns 400.</summary>
    public bool? SystemCritical { get; set; }
    /// <summary>Always-on (KYC alerts are regulatory/identity events). Sending false returns 400.</summary>
    public bool? Kyc { get; set; }
    /// <summary>Always-on (dispute updates are operational). Sending false returns 400.</summary>
    public bool? Disputes { get; set; }
}
