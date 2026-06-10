namespace JeebGateway.Services.Dispatch;

/// <summary>
/// Static template catalog for the MVP. Templates are simple string-format
/// patterns where <c>{paramName}</c> tokens are replaced by the caller-supplied
/// parameters.
///
/// When the notification-service exposes a <c>GET /render/{key}</c> endpoint,
/// replace this with an <c>HttpNotificationTemplateRenderer</c> that delegates
/// to that service.
/// </summary>
public sealed class StaticNotificationTemplateRenderer : INotificationTemplateRenderer
{
    // key → (locale → (title, body))
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, (string Title, string Body)>> Catalog =
        new Dictionary<string, IReadOnlyDictionary<string, (string, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["jeeb.request.received"] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = ("Request Received", "Your delivery request {requestId} has been received."),
                ["ar"] = ("تم استلام الطلب", "تم استلام طلب التوصيل الخاص بك {requestId}.")
            },
            ["jeeb.offer.accepted"] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = ("Offer Accepted", "Your offer for request {requestId} has been accepted."),
                ["ar"] = ("تم قبول العرض", "تم قبول عرضك للطلب {requestId}.")
            },
            ["jeeb.delivery.started"] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = ("Delivery Started", "Your delivery {requestId} is on its way."),
                ["ar"] = ("بدأ التوصيل", "جاري توصيل طلبك {requestId}.")
            },
            ["jeeb.delivery.completed"] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = ("Delivery Completed", "Your delivery {requestId} has been completed."),
                ["ar"] = ("اكتمل التوصيل", "تم توصيل طلبك {requestId} بنجاح.")
            },
            ["jeeb.kyc.approved"] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = ("KYC Approved", "Your identity verification has been approved."),
                ["ar"] = ("تمت الموافقة على التحقق", "تمت الموافقة على التحقق من هويتك.")
            },
            ["jeeb.kyc.rejected"] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = ("KYC Rejected", "Your identity verification was not approved. Please resubmit."),
                ["ar"] = ("رُفض التحقق", "لم تتم الموافقة على التحقق من هويتك. يرجى إعادة التقديم.")
            },
            ["jeeb.dispute.update"] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = ("Dispute Update", "There is an update on your dispute {disputeId}."),
                ["ar"] = ("تحديث النزاع", "هناك تحديث على نزاعك {disputeId}.")
            },
        };

    public RenderedNotification? Render(string templateKey, string locale, IReadOnlyDictionary<string, string> parameters)
    {
        if (!Catalog.TryGetValue(templateKey, out var localeMap))
            return null;

        var (title, body) = localeMap.TryGetValue(locale, out var localized)
            ? localized
            : localeMap.TryGetValue("en", out var fallback)
                ? fallback
                : default;

        if (title is null) return null;

        title = ApplyParameters(title, parameters);
        body = ApplyParameters(body, parameters);

        return new RenderedNotification(title, body);
    }

    private static string ApplyParameters(string template, IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var (key, value) in parameters)
            template = template.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);
        return template;
    }
}
