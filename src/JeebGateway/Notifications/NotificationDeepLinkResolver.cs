using System;
using System.Collections.Generic;

namespace JeebGateway.Notifications;

/// <summary>
/// NOT-02 (Domain 12) — in-app inbox deep-links.
///
/// <para>The notification-service stores opaque <c>notification_type</c> strings and an
/// optional payload; it has no notion of the Jeeb mobile route graph. The mapping from a
/// notification type to a client deep-link is a <b>gateway-owned, product-specific</b>
/// concern (same boundary rationale as <see cref="JeebNotificationCatalog"/> for copy).
/// Keeping it here means the shared service never learns a Jeeb route, and the mobile
/// client receives a ready-to-navigate <c>deepLink</c> on every inbox row.</para>
///
/// <para>The resolver is pure and total: an unknown type yields the inbox root rather than
/// throwing, so a new upstream notification type can never break the inbox render.</para>
/// </summary>
public static class NotificationDeepLinkResolver
{
    /// <summary>Fallback route when a type has no specific destination.</summary>
    public const string InboxRoot = "jeeb://notifications";

    // notification_type (lower-cased) -> route template. Templates may contain a single
    // {id} token substituted from the notification's primary entity id when present.
    private static readonly IReadOnlyDictionary<string, string> Routes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // order/delivery status -> live tracking screen
            ["jeeb.delivery_status_updated"] = "jeeb://deliveries/{id}/tracking",
            ["delivery_status_updated"]      = "jeeb://deliveries/{id}/tracking",
            ["order_status"]                 = "jeeb://deliveries/{id}/tracking",

            // offer lifecycle -> offer detail
            ["jeeb.offer_received"] = "jeeb://offers/{id}",
            ["offer_received"]      = "jeeb://offers/{id}",
            ["jeeb.offer_accepted"] = "jeeb://offers/{id}",
            ["offer_accepted"]      = "jeeb://offers/{id}",
            // NOTE — "jeeb.offer_rejected" / "offer_rejected" are deliberately ABSENT
            // (b02 step 6b, owner ruling D3 = retire). This map exists to deep-link an INBOX
            // ROW, and the notification centre has no route for either spelling
            // (POST :10026/notifications/jeeb.offer_rejected -> 405), so a row of that type
            // cannot exist and an entry here could never be reached from the inbox.
            // The loser-bidder PUSH still carries jeeb://offers/{offerId}; it now gets that
            // link from OfferPushNotifier.OfferLostDeepLink, not from this resolver.

            // KYC approve/reject -> KYC review screen
            ["jeeb.kyc_approved"] = "jeeb://kyc/status",
            ["kyc_approved"]      = "jeeb://kyc/status",
            ["jeeb.kyc_rejected"] = "jeeb://kyc/status",
            ["kyc_rejected"]      = "jeeb://kyc/status",

            // request expiry -> request detail
            ["request_expiry"]   = "jeeb://requests/{id}",
            ["request_expired"]  = "jeeb://requests/{id}",

            // settlement -> wallet
            ["jeeb.settlement_paid"] = "jeeb://wallet/settlements/{id}",
            ["settlement_paid"]      = "jeeb://wallet/settlements/{id}",

            // dispute -> dispute detail
            ["jeeb.dispute_resolved"] = "jeeb://disputes/{id}",
            ["dispute_resolved"]      = "jeeb://disputes/{id}",
        };

    /// <summary>
    /// Resolve a client deep-link for an inbox row. <paramref name="notificationType"/> is the
    /// opaque upstream type; <paramref name="entityId"/> is the optional primary entity id used
    /// to fill a <c>{id}</c> token. Never throws; unknown/blank types return <see cref="InboxRoot"/>.
    /// </summary>
    public static string Resolve(string? notificationType, string? entityId = null)
    {
        if (string.IsNullOrWhiteSpace(notificationType))
        {
            return InboxRoot;
        }

        if (!Routes.TryGetValue(notificationType.Trim(), out var template))
        {
            return InboxRoot;
        }

        if (!template.Contains("{id}", StringComparison.Ordinal))
        {
            return template;
        }

        // Template expects an id. If we don't have one, drop the id segment gracefully by
        // returning the inbox root rather than emitting a malformed "jeeb://offers/{id}" link.
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return InboxRoot;
        }

        return template.Replace("{id}", entityId.Trim(), StringComparison.Ordinal);
    }
}
