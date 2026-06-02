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
    /// When true, the thin generic OTP surface (<see cref="JeebGateway.Controllers.OtpController"/>)
    /// proxies the real one-time-password service (OTPApi; live swarm overlay,
    /// host port 10037) via
    /// <see cref="JeebGateway.Services.Clients.IServiceOTPClient"/> — the same
    /// NSwag-generated client the delivery-handover OTP path already consumes.
    /// Default false so the gateway returns 503 (not a localhost-fallback call)
    /// when the upstream is not configured in an environment, and so existing
    /// fixtures that do not stand up the OTP upstream stay green. Defaulted ON
    /// in <c>appsettings.Production.json</c> because the upstream is live.
    ///
    /// Serves JEB-1471, JEB-1467, JEB-1459, JEB-1455, JEB-1441, JEB-1437,
    /// JEB-1433, JEB-1430, JEB-626, JEB-625, JEB-471, JEB-158, JEB-159, JEB-55,
    /// JEB-49, JEB-37, JEB-38, JEB-39 (OTP send/validate for phone sign-in and
    /// the 4-digit delivery_handover OTP).
    /// </summary>
    public bool Otp { get; set; }

    /// <summary>
    /// When true, real-time chat fan-out is published to the shared
    /// <c>realtime-comunication-service</c> (Elixir/Phoenix, HTTP ingest
    /// <c>POST /api/ingest/{topic}/{stream}</c>) via
    /// <see cref="JeebGateway.Services.Clients.IRealtimeCommunicationClient"/>
    /// instead of (or in addition to) the gateway-local SignalR
    /// <see cref="JeebGateway.Chat.ChatHub"/> fan-out. Serves JEB-1453, JEB-1449,
    /// JEB-1432, JEB-626, JEB-444, JEB-50/51/52 (jeeb:chat Phoenix channel,
    /// membership-validated join, per-recipient fan-out filter).
    ///
    /// Default false in every environment: the realtime-comunication-service is
    /// NOT yet on the Jeeb swarm — <c>Services:Realtime:BaseUrl</c> is a marked
    /// placeholder (PORT_TBD) in appsettings.Production.json. The flag MUST stay
    /// false until that service is deployed and the placeholder is replaced with
    /// the real host:port, otherwise the publish path would target an unbound
    /// upstream and the resilience pipeline would burn retries to nowhere.
    /// </summary>
    public bool Realtime { get; set; }

    /// <summary>
    /// When true, contract-signing paths proxy the real
    /// <c>contract-signing-service</c> (FastAPI, immutable contract templates +
    /// per-party signatures; serves the versioned Jeeb Terms-of-Service template
    /// <c>jeeb_tos_v1</c>) via
    /// <see cref="JeebGateway.Services.Clients.IContractSigningServiceClient"/>.
    /// This path is net-new (the gateway never held a contract store), so the flag
    /// is a runtime kill switch, NOT a fallback to local state: when off the
    /// endpoints return 503. Defaults OFF in EVERY environment because
    /// contract-signing-service is NOT yet deployed to the Jeeb swarm — its
    /// <c>Services:ContractSigning:BaseUrl</c> is a placeholder (<c>PORT_TBD</c>)
    /// pending deployment. Flip on once the service is live and the BaseUrl is real.
    /// </summary>
    public bool ContractSigning { get; set; }
}
