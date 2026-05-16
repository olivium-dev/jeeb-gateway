namespace JeebGateway.Push;

/// <summary>
/// The set of in-product events that fan out to push (T-backend-022, AC line 1).
/// Each trigger maps to exactly one category for preference filtering via
/// <see cref="PushTriggerCategoryMap"/>. Always-on triggers (KYC, OTP) bypass
/// the per-user preferences check.
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
    AutoOffline
}
