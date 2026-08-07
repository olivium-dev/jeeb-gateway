using JeebGateway.Auth.Capabilities;
using JeebGateway.Users;

namespace JeebGateway.Auth.Oidc;

/// <summary>
/// External administrator identity configuration. The provider owns credentials,
/// recovery, lockout, and MFA. The gateway accepts only a signed OIDC result and
/// maps configured provider groups onto the existing narrow operator roles.
/// </summary>
public sealed class AdminOidcOptions
{
    public const string SectionName = "AdminOidc";

    public bool Enabled { get; init; }
    public string Issuer { get; init; } = string.Empty;
    public string AuthorizationEndpoint { get; init; } = string.Empty;
    public string TokenEndpoint { get; init; } = string.Empty;
    public string JwksUri { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = string.Empty;
    public string StateProtectionKey { get; init; } = string.Empty;
    public string GroupClaim { get; init; } = "groups";
    public string[] Scopes { get; init; } = ["openid", "profile", "email"];
    public string[] AllowedSigningAlgorithms { get; init; } = ["RS256", "ES256"];
    /// <summary>
    /// Space-separated, case-sensitive provider ACR values that independently
    /// prove the required administrator assurance. When absent, the signed ID
    /// token must contain the explicit AMR value <c>mfa</c>. Factor names and
    /// AMR counts are never treated as MFA proof.
    /// </summary>
    public string? RequiredAcrValues { get; init; }
    public int CorrelationLifetimeMinutes { get; init; } = 10;
    public int MaxAuthenticationAgeMinutes { get; init; } = 5;
    public int OperatorSessionHours { get; init; } = 8;

    /// <summary>
    /// Canonical gateway role -&gt; exact IdP group names. Environment variables use
    /// <c>AdminOidc__RoleMappings__finance_approver__0=jeeb-finance-approvers</c>.
    /// </summary>
    public Dictionary<string, string[]> RoleMappings { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool TryLoad(
        IConfiguration configuration,
        out AdminOidcOptions options,
        out string error)
    {
        try
        {
            options = configuration.GetSection(SectionName).Get<AdminOidcOptions>() ?? new AdminOidcOptions();
            error = options.Validate();
            return error.Length == 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            options = new AdminOidcOptions();
            error = "AdminOidc contains an invalid configuration value.";
            return false;
        }
    }

    public string Validate()
    {
        if (!Enabled) return "AdminOidc is disabled.";
        if (!AdminOidcEndpointPolicy.TryPublicHttpsUri(Issuer, out var issuer))
            return "AdminOidc:Issuer must be a public absolute HTTPS URL.";
        if (!AdminOidcEndpointPolicy.TryPublicHttpsUri(AuthorizationEndpoint, out var authorization))
            return "AdminOidc:AuthorizationEndpoint must be a public absolute HTTPS URL.";
        if (!AdminOidcEndpointPolicy.TryPublicHttpsUri(TokenEndpoint, out var token))
            return "AdminOidc:TokenEndpoint must be a public absolute HTTPS URL.";
        if (!AdminOidcEndpointPolicy.TryPublicHttpsUri(JwksUri, out var jwks))
            return "AdminOidc:JwksUri must be a public absolute HTTPS URL.";
        if (!AdminOidcEndpointPolicy.TryPublicHttpsUri(RedirectUri, out _))
            return "AdminOidc:RedirectUri must be a public absolute HTTPS URL.";
        if (!AdminOidcEndpointPolicy.SameOrigin(issuer!, authorization!)
            || !AdminOidcEndpointPolicy.SameOrigin(issuer!, token!)
            || !AdminOidcEndpointPolicy.SameOrigin(issuer!, jwks!))
            return "AdminOidc provider endpoints must use the exact Issuer origin.";
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 512)
            return "AdminOidc:ClientId is required and must be bounded.";
        if (string.IsNullOrWhiteSpace(ClientSecret) || ClientSecret.Length > 2048)
            return "AdminOidc:ClientSecret is required and must be bounded.";
        if (string.IsNullOrWhiteSpace(GroupClaim) || GroupClaim.Length > 128 || GroupClaim.Any(char.IsControl))
            return "AdminOidc:GroupClaim is required and must be bounded.";
        if (CorrelationLifetimeMinutes is < 2 or > 15)
            return "AdminOidc:CorrelationLifetimeMinutes must be between 2 and 15.";
        if (MaxAuthenticationAgeMinutes is < 1 or > 10)
            return "AdminOidc:MaxAuthenticationAgeMinutes must be between 1 and 10.";
        if (OperatorSessionHours is < 1 or > 12)
            return "AdminOidc:OperatorSessionHours must be between 1 and 12.";
        if (Scopes is null || Scopes.Length is 0 or > 16
            || !Scopes.Contains("openid", StringComparer.Ordinal)
            || Scopes.Any(static value => string.IsNullOrWhiteSpace(value)
                                                   || value.Length > 128
                                                   || value.Any(char.IsWhiteSpace)
                                                   || value.Any(char.IsControl)))
            return "AdminOidc:Scopes must include openid.";
        if (AllowedSigningAlgorithms is null || AllowedSigningAlgorithms.Length is 0 or > 2
            || AllowedSigningAlgorithms.Any(static value => value is not ("RS256" or "ES256")))
            return "AdminOidc:AllowedSigningAlgorithms may contain only RS256 or ES256.";
        if (!string.IsNullOrWhiteSpace(RequiredAcrValues))
        {
            var acrValues = RequiredAcrValues.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (acrValues.Length is 0 or > 8
                || acrValues.Distinct(StringComparer.Ordinal).Count() != acrValues.Length
                || acrValues.Any(static value => value.Length > 256 || value.Any(char.IsControl)))
                return "AdminOidc:RequiredAcrValues must contain unique, bounded assurance values.";
        }
        if (!TryStateKey(out _))
            return "AdminOidc:StateProtectionKey must be a base64-encoded 32-byte secret.";
        if (RoleMappings is null || RoleMappings.Count is 0 or > 5)
            return "AdminOidc:RoleMappings must map at least one operator role.";

        foreach (var (role, groups) in RoleMappings)
        {
            if (!AdminOidcRoleMapper.AllowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                return $"AdminOidc:RoleMappings contains unsupported role '{role}'.";
            if (groups is null || groups.Length == 0
                               || groups.Any(static group => string.IsNullOrWhiteSpace(group) || group.Length > 256))
                return $"AdminOidc:RoleMappings:{role} must contain bounded group names.";
        }
        return string.Empty;
    }

    public bool TryStateKey(out byte[] key)
    {
        try
        {
            key = Convert.FromBase64String(StateProtectionKey);
            return key.Length == 32;
        }
        catch (FormatException)
        {
            key = [];
            return false;
        }
    }

}

public static class AdminOidcRoleMapper
{
    public static readonly IReadOnlyCollection<string> AllowedRoles = new[]
    {
        Roles.Support,
        Roles.SupportLead,
        Roles.Operations,
        Roles.FinanceViewer,
        Roles.FinanceApprover,
    };

    public static IReadOnlyList<string> Map(AdminOidcOptions options, IEnumerable<string> groups)
    {
        var asserted = groups.ToHashSet(StringComparer.Ordinal);
        var roles = options.RoleMappings
            .Where(mapping => mapping.Value.Any(asserted.Contains))
            .Select(mapping => AllowedRoles.Single(role =>
                role.Equals(mapping.Key, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static role => role, StringComparer.Ordinal)
            .ToArray();

        if (roles.Contains(Roles.FinanceApprover, StringComparer.OrdinalIgnoreCase)
            && roles.Any(role => role.Equals(Roles.Support, StringComparison.OrdinalIgnoreCase)
                                 || role.Equals(Roles.SupportLead, StringComparison.OrdinalIgnoreCase)
                                 || role.Equals(Roles.Operations, StringComparison.OrdinalIgnoreCase)))
            return [];

        var portalRoles = CapabilityRolePolicy.RolesFor(
            JeebGateway.Auth.Capabilities.Capabilities.AdminPortalAccess);
        return roles.Where(role => portalRoles.Contains(role, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
}
