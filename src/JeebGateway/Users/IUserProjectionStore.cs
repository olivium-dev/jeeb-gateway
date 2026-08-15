namespace JeebGateway.Users;

/// <summary>
/// Identity <b>projection</b> contract for the gateway users store.
///
/// <para>The gateway is a stateless BFF; user-management (UM) is the identity system of
/// record. This contract exists so admin user-search and the CMS dashboard can be served
/// from a projection without the gateway owning identity.</para>
///
/// <para>W5-11 deleted the gateway-Postgres implementation of this interface. The contract
/// itself is retained because the upstream-backed stores implement it — it describes the
/// projection shape, not a database.</para>
///
/// <para><b>Upsert semantics (<see cref="UpsertIdentityAsync"/>).</b> Identity fields UM
/// owns (roles / active_role / language / phone) are replaced by the incoming projection;
/// DISPLAY fields (name / email / avatar_url) are blank-preserving — an incoming blank
/// never wipes a display value the profile-update mirror or /me hydration already learned
/// (the jeeberName-gap invariant, matching
/// <see cref="InMemoryUsersStore.UpsertProjectionAsync"/>). Suspension, rating and
/// created_at are left UNTOUCHED on conflict so a re-login never un-suspends a user or
/// clobbers the score-service's denormalised rating. Suspension is mutated only through
/// <see cref="SetSuspensionAsync"/>; PII is purged through <see cref="PurgePiiAsync"/>.</para>
/// </summary>
public interface IUserProjectionStore
{
    /// <summary>Point-lookup of the projection row, or null when absent.</summary>
    Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Admin user-search over name / phone / email, case-insensitive and paginated.
    /// </summary>
    Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct);

    /// <summary>
    /// Idempotent upsert of the UM-resolved identity projection (blank-preserving
    /// display; suspension / rating / created_at preserved on conflict).
    /// </summary>
    Task UpsertIdentityAsync(UserProfile profile, CancellationToken ct);

    /// <summary>Flips suspension state (kept constraint-consistent internally).</summary>
    Task SetSuspensionAsync(
        string userId, bool isSuspended, string? reason, DateTimeOffset? at, CancellationToken ct);

    /// <summary>GDPR PII purge — name → '', email → NULL, avatar_url → NULL.</summary>
    Task PurgePiiAsync(string userId, CancellationToken ct);

    /// <summary>
    /// CMS dashboard read (D2): total rows + a count per OPAQUE role. Default returns
    /// <see cref="UserRoleCounts.Empty"/> so unrelated test doubles keep compiling.
    /// </summary>
    Task<UserRoleCounts> CountByRolesAsync(IReadOnlyCollection<string> opaqueRoles, CancellationToken ct)
        => Task.FromResult(UserRoleCounts.Empty);
}
