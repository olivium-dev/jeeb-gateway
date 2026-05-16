namespace JeebGateway.Availability;

/// <summary>
/// Per-Jeeber availability state. Mirrors the durable
/// <c>jeeber_availability</c> Postgres row, with the additional
/// <see cref="LastInteractionAt"/> watermark used by the auto-offline
/// sweeper (T-backend-023). The Postgres heartbeat column drives the
/// matching service; <see cref="LastInteractionAt"/> drives presence.
/// </summary>
public class JeeberAvailability
{
    public required string UserId { get; init; }
    public bool IsOnline { get; set; }
    public VehicleType VehicleType { get; set; }
    public string? Zone { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// Most recent user-driven event — go-online, GPS heartbeat, or any
    /// other UI interaction. The auto-offline sweeper compares against
    /// this to decide who to flip offline.
    /// </summary>
    public DateTimeOffset? LastInteractionAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
