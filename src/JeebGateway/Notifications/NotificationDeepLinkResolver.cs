using System;
using System.Collections.Generic;
using System.Linq;

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
/// <para>Unknown types or absent destinations yield the inbox root. A present but
/// malformed destination is an upstream contract failure, never a fabricated route.</para>
/// </summary>
public static class NotificationDeepLinkResolver
{
    /// <summary>Fallback route when a type has no specific destination.</summary>
    public const string InboxRoot = "jeeb://notifications";

    public static string? ValidateEntityId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value is "." or ".." || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '~')))
            throw new NotificationContractException();
        return value;
    }

    /// <summary>The link when it is well-formed AND names a route in the mobile grammar; null when
    /// it is well-formed but names no route, so the caller resolves the row from its type instead.</summary>
    public static string? MatchExplicitLink(string value)
    {
        value = value.Trim();
        var isPath = value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal);
        var isJeebUri = value.StartsWith("jeeb://", StringComparison.OrdinalIgnoreCase);
        if (!isPath && !isJeebUri)
            throw new NotificationContractException();
        // Uri parsing checks query/fragment syntax only; the temporary authority lets
        // System.Uri accept an app-relative path. Route checks below use the original.
        var parseable = isPath ? "jeeb://route-validation" + value : value;
        if (!Uri.TryCreate(parseable, UriKind.Absolute, out var parsed) || !parsed.IsWellFormedOriginalString())
            throw new NotificationContractException();
        var suffix = value.IndexOfAny(['?', '#']);
        var route = suffix < 0 ? value : value[..suffix];
        route = isPath ? route[1..] : route["jeeb://".Length..];
        // Mobile strips one optional trailing slash, and supports '/' itself.
        if (route.EndsWith('/')) route = route[..^1];
        if (isPath && route.Length == 0) return value;
        // Match the original segments, before URI normalization could conceal traversal
        // or escaped separators. The route graph is finite; no regex automaton needed.
        var segments = route.Split('/');
        if (segments.Any(segment => ValidateEntityId(segment) is null))
            throw new NotificationContractException();
        if (isJeebUri) segments[0] = segments[0].ToLowerInvariant();
        var allowed = segments is
            ["notifications"] or ["wallet"] or ["earnings"] or ["support"]
            or ["wallet", "customer" or "activity" or "charge-info"]
            or ["profile", "kyc"] or ["settings", "notifications"] or ["jeeber", "pending-offers"]
            or ["chat", _] or ["disputes", _] or ["support", "tickets", _] or ["wallet", "transactions", _]
            or ["requests", _, "offers" or "waiting"]
            or ["orders", _]
            or ["orders", _, "receipt" or "summary" or "cancel" or "rate" or "tracking" or "otp" or "feedback" or "mutual-rate" or "escalate"]
            or ["jeeber", "requests", _] or ["jeeber", "requests", _, "offer"]
            or ["jeeber", "deliveries", _, "active"];
        return allowed ? value : null;
    }

    // Templates follow mobile routeFromPushDeepLink's allow-list; host is the first path segment.
    // {id} is the request/delivery ref, never an offer or conversation id.
    private static readonly IReadOnlyDictionary<string, string> Routes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["new_request"] = "jeeb://jeeber/requests/{id}",
            ["jeeb.new_request"] = "jeeb://jeeber/requests/{id}",
            ["chat"] = "jeeb://chat/{id}",
            ["chat_message"] = "jeeb://chat/{id}",
            ["jeeb.chat_message"] = "jeeb://chat/{id}",
            ["delivery"] = "jeeb://orders/{id}",
            ["cancellation_decision"] = "jeeb://orders/{id}",
            ["jeeb.cancellation_decision"] = "jeeb://orders/{id}",
            ["jeeb.delivery_status_updated"] = "jeeb://orders/{id}",
            ["delivery_status_updated"]      = "jeeb://orders/{id}",
            ["order_status"]                 = "jeeb://orders/{id}",

            ["offer"] = "jeeb://requests/{id}/offers",
            ["jeeb.offer_received"] = "jeeb://requests/{id}/offers",
            ["offer_received"]      = "jeeb://requests/{id}/offers",
            // Kept: ServiceCallbacksController + JeebNotificationCatalog still produce this type.
            ["jeeb.offer_updated"] = "jeeb://requests/{id}/offers",
            ["offer_updated"]      = "jeeb://requests/{id}/offers",
            ["jeeb.offer_accepted"] = "jeeb://chat/{id}",
            ["offer_accepted"]      = "jeeb://chat/{id}",
            // NOTE — "jeeb.offer_rejected" / "offer_rejected" stay ABSENT (b02 step 6b, D3 = retire):
            // the centre 405s that type, and PLAN-P02 §4 routes offer_lost to the inbox root.

            // KYC approve/reject -> KYC review screen
            ["jeeb.kyc_approved"] = "jeeb://profile/kyc",
            ["kyc_approved"]      = "jeeb://profile/kyc",
            ["jeeb.kyc_rejected"] = "jeeb://profile/kyc",
            ["kyc_rejected"]      = "jeeb://profile/kyc",

            // request expiry -> request detail
            ["request.try_expand_tier"] = "jeeb://requests/{id}/waiting",
            ["request.expired"] = "jeeb://requests/{id}/waiting",
            ["request_expiring"] = "jeeb://requests/{id}/waiting",
            ["request_expiry"]   = "jeeb://requests/{id}/waiting",
            ["request_expired"]  = "jeeb://requests/{id}/waiting",

            // settlement -> wallet
            ["jeeb.settlement_paid"] = "jeeb://wallet",
            ["settlement_paid"]      = "jeeb://wallet",

            // dispute -> dispute detail
            ["jeeb.dispute_resolved"] = "jeeb://disputes/{id}",
            ["dispute_resolved"]      = "jeeb://disputes/{id}",
        };

    /// <summary>
    /// Resolve a client deep-link for an inbox row. <paramref name="notificationType"/> is the
    /// opaque upstream type; <paramref name="entityId"/> is the optional primary entity id used
    /// to fill a <c>{id}</c> token. Unknown/blank types return <see cref="InboxRoot"/>;
    /// malformed present IDs throw <see cref="NotificationContractException"/>.
    /// </summary>
    public static string Resolve(string? notificationType, string? entityId = null)
    {
        entityId = ValidateEntityId(entityId);
        if (string.IsNullOrWhiteSpace(notificationType))
        {
            return InboxRoot;
        }

        var normalized = notificationType.Trim();
        if (normalized.StartsWith("jeeb.dispute.", StringComparison.OrdinalIgnoreCase))
            return CaseLink("jeeb://disputes/{id}", entityId);
        if (normalized.StartsWith("jeeb.support.", StringComparison.OrdinalIgnoreCase))
            return CaseLink("jeeb://support/tickets/{id}", entityId);

        if (!Routes.TryGetValue(normalized, out var template))
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

        return template.Replace("{id}", entityId, StringComparison.Ordinal);
    }

    private static string CaseLink(string template, string? caseId) =>
        string.IsNullOrWhiteSpace(caseId)
            ? InboxRoot
            : template.Replace("{id}", caseId, StringComparison.Ordinal);
}
