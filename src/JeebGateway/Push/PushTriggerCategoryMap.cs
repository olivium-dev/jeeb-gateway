using JeebGateway.NotificationPreferences;

namespace JeebGateway.Push;

public static class PushTriggerCategoryMap
{
    public static NotificationCategory? CategoryFor(NotificationTrigger trigger) => trigger switch
    {
        NotificationTrigger.NewOffer => NotificationCategory.Offers,
        NotificationTrigger.OfferAccepted => NotificationCategory.StatusChanges,
        NotificationTrigger.StatusChange => NotificationCategory.StatusChanges,
        NotificationTrigger.Chat => NotificationCategory.Chat,
        NotificationTrigger.RatingReminder => NotificationCategory.RatingReminders,
        NotificationTrigger.Promotion => NotificationCategory.Promotions,
        NotificationTrigger.KycUpdate => null,
        NotificationTrigger.AutoOffline => null,
        NotificationTrigger.RatingRevealed => NotificationCategory.RatingReminders,
        NotificationTrigger.LowRatingFlag => null,
        NotificationTrigger.DisputeUpdate => null,
        NotificationTrigger.SettlementPaid => NotificationCategory.Settlements,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown trigger")
    };

    public static bool IsAllowed(NotificationTrigger trigger, UserNotificationPreferences prefs)
    {
        var category = CategoryFor(trigger);
        if (category is null) return true;
        return category switch
        {
            NotificationCategory.Offers => prefs.Offers,
            NotificationCategory.Chat => prefs.Chat,
            NotificationCategory.StatusChanges => prefs.StatusChanges,
            NotificationCategory.RatingReminders => prefs.RatingReminders,
            NotificationCategory.Promotions => prefs.Promotions,
            NotificationCategory.Settlements => prefs.Settlements,
            _ => true
        };
    }
}
