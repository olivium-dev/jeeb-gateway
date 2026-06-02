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

    // Notification — REMOVED. The notification read surface is now a stateless
    // passthrough over the salehly-mirrored NSwag ServiceNotificationClient
    // (registered in Program.cs as the named client "ServiceNotificationClient")
    // consumed directly by NotificationController, so there is no longer a
    // gateway-local fallback to gate. The kill-switch flag is gone with it.

    // Push — REMOVED. The device-register / push surface is now a stateless
    // passthrough over the salehly-mirrored NSwag ServicePushNotificationClient
    // (registered in Program.cs as the named client "ServicePushNotificationClient")
    // consumed directly by PushNotificationController, so there is no longer a
    // gateway-local fallback to gate. The kill-switch flag is gone with it.

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
    /// When true, asset persist/read paths proxy the real <c>cdn-service</c>
    /// (durable object store for signed-ToS PDFs, earnings statements, evidence;
    /// 90-day retention + signed URLs) via
    /// <see cref="JeebGateway.Services.Clients.ICDNServiceClient"/>. Serves
    /// JEB-527 / JEB-519 / JEB-59.
    ///
    /// DEFAULT-OFF everywhere — including <c>appsettings.Production.json</c> —
    /// because cdn-service is NOT yet deployed (its Production BaseUrl is a
    /// placeholder, <c>http://192.168.2.50:PORT_TBD/</c>). This is a net-new path
    /// with no legacy in-memory fallback, so the flag is a runtime kill switch:
    /// while off, <see cref="JeebGateway.Controllers.CdnController"/> returns 503
    /// instead of dialing the unroutable host. Flip to true (and set the real
    /// BaseUrl + add a readiness probe) once cdn-service ships.
    /// </summary>
    public bool Cdn { get; set; }

    // Feedback no longer has a kill-switch flag. The exact-salehly feedback
    // migration replaced the gated jeeb feedback BFF (IFeedbackServiceClient +
    // FeatureFlags:UseUpstream:Feedback) with the ungated salehly mirror
    // ServiceFeedbackClient, which FeedbackController/RatingService consume
    // directly and always forward to the real feedback-service — there is no
    // 503-without-calling path, matching salehly. Any residual
    // FeatureFlags:UseUpstream:Feedback config key binds to nothing and is ignored.

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
