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
}

/// <summary>
/// PATCH body — every category is optional; unspecified fields are not modified.
/// OTP / system_critical are intentionally NOT accepted here; sending them returns 400.
/// </summary>
public class NotificationPreferencesPatchRequest
{
    public bool? Offers { get; set; }
    public bool? Chat { get; set; }
    public bool? StatusChanges { get; set; }
    public bool? RatingReminders { get; set; }

    public bool? Otp { get; set; }
    public bool? SystemCritical { get; set; }
}
