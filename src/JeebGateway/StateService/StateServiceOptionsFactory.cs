using Microsoft.Extensions.Configuration;

namespace JeebGateway.StateService;

/// <summary>
/// The single place configuration becomes <see cref="StateServiceOptions"/>. Program.cs built this
/// by hand and never set <c>ServiceTokenFile</c>, so the 2026-08-11 cutover left the main typed
/// client unauthenticated (login 500s) while <c>/health/ready</c> stayed green.
/// </summary>
public static class StateServiceOptionsFactory
{
    public const string BaseUrlKey = $"{StateServiceOptions.SectionName}:BaseUrl";
    public const string LegacyBaseUrlKey = "Services:JeebState:BaseUrl";
    public const string TimeoutSecondsKey = $"{StateServiceOptions.SectionName}:TimeoutSeconds";
    public const string EnabledKey = $"{StateServiceOptions.SectionName}:Enabled";
    public const string ServiceTokenFileKey = $"{StateServiceOptions.SectionName}:ServiceTokenFile";

    public static StateServiceOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var tokenFile = configuration[ServiceTokenFileKey];

        return new StateServiceOptions
        {
            BaseUrl = configuration[BaseUrlKey]
                      ?? configuration[LegacyBaseUrlKey]
                      ?? string.Empty,
            TimeoutSeconds = int.TryParse(configuration[TimeoutSecondsKey], out var timeout) ? timeout : 5,
            Enabled = !bool.TryParse(configuration[EnabledKey], out var enabled) || enabled,
            ServiceTokenFile = string.IsNullOrWhiteSpace(tokenFile) ? null : tokenFile,
        };
    }
}
