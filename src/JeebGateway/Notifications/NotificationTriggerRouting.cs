using JeebGateway.Push;

namespace JeebGateway.Notifications;

// Maps the public NotificationTrigger vocabulary onto the refresh category the
// notification-service hand-over stamps. Total by design: a new trigger must be decided here.
public static class NotificationTriggerRouting
{
    public static string CategoryFor(NotificationTrigger trigger) => trigger switch
    {
        NotificationTrigger.NewOffer => PushSilencePolicy.CategoryNewOffer,
        NotificationTrigger.OfferAccepted => PushSilencePolicy.CategoryOfferAccepted,
        NotificationTrigger.StatusChange => PushSilencePolicy.CategoryDelivery,
        NotificationTrigger.Chat => PushSilencePolicy.CategoryChat,
        NotificationTrigger.KycUpdate => PushSilencePolicy.CategoryKyc,
        NotificationTrigger.RatingReminder => PushSilencePolicy.CategoryRating,
        NotificationTrigger.Promotion => PushSilencePolicy.CategoryPromotion,
        NotificationTrigger.AutoOffline => PushSilencePolicy.CategoryAvailability,
        NotificationTrigger.RatingRevealed => PushSilencePolicy.CategoryRating,
        NotificationTrigger.LowRatingFlag => PushSilencePolicy.CategoryRating,
        NotificationTrigger.DisputeUpdate => PushSilencePolicy.CategoryDispute,
        NotificationTrigger.SettlementPaid => PushSilencePolicy.CategorySettlement,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown trigger")
    };
}
