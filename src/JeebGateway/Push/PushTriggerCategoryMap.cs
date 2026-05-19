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
        // T-BE-025 / JEB-61 — bucket the auto-reveal ping under
        // RatingReminders so a user who muted rating reminders is not
        // pinged about an auto-reveal either. The reveal happens regardless;
        // only the push is suppressed.
        NotificationTrigger.RatingAutoRevealed => NotificationCategory.RatingReminders,
        // T-backend-021 / JEEB-39 — mutual-consent reveal (both sides
        // submitted) is also a rating-reminder follow-up.
        NotificationTrigger.RatingRevealed => NotificationCategory.RatingReminders,
        NotificationTrigger.Promotion => NotificationCategory.Promotions,
        // KYC is a regulatory/identity event — always delivered, like OTP.
        NotificationTrigger.KycUpdate => null,
        // Auto-offline tells the Jeeber why their offer feed went silent;
        // muting it would leave them confused about lost earnings.
        NotificationTrigger.AutoOffline => null,
        // Admin-facing operational pings — always delivered.
        NotificationTrigger.LowRatingFlag => null,
        // Disputes are a regulatory/safety channel — always delivered.
        NotificationTrigger.DisputeUpdate => null,
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
