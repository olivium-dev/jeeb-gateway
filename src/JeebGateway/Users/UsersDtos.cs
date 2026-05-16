namespace JeebGateway.Users;

public class UserProfileResponse
{
    public required string Id { get; init; }
    public required string Phone { get; init; }
    public string? Email { get; init; }
    public required string Name { get; init; }
    public string? AvatarUrl { get; init; }
    public required string Language { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }
    public decimal? Rating { get; init; }
    public required int RatingCount { get; init; }
    public required IReadOnlyList<SavedAddressResponse> SavedAddresses { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    // T-backend-030. Surfaced on GET /users/me so the mobile app can show
    // the "Your account is suspended — reason: …" banner without a
    // dedicated lookup.
    public required bool IsSuspended { get; init; }
    public string? SuspensionReason { get; init; }
    public DateTimeOffset? SuspendedAt { get; init; }
    /// <summary>
    /// BR-10 / T-backend-039 acceptance criterion: true when this user has
    /// never been rated (<see cref="RatingCount"/> = 0). The mobile app
    /// renders a "New" badge in place of a numeric rating; without this
    /// flag the front-end would have to special-case "rating is null but
    /// they could still have a hidden 0 rating".
    /// </summary>
    public required bool IsNew { get; init; }
}

public class SavedAddressResponse
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Line1 { get; init; }
    public string? Line2 { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public required bool IsDefault { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// PATCH /users/me — every field is optional; only the fields present in
/// the JSON body are modified. Roles, rating, phone, and id are NOT
/// mutable here; roles are managed by admin endpoints and rating by the
/// score-taking-service.
/// </summary>
public class UpdateProfileRequest
{
    public string? Name { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Language { get; set; }
    public string? Email { get; set; }
}

public class SavedAddressUpsertRequest
{
    public string? Label { get; set; }
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public bool? IsDefault { get; set; }
}

public class AdminUserSearchResultItem
{
    public required string Id { get; init; }
    public required string Phone { get; init; }
    public string? Email { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }
    public decimal? Rating { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>
    /// True when the user has never been rated. Drives the "New" badge
    /// in admin rosters next to the Jeeber's rating column.
    /// </summary>
    public required bool IsNew { get; init; }
}

public class AdminUserSearchResponse
{
    public required IReadOnlyList<AdminUserSearchResultItem> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}

public class SuspendUserRequest
{
    public string? Reason { get; set; }
}

public class SuspendUserResponse
{
    public required string UserId { get; init; }
    public required bool IsSuspended { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset SuspendedAt { get; init; }
    public required string SuspendedBy { get; init; }
    public required int RevokedTokenCount { get; init; }
}

public class UnsuspendUserResponse
{
    public required string UserId { get; init; }
    public required bool IsSuspended { get; init; }
    public required DateTimeOffset UnsuspendedAt { get; init; }
    public required string UnsuspendedBy { get; init; }
}

/// <summary>
/// DELETE /users/me / GET /users/me/deletion response. Status drives the
/// mobile UI between "your data will be deleted on …" and
/// "we're waiting for your delivery to complete first". <c>ScheduledPurgeAt</c>
/// is null while we wait for active deliveries to finish.
/// </summary>
public class AccountDeletionResponse
{
    public required string UserId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? ScheduledPurgeAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
