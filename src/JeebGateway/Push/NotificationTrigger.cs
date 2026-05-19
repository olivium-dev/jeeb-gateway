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
    AutoOffline,
    RatingRevealed,
    LowRatingFlag,
    DisputeUpdate,

    /// <summary>
    /// T-BE-025 / JEB-61 — fired by the daily auto-reveal cron when a
    /// delivery's 7-day blind window closes without both sides submitting.
    /// Maps to notification-service template <c>rating_auto_revealed</c>.
    /// Mapped to <see cref="NotificationPreferences.NotificationCategory.RatingReminders"/>
    /// in <see cref="PushTriggerCategoryMap"/> so users who muted rating
    /// reminders are not pinged about an auto-reveal either.
    /// </summary>
    RatingAutoRevealed
}
