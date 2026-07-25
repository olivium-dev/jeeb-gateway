using Newtonsoft.Json.Linq;
using Stj = System.Text.Json;

namespace JeebGateway.Push;

/// <summary>
/// P45 — the one-way bridge for an OPEN JSON shape that crosses the gateway's TWO
/// JSON stacks: bound by System.Text.Json (the ASP.NET model binder) and written by
/// Newtonsoft (the NSwag-generated upstream clients that declare
/// <c>jsonLibrary:Newtonsoft</c>).
///
/// <para>
/// THE HUSK BUG. A DTO property typed <c>object</c> receives a
/// <see cref="System.Text.Json.JsonElement"/> from the STJ model binder. Newtonsoft
/// has no knowledge of that struct, so it REFLECTS over it and puts its single
/// public CLR property on the wire — the upstream service receives
/// <c>{"payload":{"ValueKind":1}}</c> (or <c>{"valueKind":1}</c> once a camelCase
/// naming strategy is in play) instead of the caller's object. Every key the caller
/// sent is gone; only the enum tag survives. This is the exact class of defect P45
/// tracked in chat attachments, and the conversation BFF already immunises its own
/// legs with <see cref="JeebGateway.Conversations.Client.RawJsonElementConverter"/>.
/// </para>
///
/// <para>
/// THE FIX. Re-hydrate the element's RAW JSON into a Newtonsoft-native
/// <see cref="JToken"/> before it is handed to a Newtonsoft-serialized client DTO.
/// Newtonsoft writes a JToken as the JSON it represents, so the open shape crosses
/// the stack boundary verbatim. Values that are already Newtonsoft-native (or plain
/// CLR objects) are passed through untouched, so this is safe to apply at any
/// STJ → Newtonsoft hand-off.
/// </para>
///
/// This lives beside the callers rather than inside the NSwag-generated clients on
/// purpose: the generated files are regenerated from the upstream OpenAPI by
/// <c>scripts/regenerate-clients.sh</c> and must stay untouched.
/// </summary>
public static class UpstreamJsonPayload
{
    /// <summary>
    /// Returns <paramref name="payload"/> in a form Newtonsoft can serialize
    /// faithfully. A <see cref="System.Text.Json.JsonElement"/> becomes the
    /// equivalent <see cref="JToken"/>; a JSON <c>null</c>/undefined becomes
    /// <see langword="null"/>; anything else is returned unchanged.
    /// </summary>
    public static object? ToNewtonsoftSafe(object? payload)
    {
        if (payload is not Stj.JsonElement element)
        {
            return payload;
        }

        return element.ValueKind switch
        {
            Stj.JsonValueKind.Undefined or Stj.JsonValueKind.Null => null,
            // Parse the RAW text — never reflect over the JsonElement struct.
            _ => JToken.Parse(element.GetRawText()),
        };
    }
}
