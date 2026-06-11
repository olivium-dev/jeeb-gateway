using JeebGateway.Push;

namespace JeebGateway.Financials;

/// <summary>
/// The GENERIC settlement event emitted by the shared payment gateway's
/// transactional outbox (JEB-1476). UPG states only the facts — who is the
/// payee/recipient, how much, for which period — and carries NO Jeeb push copy
/// and NO recipient device targeting. Mirrors the UPG `settlement_notifications`
/// payload shape (generic keys preferred).
/// </summary>
public sealed record GenericSettlementEvent
{
    /// <summary>Caller-supplied generic event name, e.g. "settlement_paid".</summary>
    public required string EventType { get; init; }

    /// <summary>Settlement kind, e.g. "cod".</summary>
    public required string SettlementType { get; init; }

    /// <summary>
    /// Generic payee/recipient id from UPG (`payee_id` / `recipient_id`). In
    /// the Jeeb domain this is the Jeeber who collected the cash and is owed
    /// the net amount.
    /// </summary>
    public required string RecipientId { get; init; }

    public string? NetAmount { get; init; }
    public string? Currency { get; init; }
    public string? PeriodStart { get; init; }
    public string? PeriodEnd { get; init; }
    public string? BatchId { get; init; }
}

/// <summary>
/// The Jeeb-domain push plan derived from a generic settlement event: the
/// resolved recipient, the always-on trigger, the notification-service topic
/// that owns the localized copy, and a product fallback copy.
/// </summary>
public sealed record SettlementPaidPushPlan
{
    /// <summary>The Jeeber (gateway user) who receives the push.</summary>
    public required string RecipientUserId { get; init; }

    public required NotificationTrigger Trigger { get; init; }

    /// <summary>notification-service topic carrying the localized title/body.</summary>
    public required string Topic { get; init; }

    public required string FallbackTitle { get; init; }
    public required string FallbackBody { get; init; }
}

/// <summary>
/// Owns the Jeeb-domain meaning of the generic settlement event (JEB-1476 /
/// GR2). The "Jeeber" recipient targeting and the settlement_paid push copy
/// used to live in the shared payment gateway; they now live HERE in the BFF.
/// UPG emits only the generic event via its outbox, the notification-service
/// renders the localized copy, and this mapper bridges the two.
/// </summary>
public static class SettlementPaidNotification
{
    /// <summary>The generic event name UPG emits for a paid settlement batch.</summary>
    public const string PaidEventType = "settlement_paid";

    /// <summary>notification-service topic that carries the localized copy.</summary>
    public const string Topic = "settlement.paid";

    /// <summary>Returns true when the generic event is a settlement-paid event.</summary>
    public static bool IsSettlementPaid(GenericSettlementEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        return string.Equals(ev.EventType, PaidEventType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a generic UPG settlement event onto a Jeeb push plan: resolves the
    /// recipient (the Jeeber) from the generic payee/recipient id and pins the
    /// localized notification topic plus a product fallback copy.
    /// </summary>
    public static SettlementPaidPushPlan ToPushPlan(GenericSettlementEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        if (string.IsNullOrWhiteSpace(ev.RecipientId))
            throw new ArgumentException("RecipientId is required to target a settlement push.", nameof(ev));

        return new SettlementPaidPushPlan
        {
            RecipientUserId = ev.RecipientId,
            Trigger = NotificationTrigger.SettlementPaid,
            Topic = Topic,
            FallbackTitle = "Settlement Paid",
            FallbackBody = BuildFallbackBody(ev),
        };
    }

    private static string BuildFallbackBody(GenericSettlementEvent ev)
    {
        if (!string.IsNullOrWhiteSpace(ev.NetAmount) && !string.IsNullOrWhiteSpace(ev.Currency))
            return $"Your settlement of {ev.NetAmount} {ev.Currency} has been paid.";

        return "Your settlement has been paid.";
    }
}
