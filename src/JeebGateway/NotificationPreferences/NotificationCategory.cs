namespace JeebGateway.NotificationPreferences;

/// <summary>
/// Categories the user can toggle. Critical channels (OTP, system_critical) are
/// intentionally not part of this enum — they are always-on and live in
/// <see cref="NotificationPreferencesDefaults.AlwaysOnChannels"/>.
/// </summary>
public enum NotificationCategory
{
    Offers,
    Chat,
    StatusChanges,
    RatingReminders,
    Promotions
}
