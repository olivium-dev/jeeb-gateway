using System.Text.Json;

namespace JeebGateway.FormSubmissions;

public static class TemplateSchemaValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyDictionary<string, string> Types(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("components", out var components) ||
            components.ValueKind != JsonValueKind.Array || components.GetArrayLength() == 0)
            throw new JsonException("Invalid form schema.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object ||
                !component.TryGetProperty("componentID", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) ||
                !component.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() is not ("string" or "object") ||
                !result.TryAdd(id.GetString()!, type.GetString()!))
                throw new JsonException("Invalid form component.");
        }
        return result;
    }

    public static (string Field, string Detail)? Validate(JsonElement schema, JsonElement document, JsonElement body)
    {
        var types = Types(schema);
        var limits = DefinitionLimits(schema, document, types);
        if (body.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid submission object.");
        foreach (var key in RequiredFields(schema, types))
        {
            if (!body.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null ||
                (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
                return (key, "This field is required.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return (property.Name, "Duplicate field.");
            if (!types.TryGetValue(property.Name, out var type)) return (property.Name, "Unknown field.");
            if (type == "string")
            {
                if (property.Value.ValueKind != JsonValueKind.String) return (property.Name, "Must be a string.");
                if (property.Value.GetString()!.Length > limits[property.Name])
                    return (property.Name, "Value exceeds the permitted length.");
            }
            else if (property.Value.ValueKind != JsonValueKind.Object)
                return (property.Name, "Must be an object.");
            if (property.Name == "home_base" && HomeBaseRules.Validate(property.Value) is { } error) return error;
        }
        return null;
    }

    private static HashSet<string> RequiredFields(JsonElement schema, IReadOnlyDictionary<string, string> types)
    {
        if (!schema.TryGetProperty("required_fields", out var required) || required.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid required fields.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in required.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || !types.ContainsKey(entry.GetString()!) ||
                !fields.Add(entry.GetString()!))
                throw new JsonException("Invalid required field.");
        }
        return fields;
    }

    private static IReadOnlyDictionary<string, int> DefinitionLimits(JsonElement schema, JsonElement document,
        IReadOnlyDictionary<string, string> types)
    {
        if (document.ValueKind != JsonValueKind.Array || document.GetArrayLength() != types.Count)
            throw new JsonException("Incomplete template document.");
        var required = RequiredFields(schema, types);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var templateRequired = new HashSet<string>(StringComparer.Ordinal);
        var limits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var component in document.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object ||
                !component.TryGetProperty("componentID", out var field) || field.ValueKind != JsonValueKind.String ||
                !types.TryGetValue(field.GetString()!, out var type) || !seen.Add(field.GetString()!) ||
                !component.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object ||
                !output.TryGetProperty("type", out var outputType) || outputType.ValueKind != JsonValueKind.String ||
                outputType.GetString() != type ||
                !component.TryGetProperty("validations", out var rules) || rules.ValueKind != JsonValueKind.Array)
                throw new JsonException("Template does not match its schema.");
            var id = field.GetString()!;
            int? maxLength = null;
            var ruleNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("rule", out var kind) ||
                    kind.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(kind.GetString()) ||
                    !ruleNames.Add(kind.GetString()!)) throw new JsonException("Invalid validations.");
                if (kind.GetString() == "required") templateRequired.Add(id);
                if (kind.GetString() == "maxLength")
                {
                    if (type != "string" || !rule.TryGetProperty("value", out var value) ||
                        value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var limit) || limit < 0)
                        throw new JsonException("Invalid maximum length.");
                    maxLength = limit;
                }
            }
            limits[id] = maxLength ?? 256;
        }
        if (!required.SetEquals(templateRequired)) throw new JsonException("Template required fields disagree with schema.");
        foreach (var component in schema.GetProperty("components").EnumerateArray())
            if (component.TryGetProperty("required", out var flag) &&
                (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                 flag.GetBoolean() != required.Contains(component.GetProperty("componentID").GetString()!)))
                throw new JsonException("Inconsistent schema required field.");
        return limits;
    }

    public static IReadOnlyDictionary<string, string> ToAnswers(JsonElement body) =>
        body.EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! :
                JsonSerializer.Serialize(p.Value, JsonOptions), StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, object?> Restore(JsonElement schema, JsonElement document,
        IReadOnlyDictionary<string, string> answers)
    {
        var types = Types(schema);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in answers)
        {
            if (!types.TryGetValue(key, out var type)) throw new JsonException("Stored field absent from schema.");
            if (value is null) throw new JsonException("Stored field is null.");
            if (type == "string") result[key] = value;
            else
            {
                using var parsed = JsonDocument.Parse(value);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Stored object is malformed.");
                result[key] = parsed.RootElement.Clone();
            }
        }
        if (Validate(schema, document, JsonSerializer.SerializeToElement(result, JsonOptions)) is not null)
            throw new JsonException("Stored submission violates its form definition.");
        return result;
    }
}
