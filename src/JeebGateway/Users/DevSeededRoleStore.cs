using System.Collections.Concurrent;

namespace JeebGateway.Users;

/// <summary>
/// JEBV4-314 — a gateway-local, DEV-ONLY record of the role a
/// <c>POST /dev/seed/user</c> caller requested, so the subsequent
/// <c>POST /v1/auth/login</c> email/password mint reflects that role.
///
/// <para><b>Why this exists.</b> <c>DevController.SeedUser</c> creates a real
/// user-management user via <c>RegisterAsync</c>, but user-management has no role
/// column on the register contract — so the requested <c>role</c> (e.g. admin) was
/// dropped. The email/password login facade resolves roles ONLY from user-management
/// (<c>GET /api/User/{id}/roles</c>), which never learned about the seed, so every
/// seeded login minted <c>roles: customer</c> and admin endpoints 403'd. This store is
/// the gateway-owned bridge the ticket calls for: the seed records the role HERE, and
/// <see cref="JeebGateway.Auth.OtpSignIn.AuthEmailFacadeController"/> consults it when
/// minting the session.</para>
///
/// <para><b>Dev-scoped by construction.</b> The ONLY writer is the
/// <c>[DevOnly]</c>-gated <c>SeedUser</c> action, which returns 404 unless
/// <c>Features:DevEndpoints:Enabled</c> is true (committed false in every environment,
/// including production). In production the map is therefore always empty, so the login
/// consult is a no-op and real login role-resolution is completely unchanged. The store
/// is process-local and volatile (a gateway restart clears it) — acceptable for a test
/// seam that seeds fresh users per run.</para>
///
/// <para>Keyed by BOTH the user-management canonical <c>userId</c> and the login email,
/// so the login mint (which resolves the userId from user-management and also holds the
/// caller's email) can join on whichever it has.</para>
/// </summary>
public interface IDevSeededRoleStore
{
    /// <summary>Record the OPAQUE roles a dev-seeded user should carry. No-op when both keys are blank.</summary>
    void Record(string? userId, string? email, IReadOnlyList<string> roles);

    /// <summary>
    /// The recorded OPAQUE roles for a user identified by <paramref name="userId"/> or
    /// <paramref name="email"/>, or <c>null</c> when nothing was seeded (the production /
    /// non-seeded case).
    /// </summary>
    IReadOnlyList<string>? Resolve(string? userId, string? email);
}

/// <inheritdoc cref="IDevSeededRoleStore"/>
public sealed class DevSeededRoleStore : IDevSeededRoleStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _byUserId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _byEmail =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(string? userId, string? email, IReadOnlyList<string> roles)
    {
        if (roles is not { Count: > 0 }) return;

        if (!string.IsNullOrWhiteSpace(userId))
        {
            _byUserId[userId.Trim()] = roles;
        }
        var normalizedEmail = NormalizeEmail(email);
        if (normalizedEmail is not null)
        {
            _byEmail[normalizedEmail] = roles;
        }
    }

    public IReadOnlyList<string>? Resolve(string? userId, string? email)
    {
        if (!string.IsNullOrWhiteSpace(userId) && _byUserId.TryGetValue(userId.Trim(), out var byId))
        {
            return byId;
        }
        var normalizedEmail = NormalizeEmail(email);
        if (normalizedEmail is not null && _byEmail.TryGetValue(normalizedEmail, out var byEmail))
        {
            return byEmail;
        }
        return null;
    }

    private static string? NormalizeEmail(string? email)
        => string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
}
