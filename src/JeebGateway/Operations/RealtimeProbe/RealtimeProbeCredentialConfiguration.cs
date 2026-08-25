using JeebGateway.Realtime;
using Microsoft.Extensions.Options;

namespace JeebGateway.Operations.RealtimeProbe;

internal interface IRealtimeProbeCredentialConfigurationGuard
{
    bool IsExact { get; }
}

/// <summary>
/// Pins the staging probe to the deployed, file-backed realtime authorities. This
/// prevents either existing issuer from taking its local/native inline or JWT-key
/// fallback when staging configuration drifts.
/// </summary>
internal sealed class RealtimeProbeCredentialConfigurationGuard
    : IRealtimeProbeCredentialConfigurationGuard
{
    internal const string GuardianSecretFile = "/run/secrets/realtime_guardian_secret";
    internal const string MembershipTicketSigningKeyFile =
        "/run/secrets/realtime_membership_ticket_key";

    private readonly RealtimeGuardianOptions _options;

    public RealtimeProbeCredentialConfigurationGuard(
        IOptions<RealtimeGuardianOptions> options)
    {
        _options = options.Value;
    }

    public bool IsExact => HasExactStagingAuthorities(_options);

    internal static bool HasExactStagingAuthorities(RealtimeGuardianOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.IsNullOrWhiteSpace(options.GuardianSecret)
            && string.Equals(
                options.GuardianSecretFile,
                GuardianSecretFile,
                StringComparison.Ordinal)
            && string.Equals(
                options.MembershipTicketSigningKeyFile,
                MembershipTicketSigningKeyFile,
                StringComparison.Ordinal)
            && string.Equals(
                options.GuardianIssuer,
                RealtimeProbeDescriptorService.ExactGuardianIssuer,
                StringComparison.Ordinal)
            && string.Equals(
                options.PublicSocketUrl,
                RealtimeProbeDescriptorService.ExactPublicSocketUrl,
                StringComparison.Ordinal)
            && string.Equals(
                options.TenantPrefix,
                RealtimeGuardianOptions.DefaultTenantPrefix,
                StringComparison.Ordinal);
    }
}
