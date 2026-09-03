namespace JeebGateway.Health;

/// <summary>How a configured credential source carries its material.</summary>
public enum GatewayCredentialSourceKind
{
    /// <summary>An absolute path to a mounted secret file (Swarm <c>/run/secrets/*</c>, or any host path a native deploy supplies).</summary>
    SecretFile,

    /// <summary>An inline configuration/environment value (the native-host fallback).</summary>
    Value,
}

/// <summary>One rung of a credential's ordered resolution chain: the key read, and whether
/// it names a secret file or holds the value.</summary>
public sealed record GatewayCredentialSource(
    string ConfigurationKey,
    GatewayCredentialSourceKind Kind);

/// <summary>A credential the gateway must resolve at runtime, declared so one readiness check can
/// exercise it. Committed config declares KEYS only; the deploy supplies the path or value.</summary>
public sealed record GatewayCredentialDeclaration(
    string Name,
    string ArmedDescription,
    Func<IConfiguration, bool> IsArmed,
    IReadOnlyList<GatewayCredentialSource> Chain);

/// <summary>The declared credential roster: every entry becomes one health-check registration
/// and one <c>/health/ready</c> row.</summary>
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
