namespace JeebGateway.Push;

/// <summary>
/// The set of in-product events that fan out to push (T-backend-022).
/// Each trigger maps to exactly one category for preference filtering via
/// <see cref="PushTriggerCategoryMap"/>. Always-on triggers bypass preferences.
/// </summary>
public enum NotificationTrigger
{
    NewOffer,
    OfferAccepted,
    StatusChange,
    Chat,
    KycUpdate,
    RatingReminder,
    Promotion,
    AutoOffline,
    RatingRevealed,
    LowRatingFlag,
    DisputeUpdate,
    /// <summary>jeeb.settlement_paid — maps to Settlements category (JEB-1498).</summary>
    SettlementPaid
}
