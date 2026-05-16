namespace JeebGateway.Users;

public class UserProfile
{
    public required string Id { get; init; }
    public required string Phone { get; init; }
    public string? Email { get; set; }
    public required string Name { get; set; }
    public string? AvatarUrl { get; set; }
    public string Language { get; set; } = "en";
    public List<string> Roles { get; set; } = new();
    public decimal? Rating { get; set; }
    public int RatingCount { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    // T-backend-030. While IsSuspended is true the auth-aware
    // SuspensionGuard rejects every Client/Jeeber mutation (request
    // creation, offer submission) with 403. Admin endpoints remain
    // reachable so an operator can lift the suspension.
    public bool IsSuspended { get; set; }
    public string? SuspensionReason { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public string? SuspendedBy { get; set; }

    /// <summary>
    /// T-backend-041. The currently active role for this dual-role account.
    /// Defaults to <see cref="Users.Roles.Client"/>. BR-1 enforcement uses
    /// this to ensure a user does not act as both Client and Jeeber on the
    /// same delivery. A role switch is only permitted when no active
    /// deliveries exist in the current role.
    /// </summary>
    public string ActiveRole { get; set; } = "customer";

    /// <summary>When the active role was last switched (T-backend-041).</summary>
    public DateTimeOffset? RoleSwitchedAt { get; set; }
}

public class SavedAddress
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Label { get; set; }
    public required string Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
