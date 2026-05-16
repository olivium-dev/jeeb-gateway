namespace JeebGateway.Availability;

/// <summary>
/// PATCH body for /jeebers/me/availability. <see cref="Online"/> is
/// required; the other fields are required only when going online so
/// the matching service has a vehicle and zone to broadcast.
/// </summary>
public class AvailabilityPatchRequest
{
    public bool? Online { get; set; }
    public string? VehicleType { get; set; }
    public string? Zone { get; set; }
    public double? Longitude { get; set; }
    public double? Latitude { get; set; }
}

public class AvailabilityResponse
{
    public required string UserId { get; init; }
    public required bool Online { get; init; }
    public required string VehicleType { get; init; }
    public string? Zone { get; init; }
    public double? Longitude { get; init; }
    public double? Latitude { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public DateTimeOffset? LastInteractionAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// How many in-flight offers were withdrawn as a result of this
    /// transition. Zero on go-online and on no-op offline calls.
    /// </summary>
    public int WithdrawnOffers { get; init; }
}
