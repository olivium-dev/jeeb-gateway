namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>
/// appsettings.Production.json arms JeebStateService:ServiceTokenFile at
/// /run/secrets/jeeb_state_service_token, which only exists inside the deployed container.
/// StateServiceCredentialStartupGuard refuses to boot when that path is unreadable, so every
/// Production-environment test host must point the key at a readable stand-in — exactly as
/// those hosts already stand in for Jwt:SigningKey and Redis:ConnectionString.
/// </summary>
internal static class ProductionMountedSecrets
{
    public const string StateServiceTokenFileKey = "JeebStateService:ServiceTokenFile";

    private static readonly Lazy<string> TokenFile = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "jeeb-it-state-service-token");
        File.WriteAllText(path, new string('s', 48));
        return path;
    });

    /// <summary>A readable stand-in for the deployed mounted secret.</summary>
    public static string StateServiceTokenFile => TokenFile.Value;
}
