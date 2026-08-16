namespace JeebGateway.Push;

/// <summary>
/// The set of in-product events that fan out to push (T-backend-022). Survives the deletion
/// of the in-gateway push stack as the wire vocabulary of POST /v1/notifications/dispatch;
/// each value maps to one refresh category via <c>NotificationTriggerRouting</c>.
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
    // Weekly COD settlement paid (JEB-1476). The generic settlement event is
    // emitted by the shared payment gateway; the gateway owns mapping it to a
    // Jeeber recipient + the localized push copy.
    SettlementPaid
}
