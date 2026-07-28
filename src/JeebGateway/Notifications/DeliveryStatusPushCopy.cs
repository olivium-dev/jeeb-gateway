namespace JeebGateway.Notifications;

/// <summary>
/// Shade copy for the delivery <see cref="DeliveryStatusPushNotification"/>.
///
/// <para><b>Why this is its own type.</b> The body was an inline interpolated string in
/// <c>DeliveriesController.NotifyOtherPartyAsync</c>, and it shipped a sentence that is
/// never true: two customer devices showed <c>"Status changed from Picked to Picked."</c>
/// and <c>"Status changed from AtDoor to AtDoor."</c>. Extracting it makes the rule
/// ("a transition renders as a transition; a non-transition does not pretend to be one")
/// directly testable without a web host.</para>
///
/// <para>The reported occurrences had an ALIASING cause — the caller read
/// <c>previousStatus</c> off the LIVE store row that the status mirror had already
/// advanced in place, so from and to were literally the same field. That is fixed at the
/// caller (it snapshots the string before the transition). This type is the second line
/// of defence: from == to is ALSO reachable with no defect at all, when a client
/// idempotently re-PATCHes the status a delivery already holds.</para>
/// </summary>
public static class DeliveryStatusPushCopy
{
    /// <summary>
    /// Shade body for a status-change push.
    ///
    /// Renders "Status changed from {from} to {to}." only when a genuine transition is
    /// being described. When the previous status is unknown (blank) or identical to the
    /// new one, it states the current status instead of asserting a change that did not
    /// happen.
    /// </summary>
    public static string StatusChangeBody(string? previousStatus, string status)
        => string.IsNullOrWhiteSpace(previousStatus)
           || string.Equals(previousStatus, status, StringComparison.OrdinalIgnoreCase)
            ? $"Status is now {status}."
            : $"Status changed from {previousStatus} to {status}.";
}
