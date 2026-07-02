using System.Collections.Generic;
using System.Linq;

namespace JeebGateway.Notifications;

/// <summary>
/// A single localized notification template (title + body).
/// </summary>
public sealed record NotificationTemplate(string Title, string Body);

/// <summary>
/// JEB-1486 boundary remediation — the Jeeb notification taxonomy lives HERE,
/// in the gateway, NOT in the shared/reusable notification-service (GR2).
///
/// This catalog OWNS the Jeeb <c>jeeb.*</c> template keys and their EN/AR copy
/// that were removed from notification-service. The gateway resolves a concrete
/// {title, body} via <see cref="Render"/> and then either:
///   * sends a FULLY-RESOLVED payload to the shared service (webhook/dispatch),
///     so the shared service never needs to know any Jeeb template, OR
///   * pushes the catalog into the shared service ONCE via its generic
///     <c>POST /templates/register</c> endpoint (opaque keys), keeping the
///     deprecated <c>jeeb.*</c> resolution working during the cutover window.
///
/// Keys are opaque to the shared service; their Jeeb meaning is defined only
/// here. This is the relocation target named in the ticket
/// ("gateway JeebNotificationCatalog + generic API + gateway-side rendering").
/// </summary>
public static class JeebNotificationCatalog
{
    public const string DefaultLocale = "en";

    public static readonly IReadOnlyList<string> SupportedLocales = new[] { "en", "ar" };

    // key -> locale -> template. Relocated verbatim from the de-leaked
    // notification-service locales/{en,ar}.json (jeeb_notifications section).
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, NotificationTemplate>> Templates =
        new Dictionary<string, IReadOnlyDictionary<string, NotificationTemplate>>
        {
            ["jeeb.offer_received"] = new Dictionary<string, NotificationTemplate>
            {
                ["en"] = new("New Delivery Offer", "You have received a new delivery offer. Check the details and accept if interested."),
                ["ar"] = new("عرض توصيل جديد", "لقد تلقيت عرض توصيل جديد. تحقق من التفاصيل واقبل إذا كنت مهتماً."),
            },
            ["jeeb.offer_accepted"] = new Dictionary<string, NotificationTemplate>
            {
                ["en"] = new("Offer Accepted", "Your delivery offer has been accepted. Get ready to start the delivery process."),
                ["ar"] = new("تم قبول العرض", "تم قبول عرض التوصيل الخاص بك. استعد لبدء عملية التوصيل."),
            },
            // sprint-009 Lane E — the LOSER side of the multi-offer accept. When the
            // client awards the delivery to one jeeber, every other bidder's offer is
            // rejected by the offer-service accept saga; the gateway pushes this to each
            // losing bidder so their offer list reconciles from "pending" to "lost"
            // without a poll. Copy is deliberately soft/encouraging (they can keep bidding).
            ["jeeb.offer_rejected"] = new Dictionary<string, NotificationTemplate>
            {
                ["en"] = new("Offer Not Selected", "Your offer wasn't selected this time. Keep an eye out for new delivery requests."),
                ["ar"] = new("لم يتم اختيار عرضك", "لم يتم اختيار عرضك هذه المرة. تابع طلبات التوصيل الجديدة."),
            },
            ["jeeb.delivery_status_updated"] = new Dictionary<string, NotificationTemplate>
            {
                ["en"] = new("Delivery Update", "Your delivery status has been updated. Check the app for the latest information."),
                ["ar"] = new("تحديث حالة التوصيل", "تم تحديث حالة التوصيل الخاصة بك. تحقق من التطبيق للحصول على أحدث المعلومات."),
            },
            ["jeeb.settlement_paid"] = new Dictionary<string, NotificationTemplate>
            {
                ["en"] = new("Payment Completed", "Your settlement payment has been processed and completed successfully."),
                ["ar"] = new("تم إكمال الدفع", "تم معالجة دفعة التسوية الخاصة بك وإكمالها بنجاح."),
            },
            ["jeeb.kyc_approved"] = new Dictionary<string, NotificationTemplate>
            {
                ["en"] = new("KYC Approved", "Congratulations! Your identity verification has been approved. You can now access all features."),
                ["ar"] = new("تم الموافقة على التحقق", "مبروك! تم الموافقة على التحقق من هويتك. يمكنك الآن الوصول إلى جميع الميزات."),
            },
            ["jeeb.kyc_rejected"] = new Dictionary<string, NotificationTemplate>
            {
                ["en"] = new("KYC Verification Required", "Your identity verification needs additional information. Please update your documents."),
                ["ar"] = new("التحقق من الهوية مطلوب", "التحقق من هويتك يحتاج إلى معلومات إضافية. يرجى تحديث مستنداتك."),
            },
            ["jeeb.dispute_resolved"] = new Dictionary<string, NotificationTemplate>
            {
                ["en"] = new("Dispute Resolved", "Your dispute has been resolved. Check the resolution details in your account."),
                ["ar"] = new("تم حل النزاع", "تم حل النزاع الخاص بك. تحقق من تفاصيل الحل في حسابك."),
            },
            ["jeeb.rating_auto_revealed"] = new Dictionary<string, NotificationTemplate>
            {
                ["en"] = new("Rating Updated", "Your delivery rating has been automatically updated based on the completed delivery."),
                ["ar"] = new("تم تحديث التقييم", "تم تحديث تقييم التوصيل الخاص بك تلقائياً بناءً على التوصيل المكتمل."),
            },
        };

