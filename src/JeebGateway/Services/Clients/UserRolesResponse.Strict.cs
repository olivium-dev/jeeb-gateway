using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JeebGateway.service.ServiceUserManagement;

// Keep the generated owner DTO strict at the authentication boundary: Json.NET
// otherwise coerces numeric/bool identity and role fields into strings, losing
// the evidence that an owner response was malformed before refresh validates it.
[JsonConverter(typeof(StrictUserRolesResponseConverter))]
public partial class UserRolesResponse;

internal sealed class StrictUserRolesResponseConverter : JsonConverter<UserRolesResponse>
{
    public override bool CanWrite => false;

    public override UserRolesResponse? ReadJson(JsonReader reader, Type objectType,
        UserRolesResponse? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return null;
        var body = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        var roles = body["available_roles"];
        if (roles is not null && roles.Type != JTokenType.Null
            && (roles is not JArray || roles.Any(role => role.Type != JTokenType.String)))
            throw new JsonSerializationException("Invalid owner role response.");
        return new UserRolesResponse
        {
            UserId = ReadString(body["userId"]),
            Available_roles = roles is JArray array ? array.Select(role => role.Value<string>()!).ToArray() : null,
            Active_role = ReadString(body["active_role"]),
            Active_role_changed_at = body["active_role_changed_at"]?.ToObject<DateTimeOffset?>(),
            Active_role_changed_by = ReadString(body["active_role_changed_by"]),
        };
    }

    private static string? ReadString(JToken? value) => value?.Type switch
    {
        null or JTokenType.Null => null,
        JTokenType.String => value.Value<string>(),
        _ => throw new JsonSerializationException("Invalid owner role response."),
    };

    public override void WriteJson(JsonWriter writer, UserRolesResponse? value, JsonSerializer serializer) =>
        throw new NotSupportedException();
}
