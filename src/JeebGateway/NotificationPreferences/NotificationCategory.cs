namespace JeebGateway.NotificationPreferences;

/// <summary>
/// Categories the user can toggle. Critical channels (OTP, system_critical, kyc, disputes)
/// are intentionally not toggleable — they are always-on per AlwaysOnChannels.
/// </summary>
public enum NotificationCategory
{
    Offers,
    Chat,
    StatusChanges,
    RatingReminders,
    Promotions,
    /// <summary>Settlement/payment notifications (jeeb.settlement_paid, JEB-1498).</summary>
    Settlements
}
