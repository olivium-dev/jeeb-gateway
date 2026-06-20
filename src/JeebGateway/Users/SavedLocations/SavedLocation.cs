namespace JeebGateway.Users.SavedLocations;

/// <summary>
/// A user's saved address / pinned location (ACCT-04, REQ-02). Gateway-thin
/// in-memory model — no DbContext. When the geolocation-service saved-locations
/// upstream lands, swap <see cref="ISavedLocationStore"/> for an NSwag-backed,
/// flag-gated remote implementation.
/// </summary>
public class SavedLocation
{
    public required string Id { get; init; }
    public required string UserId { get; init; }

    /// <summary>User-facing label, e.g. "Home", "Work".</summary>
    public required string Label { get; set; }

    /// <summary>Free-form address line shown in the UI.</summary>
    public string? Address { get; set; }

    public required double Latitude { get; set; }
    public required double Longitude { get; set; }

    /// <summary>
    /// REQ-02: the shared "my location" default. Exactly one saved location per
    /// user may be the default; setting a new default clears the previous one.
    /// </summary>
    public bool IsDefault { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
