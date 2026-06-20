namespace JeebGateway.Users.SavedLocations;

/// <summary>
/// Storage abstraction for per-user saved locations (ACCT-04 / REQ-02).
/// Gateway-thin: the only implementation today is
/// <see cref="InMemorySavedLocationStore"/>. There is no upstream
/// saved-locations surface yet, so this is net-new and in-memory. When the
/// geolocation-service / user-management upstream ships, add a remote,
/// NSwag-backed store behind a feature flag (default OFF) with a 503
/// kill-switch — never call a backend by hand-rolled HttpClient.
/// </summary>
public interface ISavedLocationStore
{
    Task<IReadOnlyList<SavedLocation>> ListAsync(string userId, CancellationToken ct);

    Task<SavedLocation?> GetAsync(string userId, string id, CancellationToken ct);

    Task<SavedLocation> CreateAsync(
        string userId,
        CreateSavedLocationRequest request,
        CancellationToken ct);

    /// <summary>Returns null when no location with that id exists for the user.</summary>
    Task<SavedLocation?> UpdateAsync(
        string userId,
        string id,
        UpdateSavedLocationRequest request,
        CancellationToken ct);

    /// <summary>Returns false when no location with that id exists for the user.</summary>
    Task<bool> DeleteAsync(string userId, string id, CancellationToken ct);
}
