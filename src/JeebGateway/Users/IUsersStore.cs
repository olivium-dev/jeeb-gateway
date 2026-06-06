namespace JeebGateway.Users;

/// <summary>
/// Storage abstraction for user profiles and their saved addresses.
/// The default in-memory implementation is intended for early-MVP local
/// runs and integration tests; production wiring will proxy to the
/// auth-service via an NSwag-generated client (or, transitionally, hit
/// Postgres directly using the schema in db/migrations/0006).
/// </summary>
public interface IUsersStore
{
    Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct);

    Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct);

    /// <summary>
    /// S02 Wave-1 (ADR-003, F-C). Inserts or replaces the local projection of an
    /// identity resolved upstream by user-management (id + OPAQUE roles + active role),
    /// so the gateway-minted session JWT embeds the SAME <c>active_role</c>/<c>roles</c>
    /// claims user-management persisted. Additive; the gateway still SIGNS the OTP
    /// sign-in session (mint stays in the gateway), it just no longer invents the
    /// identity. Idempotent upsert — safe to call repeatedly.
    /// </summary>
    Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct);

    Task<UserProfile> UpdateProfileAsync(string userId, ProfilePatch patch, CancellationToken ct);

    Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(string userId, CancellationToken ct);

    Task<SavedAddress?> GetAddressAsync(string userId, string addressId, CancellationToken ct);

    Task<SavedAddress> CreateAddressAsync(string userId, AddressUpsert input, CancellationToken ct);

    Task<SavedAddress?> UpdateAddressAsync(string userId, string addressId, AddressUpsert patch, CancellationToken ct);

    Task<bool> DeleteAddressAsync(string userId, string addressId, CancellationToken ct);

    Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct);

    /// <summary>
    /// Marks <paramref name="userId"/> suspended with the given reason and
    /// recording admin. Returns the updated profile, or null when the user
    /// does not exist. T-backend-030.
    /// </summary>
    Task<UserProfile?> SuspendAsync(string userId, string reason, string adminId, CancellationToken ct);

    /// <summary>
    /// Lifts a suspension. Returns the updated profile, or null when the
    /// user does not exist. Safe to call on a user who is not currently
    /// suspended (no-op). T-backend-030.
    /// </summary>
    Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct);

    /// <summary>
    /// T-backend-041. Switches <paramref name="userId"/>'s active role.
    /// Returns the updated profile, or null when the user does not exist.
    /// Callers MUST have already validated BR-1 (no active deliveries in
    /// the current role) before invoking — the store does not enforce
    /// business rules.
    /// </summary>
    Task<UserProfile?> SwitchRoleAsync(string userId, string newRole, CancellationToken ct);

    /// <summary>
    /// T-backend-005. Adds <paramref name="role"/> to the user's roles
    /// list. No-op when the user already holds the role. Returns the
    /// updated profile or null when the user does not exist. Used by
    /// the admin KYC approve flow to unlock the Jeeber role (AC #2 —
    /// within 5 seconds of approval).
    /// </summary>
    Task<UserProfile?> GrantRoleAsync(string userId, string role, CancellationToken ct);

    /// <summary>
    /// Hard-delete every piece of PII for <paramref name="userId"/>:
    ///   * name → empty
    ///   * phone → empty
    ///   * email → null
    ///   * avatar_url → null
    ///   * all saved addresses (full PII) → removed
    /// The user row itself is kept so anonymized orders can still
    /// resolve their foreign key. Returns false when no such user exists.
    /// </summary>
    Task<bool> PurgePiiAsync(string userId, CancellationToken ct);
}

public class ProfilePatch
{
    public string? Name { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Language { get; init; }
    public string? Email { get; init; }
}

public class AddressUpsert
{
    public string? Label { get; init; }
    public string? Line1 { get; init; }
    public string? Line2 { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public bool? IsDefault { get; init; }
}

public class UserSearchQuery
{
    public string? Name { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public class UserSearchResult
{
    public required IReadOnlyList<UserProfile> Items { get; init; }
    public required int Total { get; init; }
}

public class DuplicateAddressLabelException : Exception
{
    public DuplicateAddressLabelException(string label) : base($"An address with label '{label}' already exists.") { }
}
