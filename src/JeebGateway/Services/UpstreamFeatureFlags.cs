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
    /// INERT after the exact-salehly RemoteUserPreferences migration. The
    /// <c>UserPreferencesController</c> is now a byte-faithful salehly mirror that
    /// consumes the NSwag-generated
    /// <c>ServiceRemoteUserPreferences.ServiceRemoteUserPreferencesClient</c>
    /// directly and ALWAYS forwards to the real <c>remote-user-preferences</c>
    /// service (host port 10067) — salehly's controller has no UseUpstream gate,
    /// so there is no 503-without-calling kill switch on this path. The property
    /// is retained only so existing <c>FeatureFlags:UseUpstream:RemoteUserPreferences</c>
    /// config (appsettings + tests) binds without error; it no longer changes
    /// behaviour.
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
}
