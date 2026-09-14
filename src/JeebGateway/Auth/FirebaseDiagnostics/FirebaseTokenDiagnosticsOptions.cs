using Microsoft.Extensions.Hosting;

namespace JeebGateway.Auth.FirebaseDiagnostics;

public sealed class FirebaseTokenDiagnosticsOptions
{
    public const string SectionName = "Auth:FirebaseTokenDiagnostics";
    public const string DevelopmentEnvironment = "development";
    public const string DevelopmentProjectId = "jeeb-development-msi";
    public const string DevelopmentMsiHostName = "ouday-GT70-2OC-2OD";
    public const string StagingEnvironment = "staging";
    public const string StagingProjectId = "jeeb-5a293";

    public bool Enabled { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;

    public static bool IsAllowed(
        IHostEnvironment host,
        FirebaseTokenDiagnosticsOptions options,
        string? machineName = null)
    {
        if (!options.Enabled)
        {
            return false;
        }

        machineName ??= System.Environment.MachineName;

        var isDevelopment =
            (host.IsDevelopment()
                || (host.IsProduction()
                    && string.Equals(
                        machineName,
                        DevelopmentMsiHostName,
                        StringComparison.Ordinal)))
            && string.Equals(options.Environment, DevelopmentEnvironment, StringComparison.Ordinal)
            && string.Equals(options.ProjectId, DevelopmentProjectId, StringComparison.Ordinal);

        var isStaging =
            host.IsStaging()
            && string.Equals(options.Environment, StagingEnvironment, StringComparison.Ordinal)
            && string.Equals(options.ProjectId, StagingProjectId, StringComparison.Ordinal);

        return isDevelopment || isStaging;
    }
}
