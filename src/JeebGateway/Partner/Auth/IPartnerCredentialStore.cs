using System;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner.Auth;

/// <summary>
/// Verifies an admin-provisioned partner login credential (PP-1). The single seam the login front
/// door depends on, so the credential SOURCE (config roster today; a user-management partner-verify
/// surface if one is ever generated) can change without touching the controller or the token mint.
/// </summary>
public interface IPartnerCredentialStore
{
    /// <summary>
    /// Returns the <see cref="PartnerAccount"/> for a correct (<paramref name="login"/>,
    /// <paramref name="secret"/>) pair, or <c>null</c> when the login is unknown OR the secret is
    /// wrong. Implementations MUST NOT distinguish the two (no user enumeration) and MUST use a
    /// constant-time secret comparison (no timing side channel).
    /// </summary>
    Task<PartnerAccount?> VerifyAsync(string login, string secret, CancellationToken ct);

    /// <summary>
    /// <b>[DevOnly] test/dev seam.</b> Provisions a partner credential at runtime so a scenario can
    /// sign in without a committed config roster. Idempotent per login (last write wins). MUST be
    /// reachable only from the config-gated <c>[DevOnly]</c> seed endpoint — never a production path.
    /// </summary>
    void Seed(string login, Guid holderId, string displayName, string secret);
}
