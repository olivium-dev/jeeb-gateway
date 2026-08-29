namespace JeebGateway.Extensions;

/// <summary>
/// A9 roster contract (docs/runbooks/gwdbx-deletion-ledger.md §7): the declared names on
/// <c>/health/ready</c>. gwdbx W2-R11 pre-announces 19 → 20 by adding <c>settlement-service</c>.
/// Tests assert this list against what the code actually registers, so the count is not folklore.
/// </summary>
public static class GatewayHealthRoster
{
    /// <summary>Probes registered by <see cref="HealthCheckExtensions"/> (skipped in Dev/Testing
    /// and when the BaseUrl key is unset), with the config key each one keys off. The
    /// settlement-service entry is unconditional in Staging.</summary>
    public static readonly (string Name, string BaseUrlKey)[] DownstreamProbes =
    {
        ("wallet-service", "WalletServiceApi:BaseUrl"),
        ("notification-service", "ServiceNotificationClient:BaseUrl"),
        ("push-notification", "PushNotificationServiceApi:BaseUrl"),
        ("delivery-service", "Services:Delivery:BaseUrl"),
        ("geolocation-service", "Services:Geolocation:BaseUrl"),
        ("offer-service", "Services:Offer:BaseUrl"),
        ("ban-service", "Services:Ban:BaseUrl"),
        ("settlement-service", "Services:Settlement:BaseUrl"),
        ("voice-transcription", "Services:VoiceTranscription:BaseUrl"),
        ("user-management", "UserManagementServiceApi:BaseUrl"),
        ("realtime-comunication-service", "Services:Realtime:BaseUrl"),
        ("contract-signing-service", "Services:ContractSigning:BaseUrl"),
        ("cdn-service", "Services:Cdn:BaseUrl"),
        ("form-builder-service", "Services:FormBuilder:BaseUrl"),
        // Registered by HealthCheckExtensions as BundlerServiceHealthCheck (not a URL group),
        // still gated on the same BaseUrl key, so name and registration cannot drift.
        (JeebGateway.Cms.BundlerServiceHealthCheck.Name, JeebGateway.Cms.BundlerCmsSurfaceStore.BaseUrlConfigurationKey),
    };

    /// <summary>Ready-tagged checks registered in <c>Program.cs</c> rather than the extension.</summary>
    public static readonly string[] InProcessChecks =
    {
        "admin-oidc-configuration",
        // 2026-08-23: proves the notification owner credential resolves (608debf outage
        // stayed green because no surface exercised the fail-closed credential chain).
        JeebGateway.Notifications.NotificationCredentialHealthCheck.Name,
        // 2026-08-29: the mounted gateway key must be accepted for its fixed
        // provider scope, not merely resolve from the local secret file.
        JeebGateway.Notifications.PushRelayCredentialHealthCheck.Name,
        "whisper",
        "jeeb-state-service",
    };

    /// <summary>The full live roster, sorted. "self" is live-tagged and not part of ready.</summary>
    public static IReadOnlyList<string> Ready { get; } =
        DownstreamProbes.Select(p => p.Name).Concat(InProcessChecks).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>A9 asserted count. Production registers 15 downstream probes plus the
    /// in-process checks. 21 through W2-R11; 20 from W5-10 (WalletPostgres seam deleted);
    /// 18 from W5-11, which deleted the GatewayPostgres seam and with it both the
    /// gateway-postgres probe and the store-durability guard. (The pre-W5-10 figure of 20
    /// undercounted: bundler-service was registered but undeclared.) 19 from the D1 fix,
    /// which added a role-service probe; back to 18 on 2026-08-16 when owner decision O8
    /// retired role-service outright — that probe was the aggregate's only Degraded entry,
    /// verified against a real /health/ready read before and after. 19 from 2026-08-23:
    /// notification-credential joined after the 608debf silent push outage; 20 from
    /// 2026-08-29 when relay scoped-readiness became a deployment gate.</summary>
    public const int ExpectedReadyCount = 20;
}