    /// <summary>The Jeeb template keys owned by the gateway.</summary>
    public static IReadOnlyCollection<string> Keys => (IReadOnlyCollection<string>)Templates.Keys;

    /// <summary>True when the catalog owns a template for <paramref name="key"/>.</summary>
    public static bool HasTemplate(string key) => Templates.ContainsKey(key);

    /// <summary>
    /// Resolve a concrete {title, body} for an opaque key + locale, substituting
    /// <c>{placeholder}</c> tokens from <paramref name="parameters"/>. Unknown
    /// placeholders are left intact (never throws). An unknown key returns a
    /// generic, product-neutral fallback so a missing template never blocks a
    /// notification. Locale falls back to the default when unsupported or when
    /// the key has no entry for the requested locale.
    /// </summary>
    public static NotificationTemplate Render(
        string key,
        string? locale = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var lc = NormalizeLocale(locale);

        if (Templates.TryGetValue(key, out var byLocale))
        {
            if (!byLocale.TryGetValue(lc, out var template))
            {
                byLocale.TryGetValue(DefaultLocale, out template);
            }

            if (template is not null)
            {
                return new NotificationTemplate(
                    Substitute(template.Title, parameters),
                    Substitute(template.Body, parameters));
            }
        }

        // Generic, product-neutral fallback (mirrors the shared service's).
        return new NotificationTemplate("Notification", $"You have a new notification for {key}");
    }

    /// <summary>
    /// The full catalog shaped for the shared service's generic
    /// <c>POST /templates/register</c> body: <c>key -&gt; {locale -&gt; {title, body}}</c>.
    /// Lets the gateway register its catalog once so the shared service can
    /// resolve the deprecated <c>jeeb.*</c> keys generically during cutover —
    /// without any Jeeb literal living in that shared service's source.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, NotificationTemplate>> All => Templates;

    private static string NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return DefaultLocale;
        var lc = locale.Trim().ToLowerInvariant();
        // Accept "ar-SA" etc. by taking the primary subtag.
        var dash = lc.IndexOf('-');
        if (dash > 0) lc = lc.Substring(0, dash);
        return SupportedLocales.Contains(lc) ? lc : DefaultLocale;
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, string>? parameters)
    {
        if (string.IsNullOrEmpty(text) || parameters is null || parameters.Count == 0)
        {
            return text;
        }

        foreach (var kv in parameters)
        {
            text = text.Replace("{" + kv.Key + "}", kv.Value ?? string.Empty);
        }
        return text;
    }
}
