using System.Text.Json;

namespace JeebGateway.FormSubmissions;

public static class HomeBaseRules
{
    public static (string Field, string Detail)? Validate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return ("home_base", "Must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return ("home_base." + property.Name, "Duplicate field.");
            if (property.Name is not ("lat" or "lng" or "label"))
                return ("home_base." + property.Name, "Unknown location field.");
        }
        foreach (var (field, limit) in new[] { ("lat", 90d), ("lng", 180d) })
        {
            if (!value.TryGetProperty(field, out var coordinate) ||
                coordinate.ValueKind != JsonValueKind.Number ||
                !coordinate.TryGetDouble(out var number) || !double.IsFinite(number) ||
                number < -limit || number > limit)
                return ($"home_base.{field}", "Must be a finite coordinate within range.");
        }
        if (value.TryGetProperty("label", out var label) &&
            (label.ValueKind != JsonValueKind.String || label.GetString()!.Length > 256))
            return ("home_base.label", "Must be a string of at most 256 characters.");
        return null;
    }
}
