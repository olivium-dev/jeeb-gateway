namespace JeebGateway.Availability;

/// <summary>
/// Mirrors the Postgres <c>jeeber_vehicle_type</c> enum (migration 0003).
/// Wire-format values are lowercase strings; never reorder.
/// </summary>
public enum VehicleType
{
    Car,
    Motorbike,
    Bicycle,
    Scooter,
    Walk
}

public static class VehicleTypeExtensions
{
    public static string ToWire(this VehicleType v) => v switch
    {
        VehicleType.Car => "car",
        VehicleType.Motorbike => "motorbike",
        VehicleType.Bicycle => "bicycle",
        VehicleType.Scooter => "scooter",
        VehicleType.Walk => "walk",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown vehicle type")
    };

    public static bool TryParseWire(string? raw, out VehicleType value)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "car": value = VehicleType.Car; return true;
            case "motorbike": value = VehicleType.Motorbike; return true;
            case "bicycle": value = VehicleType.Bicycle; return true;
            case "scooter": value = VehicleType.Scooter; return true;
            case "walk": value = VehicleType.Walk; return true;
            default: value = default; return false;
        }
    }
}
