using JeebGateway.NotificationPreferences;

namespace JeebGateway.Push;

/// <summary>
/// Maps every <see cref="NotificationTrigger"/> to either a user-toggleable
/// <see cref="NotificationCategory"/> or to the always-on bucket. Always-on
/// triggers cannot be muted; see <see cref="NotificationPreferencesDefaults.AlwaysOnChannels"/>.
/// </summary>
public static class PushTriggerCategoryMap
{
    /// <summary>
    /// Returns the toggleable category for the trigger, or <c>null</c> when
    /// the trigger is always-on and must bypass preference filtering.
    /// </summary>
    public static NotificationCategory? CategoryFor(NotificationTrigger trigger) => trigger switch
    {
        NotificationTrigger.NewOffer => NotificationCategory.Offers,
        // BR-22a: "Acceptance" notifications are status changes from the
        // client's point of view — muting status changes mutes acceptance.
        NotificationTrigger.OfferAccepted => NotificationCategory.StatusChanges,
        NotificationTrigger.StatusChange => NotificationCategory.StatusChanges,
        NotificationTrigger.Chat => NotificationCategory.Chat,
        NotificationTrigger.RatingReminder => NotificationCategory.RatingReminders,
        NotificationTrigger.Promotion => NotificationCategory.Promotions,
        // KYC is a regulatory/identity event — always delivered, like OTP.
        NotificationTrigger.KycUpdate => null,
        // Auto-offline tells the Jeeber why their offer feed went silent;
        // muting it would leave them confused about lost earnings.
        NotificationTrigger.AutoOffline => null,
        // Rating reveal (T-backend-021) — same category as the daily
        // reminder so users who muted that channel are not pinged again.
        NotificationTrigger.RatingRevealed => NotificationCategory.RatingReminders,
        // Admin / moderation triggers (T-backend-040 / T-BE-028) — always
        // delivered. Low-rating flags and dispute updates are operational
        // events the participant needs to see regardless of preferences.
        NotificationTrigger.LowRatingFlag => null,
        NotificationTrigger.DisputeUpdate => null,
        // Settlement-paid is a financial event the Jeeber must always see
        // (their money moved) — always-on, like KYC (JEB-1476).
        NotificationTrigger.SettlementPaid => null,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown trigger")
    };

    public static bool IsAllowed(NotificationTrigger trigger, UserNotificationPreferences prefs)
    {
        var category = CategoryFor(trigger);
        if (category is null) return true; // always-on
        return category switch
        {
            NotificationCategory.Offers => prefs.Offers,
            NotificationCategory.Chat => prefs.Chat,
            NotificationCategory.StatusChanges => prefs.StatusChanges,
            NotificationCategory.RatingReminders => prefs.RatingReminders,
            NotificationCategory.Promotions => prefs.Promotions,
            _ => true
        };
    }
}
