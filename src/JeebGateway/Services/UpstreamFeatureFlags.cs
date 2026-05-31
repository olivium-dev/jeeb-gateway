namespace JeebGateway.Services;

/// <summary>
/// Per-service kill switches that flip controllers between the legacy
/// in-memory implementation and the upstream HTTP client. Defaults are
/// false so existing test fixtures (which exercise the in-memory paths)
/// keep passing. PR-B flips these on by default and deletes the
/// in-memory stores.
/// </summary>
public sealed class UpstreamFeatureFlags
{
    public const string SectionName = "FeatureFlags:UseUpstream";

    public bool Auth { get; set; }
    public bool Delivery { get; set; }
    public bool Matching { get; set; }
    public bool Geolocation { get; set; }

    /// <summary>
    /// When true, notification read paths proxy the real notification-service
    /// (FastAPI, Mongo <c>jeeb_notifications</c>) via
    /// <see cref="JeebGateway.Services.Clients.INotificationServiceClient"/>
    /// instead of gateway-local state. Default false keeps existing in-memory
    /// preference fixtures green.
    /// </summary>
    public bool Notification { get; set; }
}
