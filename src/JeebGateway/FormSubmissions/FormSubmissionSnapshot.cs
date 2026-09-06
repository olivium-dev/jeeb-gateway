using System.Globalization;
using System.Text.Json;
using JeebGateway.Users;

namespace JeebGateway.FormSubmissions;

public static class FormSubmissionSnapshot
{
    private static readonly string[] Fields = ["schema_version", "submitted_at", "zone_key", "answers"];

    public static Dictionary<string, string> Encode(FormSubmissionRecord record)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema_version"] = "1",
            ["submitted_at"] = record.SubmittedAt.ToString("O", CultureInfo.InvariantCulture),
            ["zone_key"] = record.ZoneKey ?? "",
            ["answers"] = JsonSerializer.Serialize(record.Answers)
        };
        _ = Decode(snapshot);
        return snapshot;
    }

    public static FormSubmissionRecord Decode(IDictionary<string, string>? snapshot)
    {
        if (snapshot is null || snapshot.Count != Fields.Length ||
            Fields.Any(key => !snapshot.TryGetValue(key, out var value) || value is null) ||
            snapshot["schema_version"] != "1" ||
            !DateTimeOffset.TryParseExact(snapshot["submitted_at"], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var submittedAt) ||
            (snapshot["zone_key"].Length > 0 && string.IsNullOrWhiteSpace(snapshot["zone_key"])))
            throw InvalidSnapshot();
        try
        {
            using var parsed = JsonDocument.Parse(snapshot["answers"]);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw InvalidSnapshot();
            var answers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in parsed.RootElement.EnumerateObject())
                if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.String ||
                    !answers.TryAdd(property.Name, property.Value.GetString()!)) throw InvalidSnapshot();
            if (answers.Count == 0) throw InvalidSnapshot();
            return new FormSubmissionRecord(answers, submittedAt,
                snapshot["zone_key"].Length == 0 ? null : snapshot["zone_key"]);
        }
        catch (JsonException) { throw InvalidSnapshot(); }
    }

    private static UserPreferencesUnavailableException InvalidSnapshot() =>
        new("Stored submission snapshot is unsupported or malformed.");
}
