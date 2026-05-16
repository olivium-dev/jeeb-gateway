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
