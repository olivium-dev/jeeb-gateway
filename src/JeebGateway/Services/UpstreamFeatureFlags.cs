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

    /// <summary>
    /// When true, offer read/write paths proxy the real offer-service
    /// (host port 10063, health <c>/health</c>) via
    /// <see cref="JeebGateway.Services.Clients.IOfferServiceClient"/>
    /// instead of gateway-local state. Default false keeps existing in-memory
    /// offer fixtures green.
    /// </summary>
    public bool Offer { get; set; }

    /// <summary>
    /// When true, ban / moderation paths proxy the real ban-service
    /// (host port 10065, health <c>/health</c>) via
    /// <see cref="JeebGateway.Services.Clients.IBanServiceClient"/>
    /// instead of gateway-local state. Default false keeps existing in-memory
    /// ban fixtures green.
    /// </summary>
    public bool Ban { get; set; }

    /// <summary>
    /// When true, feedback paths proxy the real feedback-service
    /// (host port 10064, liveness-only health probe) via
    /// <see cref="JeebGateway.Services.Clients.IFeedbackServiceClient"/>
    /// instead of gateway-local state. Default false keeps existing in-memory
    /// feedback fixtures green.
    /// </summary>
    public bool Feedback { get; set; }

    /// <summary>
    /// When true, voice transcription paths proxy the real
    /// voice-transcription-service (host port 10062, health <c>/healthz</c>) via
    /// <see cref="JeebGateway.Services.Clients.IVoiceTranscriptionClient"/>
    /// instead of the gateway-local Whisper transport. Default false keeps the
    /// existing in-process Whisper fixtures green.
    /// </summary>
    public bool Voice { get; set; }

    /// <summary>
    /// When true, form-builder paths proxy the real <c>form-builder-service</c>
    /// (FastAPI dynamic-forms upstream; serves the versioned Jeeb KYC form schema
    /// <c>jeeb_jeeber_v1</c>) via
    /// <see cref="JeebGateway.Services.Clients.IFormBuilderServiceClient"/>.
    /// This path is net-new (the gateway never held a form store), so the flag is
    /// a runtime kill switch, NOT a fallback to local state: when off the
    /// endpoints return 503. Defaults OFF in EVERY environment; form-builder-service
    /// is now deployed to the Jeeb swarm with
    /// <c>Services:FormBuilder:BaseUrl</c> = <c>http://192.168.2.50:10070/</c>, so
    /// flip this on to route through the real upstream.
    /// </summary>
    public bool FormBuilder { get; set; }
}
