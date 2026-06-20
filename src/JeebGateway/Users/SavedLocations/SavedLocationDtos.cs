using System.ComponentModel.DataAnnotations;

namespace JeebGateway.Users.SavedLocations;

public class SavedLocationResponse
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Label { get; init; }
    public string? Address { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required bool IsDefault { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public class SavedLocationsListResponse
{
    public required string UserId { get; init; }
    public required IReadOnlyList<SavedLocationResponse> Items { get; init; }
    /// <summary>REQ-02 convenience: id of the current default ("my location"), or null.</summary>
    public string? DefaultId { get; init; }
}

/// <summary>POST body — create a saved location (ACCT-04 add).</summary>
public class CreateSavedLocationRequest
{
    [Required, StringLength(80, MinimumLength = 1)]
    public string Label { get; set; } = string.Empty;

    [StringLength(256)]
    public string? Address { get; set; }

    [Range(-90, 90)]
    public double Latitude { get; set; }

    [Range(-180, 180)]
    public double Longitude { get; set; }

    /// <summary>REQ-02: mark this as the shared "my location" default on create.</summary>
    public bool IsDefault { get; set; }
}

/// <summary>
/// PUT/PATCH body — edit an existing saved location (ACCT-04 edit). Every field is
/// optional; unspecified fields are left untouched.
/// </summary>
public class UpdateSavedLocationRequest
{
    [StringLength(80, MinimumLength = 1)]
    public string? Label { get; set; }

    [StringLength(256)]
    public string? Address { get; set; }

    [Range(-90, 90)]
    public double? Latitude { get; set; }

    [Range(-180, 180)]
    public double? Longitude { get; set; }

    public bool? IsDefault { get; set; }
}
