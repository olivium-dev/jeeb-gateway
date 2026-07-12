namespace JeebGateway.Services.Bff;

/// <summary>
/// JEB-67 / T-BE-031 AC1 — declares which downstream services the BFF is
/// required to have a BaseUrl for in a given environment. Startup runs the
/// validator in <see cref="BffStartupValidator"/> against this list; a
/// missing key fails the boot with a structured error naming the missing
/// config path.
///
/// Defaults match the services already registered in
/// <see cref="Extensions.ServiceClientExtensions.AddDownstreamClients"/>.
/// Tests override the list when they want to assert the AC1 failure path
/// without standing up every downstream URL.
///
/// Why this is opt-in (RequiredInProduction) rather than always-on: dev and
/// integration test environments deliberately omit URLs for services they
/// are not exercising. Making the validator unconditional would block the
/// xUnit/WebApplicationFactory bootstrap.
/// </summary>
public sealed class DownstreamServicesOptions
{
    public const string SectionName = "BffServices";

    /// <summary>
    /// When true the validator enforces presence of every entry in
    /// <see cref="Required"/>. Defaults to true so a production deploy
    /// without explicit override fails closed.
    /// </summary>
    public bool RequiredInProduction { get; set; } = true;

    /// <summary>
    /// Set of <c>Services:{Section}:BaseUrl</c> sub-section names that the
    /// gateway must have configured. Validator throws a structured error
    /// listing every missing one (not just the first) so operators get a
    /// single round trip on the fix.
    /// </summary>
    // wallet-service is intentionally absent: it is wired in Program.cs as a
    // salehly-mirrored named client bound to the top-level WalletServiceApi:BaseUrl
    // key (not a Services:{Section} downstream), so it is not validated here.
    // NOTE: chat-service is intentionally NOT listed here. Chat moved to the
    // salehly-style top-level ChatServiceApi:BaseUrl key (consumed directly by the
    // NSwag ServiceChatClient in Program.cs) and is no longer a Services:* nested
    // downstream client, so this Services:{section}:BaseUrl validator does not
    // cover it.
    // NOTE: notification-service is intentionally NOT listed here. Notification
    // moved to the salehly-style top-level ServiceNotificationClient:BaseUrl key
    // (consumed directly by the NSwag ServiceNotificationClient in Program.cs) and
    // is no longer a Services:* nested downstream client.
    // NOTE: push-notification is intentionally NOT listed here. PushNotification
    // moved to the salehly-style top-level PushNotificationServiceApi:BaseUrl key
    // (consumed directly by the NSwag ServicePushNotificationClient in Program.cs)
    // and is no longer a Services:* nested downstream client, so this
    // Services:{section}:BaseUrl validator does not cover it.
    // NOTE: "Matching" is intentionally NOT listed here. The standalone
    // matching-service read path was retired (JEBV4-220 / E25, Q-020): nothing
    // dials Services:Matching, so a production boot must NOT fail closed on a
    // missing matching key. Courier matching lives in delivery-service.
    public List<string> Required { get; set; } = new()
    {
        "Auth",
        "UserManagement",
        "Geolocation",
        "Delivery",
    };

    /// <summary>
    /// Per-section overrides for the config key the validator probes. Most
    /// downstreams live under the nested <c>Services:{Section}:BaseUrl</c> key,
    /// but a few were migrated to a salehly-style TOP-LEVEL
    /// <c>{Service}ServiceApi:BaseUrl</c> key and are consumed by an NSwag typed
    /// client registered directly in <c>Program.cs</c> rather than via the
    /// generic <c>Services:*</c> named-client helper.
    ///
    /// <para><b>UserManagement</b> is exactly that case (config-key drift fix):
    /// the gateway dials user-management through the scoped
    /// <c>ServiceUserManagementClient</c> bound to
    /// <c>UserManagementServiceApi:BaseUrl</c> (Program.cs) and the readiness
    /// probe (HealthCheckExtensions) uses the same key. The validator must
    /// therefore enforce that SAME canonical key — not a phantom
    /// <c>Services:UserManagement:BaseUrl</c> that nothing actually reads — so a
    /// production boot validates what the app really uses and operators set ONE
    /// key. See <see cref="ConfigKeyFor"/>.</para>
    /// </summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UserManagement"] = "UserManagementServiceApi:BaseUrl",
    };

    /// <summary>
    /// Resolves the config key the validator probes for a required section:
    /// the explicit <see cref="Keys"/> override if present, otherwise the
    /// default nested <c>Services:{section}:BaseUrl</c> shape.
    /// </summary>
    public string ConfigKeyFor(string section) =>
        Keys.TryGetValue(section, out var key) && !string.IsNullOrWhiteSpace(key)
            ? key
            : $"Services:{section}:BaseUrl";
}
