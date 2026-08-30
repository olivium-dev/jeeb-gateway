using System;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner.Auth;

/// <summary>
/// Verifies configured partner credentials and short-lived DevOnly runtime credentials. Runtime
/// reservations, activation, one-shot claims, deadlines, and revocation are shared across replicas.
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
    /// Fails before any wallet mutation when a runtime login/holder would collide with a
    /// configured credential, a revoked runtime holder, or a different runtime binding.
    /// </summary>
    Task ReserveRuntimeSeedAsync(
        string login, Guid holderId, string displayName, string secret, CancellationToken ct);

    /// <summary>
    /// <b>[DevOnly] test/dev seam.</b> Activates the already-reserved runtime credential only after
    /// wallet provisioning succeeds. Repeating the exact binding cannot reset one-shot use or the
    /// original five-minute deadline. This is reachable only from the config-gated dev endpoint.
    /// </summary>
    Task ActivateRuntimeSeedAsync(string login, Guid holderId, CancellationToken ct);

    /// <summary>
    /// Links the exact bounded refresh family minted by a successful one-shot runtime login. The
    /// link is immutable and is the only token family Dev Tool cleanup is allowed to revoke.
    /// </summary>
    Task BindRuntimeSessionAsync(
        string login,
        Guid holderId,
        string sessionFamilyId,
        CancellationToken ct);

    /// <summary>
    /// Removes a runtime dev credential. The expected holder lets a fresh replica durably revoke
    /// the bounded session even when it has no process-local login mapping. Configured credentials
    /// are never exposed through the dev endpoint, whose generated login namespace is distinct.
    /// </summary>
    Task<RuntimeCredentialSession> RemoveAsync(
        string login,
        Guid expectedHolderId,
        CancellationToken ct);
}

public sealed record RuntimeCredentialSession(Guid HolderId, string SessionFamilyId);

public sealed class RuntimeCredentialNotFoundException : Exception
{
    public RuntimeCredentialNotFoundException(string message) : base(message) { }
}
