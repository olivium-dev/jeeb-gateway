namespace JeebGateway.NotificationPreferences;

public class NotificationPreferencesResponse
{
    public required string UserId { get; init; }
    public required NotificationPreferencesCategoryToggles Preferences { get; init; }
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
/// PATCH body. Transactional channels (otp, systemCritical, kyc, disputes) cannot be
/// disabled; sending false returns 400.
/// </summary>
public class NotificationPreferencesPatchRequest
{
    public bool? Offers { get; set; }
    public bool? Chat { get; set; }
    public bool? StatusChanges { get; set; }
    public bool? RatingReminders { get; set; }
    public bool? Promotions { get; set; }
    public bool? Settlements { get; set; }
    public bool? Otp { get; set; }
    public bool? SystemCritical { get; set; }
    public bool? Kyc { get; set; }
    public bool? Disputes { get; set; }
}
