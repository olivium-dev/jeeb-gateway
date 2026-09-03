namespace JeebGateway.Health;

/// <summary>How a configured credential source carries its material.</summary>
public enum GatewayCredentialSourceKind
{
    /// <summary>An absolute path to a mounted secret file (Swarm <c>/run/secrets/*</c>, or any host path a native deploy supplies).</summary>
    SecretFile,

    /// <summary>An inline configuration/environment value (the native-host fallback).</summary>
    Value,
}

/// <summary>One rung of a credential's ordered resolution chain.</summary>
/// <param name="ConfigurationKey">The configuration key read for this rung.</param>
/// <param name="Kind">Whether the key names a secret file or holds the value.</param>
public sealed record GatewayCredentialSource(
    string ConfigurationKey,
    GatewayCredentialSourceKind Kind);

/// <summary>
/// A credential the gateway must be able to resolve at runtime, declared so a
/// single readiness check can exercise it. The 608debf outage (23h of dead push
/// behind a green /health/ready) happened because a Swarm-only
/// <c>/run/secrets</c> path was a committed default on a native host and no
/// surface traversed the fail-closed chain. Committed configuration therefore
/// declares only KEYS; the deploy supplies the path or the value.
/// </summary>
/// <param name="Name">The name this credential reports under on <c>/health/ready</c>.</param>
/// <param name="ArmedDescription">Human-readable statement of the gate in <paramref name="IsArmed"/>.</param>
/// <param name="IsArmed">True when the consuming feature can actually dial, so the credential is required.</param>
/// <param name="Chain">Ordered resolution chain; the first usable rung wins, exactly as the runtime handler resolves it.</param>
public sealed record GatewayCredentialDeclaration(
    string Name,
    string ArmedDescription,
    Func<IConfiguration, bool> IsArmed,
    IReadOnlyList<GatewayCredentialSource> Chain);

/// <summary>
/// The declared credential roster. Every entry here becomes one
/// <see cref="ConfiguredCredentialHealthCheck"/> registration and one
/// <c>/health/ready</c> row, so a credential cannot be silently unresolvable.
/// </summary>
public static class GatewayCredentialDeclarations
{
    private static bool Flag(IConfiguration configuration, string key, bool defaultValue = false) =>
        configuration.GetValue(key, defaultValue);

    private static bool NonEmpty(IConfiguration configuration, string key) =>
        !string.IsNullOrWhiteSpace(configuration[key]);

    private static GatewayCredentialSource File(string key) =>
        new(key, GatewayCredentialSourceKind.SecretFile);

    private static GatewayCredentialSource Value(string key) =>
        new(key, GatewayCredentialSourceKind.Value);

    public const string NotificationOwnerName = "notification-credential";

    public static readonly IReadOnlyList<GatewayCredentialDeclaration> All = new[]
    {
        // Kept under its original name: this is the check the 608debf outage produced.
        new GatewayCredentialDeclaration(
            NotificationOwnerName,
            "FeatureFlags:NotificationDurableWrite:Enabled is true",
            configuration => Flag(configuration, "FeatureFlags:NotificationDurableWrite:Enabled"),
            new[]
            {
                File("ServiceNotificationClient:ServiceTokenFile"),
                Value("ServiceNotificationClient:ServiceToken"),
                Value("NOTIFICATION_SERVICE_TOKEN"),
                Value("ServiceNotificationClient:ApiToken"),
            }),
        new GatewayCredentialDeclaration(
            "credential-state-service-token",
            "JeebStateService:Enabled is true",
            configuration => Flag(configuration, "JeebStateService:Enabled"),
            new[] { File("JeebStateService:ServiceTokenFile") }),
        new GatewayCredentialDeclaration(
            "credential-delivery-service-token",
            "FeatureFlags:UseUpstream:Delivery is true",
            configuration => Flag(configuration, "FeatureFlags:UseUpstream:Delivery"),
            new[]
            {
                File("DELIVERY_SERVICE_TOKEN_FILE"),
                File("Services:Delivery:ServiceTokenFile"),
                Value("DELIVERY_SERVICE_TOKEN"),
                Value("Services:Delivery:ServiceToken"),
            }),
        new GatewayCredentialDeclaration(
            "credential-bundler-cms-bearer",
            "BUNDLER_CMS_BASE_URL is configured",
            configuration => NonEmpty(configuration, "BUNDLER_CMS_BASE_URL"),
            new[] { File("BUNDLER_CMS_BEARER_TOKEN_FILE") }),
        new GatewayCredentialDeclaration(
            "credential-internal-job-token",
            "always armed: the internal job plane is unconditional",
            _ => true,
            new[] { File("InternalJobAuth:TokenFile") }),
        new GatewayCredentialDeclaration(
            "credential-private-artifact-store-bearer",
            "Users:DataExport:Enabled is true and PRIVATE_ARTIFACT_STORE_BASE_URL is configured",
            configuration => Flag(configuration, "Users:DataExport:Enabled", defaultValue: true)
                             && NonEmpty(configuration, "PRIVATE_ARTIFACT_STORE_BASE_URL"),
            new[] { File("PRIVATE_ARTIFACT_STORE_BEARER_TOKEN_FILE") }),
        new GatewayCredentialDeclaration(
            "credential-data-export-signing-key",
            "Users:DataExport:Enabled is true",
            configuration => Flag(configuration, "Users:DataExport:Enabled", defaultValue: true),
            new[] { File("DATA_EXPORT_TOKEN_SIGNING_KEY_FILE") }),
    };
}
