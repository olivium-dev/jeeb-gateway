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

    /// <summary>
    /// When true, device-register / push paths proxy the real
    /// push-notification service via
    /// <see cref="JeebGateway.Services.Clients.IPushNotificationClient"/>
    /// instead of the in-memory transport. Default false keeps existing
    /// push fixtures green.
    /// </summary>
    public bool Push { get; set; }

    /// <summary>
    /// When true, the user-preferences read/write surface proxies the real
    /// <c>remote-user-preferences</c> service (the fleet-wide preference store,
    /// host port 10067) via
    /// <see cref="JeebGateway.Services.Clients.IUserPreferencesClient"/>.
    /// This path is net-new — the gateway never held a preferences store — so
    /// the flag is a runtime kill switch (flip to false to make the endpoints
    /// return 503 without a redeploy if the upstream is taken down), NOT a
    /// fallback to local state. Defaulted ON in
    /// <c>appsettings.Production.json</c> because the upstream is live; default
    /// false here keeps unit fixtures that do not configure the upstream green.
    /// </summary>
    public bool RemoteUserPreferences { get; set; }
}
